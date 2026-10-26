"""S5 stream tools for the stand-ins and the S5 analyzer (Python 3.10 stdlib; ROADMAP S5 "tools/tests", CONTRACT §5.4
"C1–C4 conventions for S5" and "Stream recording (.derec, tests only)").

 python -I tests/standins/s5_stream.py selftest # conventions vectors,.derec round trip, a synth
 # ship validated as the Subnautica side reads it
 python -I tests/standins/s5_stream.py info <name | file.derec> # header, counts per kind, sidecar check
 python -I tests/standins/s5_stream.py synth 500|2000|5000 [--name NAME] [--seed N] [--content DIR]
 # writes %LOCALAPPDATA%\\DeepEngineers\\recordings\\NAME.derec

What lives here (one module so the fake producer, the analyzer and the launcher share it, independent of Wire's C#):
- the S5 mirror conventions twin: flags, the canonical text and its FNV-1a 64 (0x31dfd6f9befdef5d), the key functions
 (modelKey text, partKey cube/edge/fat/subpart, skinId) checked against the contract's example vectors. It reads only
 tests/wire/layout_py.py (record layouts) and tools/worldmap/wire_ref.py (S3 model and material keys);
- `.derec` recording files: writer (64 B header, 24 B record headers, 8-byte padding, end record, JSON sidecar with the
 sha256) and reader (a file without the end record is read to its last whole record);
- a read-only `.mwm` reader
 and the C3 export
 of one model the way the SE plugin's ModelLoader does it (56 B vertices, MyModel.Triangles order = the file order with
 the 2nd and 3rd index swapped, one submesh per mesh part, MaterialDef with absolute paths under Content);
- the synthetic ship of the S5 stand-in (`Ship`): N blocks of SE's real models read in place from the user's install
 (202 large armor models, a cockpit, a door with two dynamic subparts, a rotor whose top is a subgrid turning at 10
 rpm, a piston with three dynamic subparts and a top subgrid, a small docked subgrid holding the census-listed inverted
 model InsetConnectorConstruction_1 as a build stage; RoundCornerTriangleRightPlate is one of the armor models),
 instanced with 24 rotations and a colour palette, its S5 records (GridAdd/PartAdd with the version nibble), its C2
 rows per tick, its live edits (weld, stage, damage, paint, grind) and its counts.
Nothing is copied from the game: models are read where they are; recordings are derived data under %LOCALAPPDATA%.
"""
from __future__ import annotations

import hashlib
import json
import math
import os
import struct
import sys
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_HERE, "..", ".."))
for _p in (_HERE, os.path.join(_REPO, "tests", "wire"), os.path.join(_REPO, "tools", "worldmap")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import layout_py as L  # noqa: E402
import wire_ref as W  # noqa: E402

SE_CONTENT = os.environ.get("DEEPENG_SE_CONTENT") or \
    r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content"
LOCAL = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers")
RECORDINGS = os.path.join(LOCAL, "recordings")

# ======================================================================================================================
# S5 mirror conventions (CONTRACT §5.4; Wire MirrorConventions is the C# side, this is an independent twin)
# ======================================================================================================================

CONVENTIONS_HASH = 0x31dfd6f9befdef5d
SKELETON_HASH = 0x995765282e7d4f1d
CANONICAL_TEXT = (
    "ext s5 mirror 1\n"
    "const MirrorCharacterFlags Player 1\n"
    "const MirrorCharacterFlags Fallback 2\n"
    "const MirrorCharacterFlags Version1 268435456\n"
    "const MirrorCharacterPart Body 1\n"
    "const MirrorCharacterPart Head1st 2\n"
    "const MirrorCharacterPart Tool 3\n"
    "const MirrorGridFlags Static 1\n"
    "const MirrorGridFlags Subgrid 2\n"
    "const MirrorGridFlags FloatingObject 4\n"
    "const MirrorGridFlags Version1 268435456\n"
    "const MirrorLimits MaxSkinWeights 4\n"
    "const MirrorLimits CanonicalBones 78\n"
    "const MirrorPartClass CubePart 1\n"
    "const MirrorPartClass Edge 2\n"
    "const MirrorPartClass FatBlock 3\n"
    "const MirrorPartClass Subpart 4\n"
    "const MirrorPartClass CharacterBody 5\n"
    "const MirrorPartClass Tool 6\n"
    "const MirrorPartClass FloatingObject 7\n"
    "const MirrorPartFlags ClassMask 15\n"
    "const MirrorPartFlags Dynamic 16\n"
    "const MirrorPartFlags HiddenInFirstPerson 32\n"
    "const MirrorPartFlags BuildStage 64\n"
    "const MirrorPartFlags Deformed 128\n"
    "const MirrorPartFlags NoShadows 256\n"
    "const MirrorPartFlags MetalnessColorable 512\n"
    "const MirrorPartFlags Version1 268435456\n"
    "const MirrorPartFlags VersionMask 4026531840\n"
    "format modelKey <path>|lod<n>[|scale=<s>][|skin=<skin>][|char=body|char=head1st]\n"
    "format partKey.cube fnv1a64(u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)\n"
    "format partKey.edge fnv1a64(u32 2, u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)\n"
    "format partKey.fat fnv1a64(utf8 fat|<minX>,<minY>,<minZ>)\n"
    "format partKey.subpart fnv1a64(utf8 sub|<fatKey hex16>|<name>[/<name>])\n"
    "format partKey.floating 1\n"
    "format skinId fnv1a32(utf8 lower(<skin>)) or 0\n"
    "skeleton canon1 78 0x995765282e7d4f1d\n")

CHAR_PLAYER, CHAR_FALLBACK, CHAR_VERSION1 = 1, 2, 1 << 28
CHAR_BODY, CHAR_HEAD1ST, CHAR_TOOL = 1, 2, 3
GRID_STATIC, GRID_SUBGRID, GRID_FLOATING, GRID_VERSION1 = 1, 2, 4, 1 << 28
CLS_CUBE, CLS_EDGE, CLS_FAT, CLS_SUBPART, CLS_CHAR, CLS_TOOL, CLS_FLOATING = 1, 2, 3, 4, 5, 6, 7
PF_CLASS_MASK, PF_DYNAMIC, PF_HIDDEN_1ST, PF_BUILD_STAGE, PF_DEFORMED = 15, 16, 32, 64, 128
PF_NO_SHADOWS, PF_METALNESS, PF_VERSION1, PF_VERSION_MASK = 256, 512, 1 << 28, 0xF0000000
CLASS_NAMES = {1: "cube", 2: "edge", 3: "fat", 4: "subpart", 5: "character", 6: "tool", 7: "floating"}
CANONICAL_BONES = 78
SKELETON_TSV = os.path.join(_REPO, "src", "DeepEngineers.Wire", "Data", "SeSkeleton.canon1.tsv")

FNV64_OFFSET, FNV64_PRIME = 0xcbf29ce484222325, 0x100000001b3
FNV32_OFFSET, FNV32_PRIME = 0x811c9dc5, 0x01000193

def fnv1a64(data: bytes, h: int = FNV64_OFFSET) -> int:
    for b in data:
        h = ((h ^ b) * FNV64_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h

def fnv1a32(data: bytes, h: int = FNV32_OFFSET) -> int:
    for b in data:
        h = ((h ^ b) * FNV32_PRIME) & 0xFFFFFFFF
    return h

def model_key_text(path: str, lod: int = 0, scale: float = 1.0, skin: str = None, char: str = None) -> str:
    """'<path>|lod<n>[|scale=<s>][|skin=<skin>][|char=body|char=head1st]' (path normalised as S3: trimmed, / -> \\,
 lower-case; scale as.NET float.ToString("R") when not 1; skin lower-case)."""
    text = W.normalize_model_path(path) + "|lod" + str(int(lod))
    if W.f32(scale) != 1.0:
        text += "|scale=" + W.net_float_r(scale)
    if skin:
        text += "|skin=" + skin.lower()
    if char:
        text += "|char=" + char
    return text

def model_key(path: str, lod: int = 0, scale: float = 1.0, skin: str = None, char: str = None) -> int:
    return fnv1a64(model_key_text(path, lod, scale, skin, char).encode("utf-8"))

def cube_part_key(model_key_: int, tx: float, ty: float, tz: float, packed_rotation: float) -> int:
    return fnv1a64(struct.pack("<Qffff", model_key_, tx, ty, tz, packed_rotation))

def edge_part_key(model_key_: int, tx: float, ty: float, tz: float, packed_rotation: float) -> int:
    return fnv1a64(struct.pack("<IQffff", 2, model_key_, tx, ty, tz, packed_rotation))

def fat_part_key(min_x: int, min_y: int, min_z: int) -> int:
    return fnv1a64(("fat|%d,%d,%d" % (min_x, min_y, min_z)).encode("utf-8"))

def subpart_key(fat_key: int, names) -> int:
    path = names if isinstance(names, str) else "/".join(names)
    return fnv1a64(("sub|%016x|%s" % (fat_key, path)).encode("utf-8"))

def skin_id(skin: str) -> int:
    return fnv1a32(skin.lower().encode("utf-8")) if skin else 0

def part_class(flags: int) -> int:
    return flags & PF_CLASS_MASK

def version_nibble(flags: int) -> int:
    return (flags >> 28) & 0xF

def skeleton_text(path: str = SKELETON_TSV) -> str:
    """'bone <i> <seBone> <parent>\\n' per row (columns 1-3 of the canon1 table; CONTRACT §5.4)."""
    out = []
    header = None
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line or line.startswith("#"):
                continue
            cols = line.split("\t")
            if header is None:
                header = cols
                continue
            out.append("bone %s %s %s\n" % (cols[0], cols[1], cols[2]))
    return "".join(out)

EXAMPLE_VECTORS = (
    ("fat|-3,0,12", lambda: fat_part_key(-3, 0, 12), 0x5148adb433ae18cf),
    ("sub DoorLeft", lambda: subpart_key(0x5148adb433ae18cf, "DoorLeft"), 0x09b78ce1c6718cc7),
    ("sub TurretBase/TurretBarrel", lambda: subpart_key(0x5148adb433ae18cf, ["TurretBase", "TurretBarrel"]),
     0xf4a128d807ca725c),
    ("skinId clean_armor", lambda: skin_id("Clean_Armor"), 0xfded4532),
    # the contract tool's table gives 0xf18ee13d1b3e0dec for this text: that is the hash of the same text with "\a"
 # read as the BEL character (an unescaped Python literal "...large\armorcube..."); the documented text hashes to
 # 0xff3f6e15d5fff26a (wire_ref.model_key agrees). The cube and edge vectors below take 0xf18ee13d1b3e0dec as a given
 # u64, so they stay valid as written.
    ("modelKey armorcube", lambda: model_key(r"Models\Cubes\Large\ArmorCube.mwm"), 0xff3f6e15d5fff26a),
    ("modelKey armorcube (BEL text of the table)", lambda: fnv1a64(b"models\\cubes\\large\x07rmorcube.mwm|lod0"),
     0xf18ee13d1b3e0dec),
    ("cube key", lambda: cube_part_key(0xf18ee13d1b3e0dec, 2.5, 0.0, -5.0, 0.0), 0xb59d1fc7857bd192),
    ("edge key", lambda: edge_part_key(0xf18ee13d1b3e0dec, 2.5, 0.0, -5.0, 0.0), 0xd5f1574eb7e10570),
    # with the real model key
    ("cube key (real mk)", lambda: cube_part_key(0xff3f6e15d5fff26a, 2.5, 0.0, -5.0, 0.0), 0xeb6d37388d73d6d4),
    ("edge key (real mk)", lambda: edge_part_key(0xff3f6e15d5fff26a, 2.5, 0.0, -5.0, 0.0), 0x3dab9da5885325a6),
)

def conventions_problems() -> list:
    """[] when the twin matches the contract: the text hash, its shape, the example vectors, the skeleton hash."""
    bad = []
    t = CANONICAL_TEXT.encode("ascii")
    if fnv1a64(t) != CONVENTIONS_HASH:
        bad.append("canonical text hash 0x%016x != 0x%016x" % (fnv1a64(t), CONVENTIONS_HASH))
    if len(t) != 1539 or t.count(b"\n") != 37:
        bad.append("canonical text %d bytes / %d lines (contract: 1,539 / 37)" % (len(t), t.count(b"\n")))
    for name, fn, want in EXAMPLE_VECTORS:
        got = fn()
        if got != want:
            bad.append("%s 0x%x != 0x%x" % (name, got, want))
    if os.path.exists(SKELETON_TSV):
        st = skeleton_text()
        if fnv1a64(st.encode("utf-8")) != SKELETON_HASH or st.count("\n") != CANONICAL_BONES:
            bad.append("skeleton canon1 hash 0x%016x / %d rows" % (fnv1a64(st.encode("utf-8")), st.count("\n")))
    else:
        bad.append("skeleton table %s absent" % SKELETON_TSV)
    # the Wire build pass Python twin (tools/worldmap/wire_ref.py s5_* functions), when present: the same keys and text
    if hasattr(W, "s5_model_key") and hasattr(W, "s5_conv_text"):
        try:
            if W.s5_model_key(r"Models\Cubes\Large\ArmorCube.mwm", 0) != model_key(r"Models\Cubes\Large\ArmorCube.mwm"):
                bad.append("wire_ref.s5_model_key differs")
            if W.s5_model_key("a.mwm", 0, 1.0, "Clean_Armor", 1) != model_key("a.mwm", 0, 1.0, "Clean_Armor", "body"):
                bad.append("wire_ref.s5_model_key (skin, char) differs")
            if W.s5_conv_text() != CANONICAL_TEXT:
                bad.append("wire_ref.s5_conv_text differs from this twin's canonical text")
        except Exception as e:   # noqa: BLE001 - an API change there is reported, not fatal
            bad.append("wire_ref s5 twin not comparable: %s" % e)
    for fl in (PF_DYNAMIC, PF_HIDDEN_1ST, PF_BUILD_STAGE, PF_DEFORMED, PF_NO_SHADOWS, PF_METALNESS):
        if fl & (PF_CLASS_MASK | PF_VERSION_MASK):
            bad.append("flag %d overlaps the class or version field" % fl)
    return bad

# ======================================================================================================================
#.derec recordings (CONTRACT §5.4 "Stream recording")
# ======================================================================================================================

DEREC_MAGIC = b"DEREC001"
DEREC_HEADER = struct.Struct("<8sIIQQIIQIIQ")         # 64 B
DEREC_RECORD = struct.Struct("<HHIQq")                # 24 B
KIND_C3, KIND_C4, KIND_C2, KIND_C1, KIND_MARKER, KIND_END = 1, 2, 3, 4, 5, 0xFFFF
KIND_NAMES = {KIND_C3: "c3", KIND_C4: "c4", KIND_C2: "c2", KIND_C1: "c1", KIND_MARKER: "marker", KIND_END: "end"}
DF_C1, DF_C2, DF_SYNTHETIC = 1, 2, 4
SE_VERSION_INSTALLED = 1210014
assert DEREC_HEADER.size == 64 and DEREC_RECORD.size == 24

def framed(mtype: int, payload: bytes) -> bytes:
    """A C3/C4 message as written to the ring ({u32 type, u32 payloadBytes} + payload, unpadded)."""
    return struct.pack("<II", mtype, len(payload)) + payload

def unframe(blob: bytes):
    mtype, n = struct.unpack_from("<II", blob, 0)
    if n != len(blob) - 8:
        raise ValueError("framed message: payloadBytes %d for %d bytes" % (n, len(blob) - 8))
    return mtype, blob[8:]

def recording_path(name: str) -> str:
    return os.path.join(RECORDINGS, name + ".derec")

def resolve_recording(arg: str) -> str:
    """A.derec path, a folder (the newest.derec in it), or a name under %LOCALAPPDATA%\\DeepEngineers\\recordings."""
    if not arg:
        raise FileNotFoundError("no recording given")
    if os.path.isfile(arg):
        return arg
    if os.path.isdir(arg):
        files = sorted((os.path.join(arg, f) for f in os.listdir(arg) if f.lower().endswith(".derec")),
                       key=os.path.getmtime)
        if not files:
            raise FileNotFoundError("no .derec in %s" % arg)
        return files[-1]
    p = recording_path(arg[:-6] if arg.lower().endswith(".derec") else arg)
    if os.path.isfile(p):
        return p
    raise FileNotFoundError("recording %s not found (looked for %s)" % (arg, p))

class DerecWriter:
    """Writes one.derec file and its JSON sidecar. Records are kept in order; `close` writes the end record."""

    def __init__(self, path: str, synthetic: bool = False, se_version: int = 0, world_epoch: int = 0,
                 has_c1: bool = False, has_c2: bool = False, created_ms: int = None):
        self.path = path
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        self.f = open(path, "wb")
        self.flags = (DF_C1 if has_c1 else 0) | (DF_C2 if has_c2 else 0) | (DF_SYNTHETIC if synthetic else 0)
        self.se_version = 0 if synthetic else int(se_version)
        self.world_epoch = int(world_epoch)
        self.created = int(created_ms if created_ms is not None else time.time() * 1000)
        self.f.write(DEREC_HEADER.pack(DEREC_MAGIC, 1, 64, L.layout_hash(), CONVENTIONS_HASH, L.WIRE_VERSION,
                                       self.se_version, self.created, self.flags, self.world_epoch, 0))
        self.counts = {}
        self.bytes = 64
        self.first_tick = None
        self.last_tick = None
        self.models, self.grids, self.parts = set(), set(), 0
        self.dropped = 0

    def add(self, kind: int, tick: int, payload: bytes, qpc: int = 0):
        pad = (-len(payload)) % 8
        self.f.write(DEREC_RECORD.pack(kind, 0, len(payload), int(tick), int(qpc)))
        self.f.write(payload)
        if pad:
            self.f.write(b"\0" * pad)
        self.bytes += 24 + len(payload) + pad
        self.counts[KIND_NAMES.get(kind, str(kind))] = self.counts.get(KIND_NAMES.get(kind, str(kind)), 0) + 1
        if kind != KIND_END:
            self.first_tick = tick if self.first_tick is None else min(self.first_tick, tick)
            self.last_tick = tick if self.last_tick is None else max(self.last_tick, tick)
        if kind in (KIND_C3, KIND_C4) and len(payload) >= 8:
            mtype, _n = struct.unpack_from("<II", payload, 0)
            if kind == KIND_C3 and mtype == L.C3_TYPES["ModelBegin"]:
                self.models.add(struct.unpack_from("<Q", payload, 8)[0])
            elif kind == KIND_C4 and mtype == L.C4_TYPES["GridAdd"]:
                self.grids.add(struct.unpack_from("<Q", payload, 8)[0])
            elif kind == KIND_C4 and mtype == L.C4_TYPES["PartAdd"]:
                self.parts += 1

    def message(self, ring: str, mtype: int, payload: bytes, tick: int, qpc: int = 0):
        self.add(KIND_C3 if ring == "C3" else KIND_C4, tick, framed(mtype, payload), qpc)

    def marker(self, obj: dict, tick: int, qpc: int = 0):
        self.add(KIND_MARKER, tick, json.dumps(obj, separators=(",", ":")).encode("utf-8"), qpc)

    def close(self, name: str = None) -> dict:
        self.add(KIND_END, self.last_tick or 0, b"")
        self.f.close()
        side = {"name": name or os.path.splitext(os.path.basename(self.path))[0], "formatVersion": 1,
                "created": self.created, "layoutHash": "0x%016x" % L.layout_hash(),
                "conventionsHash": "0x%016x" % CONVENTIONS_HASH, "seVersion": self.se_version,
                "synthetic": bool(self.flags & DF_SYNTHETIC), "firstTick": self.first_tick, "lastTick": self.last_tick,
                "counts": self.counts, "bytes": self.bytes, "models": len(self.models), "grids": len(self.grids),
                "parts": self.parts, "dropped": self.dropped, "sha256": sha256_file(self.path)}
        with open(os.path.splitext(self.path)[0] + ".json", "w", encoding="utf-8") as f:
            json.dump(side, f, indent=1)
        return side

def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for blk in iter(lambda: f.read(1 << 20), b""):
            h.update(blk)
    return h.hexdigest()

def read_header(blob: bytes) -> dict:
    if len(blob) < 64:
        raise ValueError("file shorter than the 64 B header")
    (magic, fmt, hdr_bytes, lh, ch, wv, sev, created, flags, epoch, _res) = DEREC_HEADER.unpack_from(blob, 0)
    if magic != DEREC_MAGIC:
        raise ValueError("magic %r (expected DEREC001)" % magic)
    if fmt != 1 or hdr_bytes != 64:
        raise ValueError("formatVersion %d headerBytes %d (expected 1, 64)" % (fmt, hdr_bytes))
    return {"formatVersion": fmt, "headerBytes": hdr_bytes, "layoutHash": lh, "conventionsHash": ch,
            "wireVersion": wv, "seVersion": sev, "createdUnixMs": created, "flags": flags, "worldEpoch": epoch,
            "hasC1": bool(flags & DF_C1), "hasC2": bool(flags & DF_C2), "synthetic": bool(flags & DF_SYNTHETIC)}

def read_derec(path: str):
    """(header dict, [(kind, seTick, qpc, payload bytes)], ended) - records up to the end record or the last whole one."""
    with open(path, "rb") as f:
        blob = f.read()
    hdr = read_header(blob)
    recs, pos, ended = [], 64, False
    while pos + 24 <= len(blob):
        kind, _pad, n, tick, qpc = DEREC_RECORD.unpack_from(blob, pos)
        end = pos + 24 + n
        if end > len(blob):
            break
        if kind == KIND_END:
            ended = True
            break
        recs.append((kind, tick, qpc, blob[pos + 24:end]))
        pos = end + ((-n) % 8)
    return hdr, recs, ended

def recording_problems(path: str) -> list:
    """[] when a recording can be replayed by this build: header hashes, framing of every C3/C4 record, the sidecar."""
    bad = []
    try:
        hdr, recs, ended = read_derec(path)
    except (OSError, ValueError) as e:
        return ["unreadable: %s" % e]
    if hdr["layoutHash"] != L.layout_hash():
        bad.append("layoutHash 0x%016x != this layout 0x%016x" % (hdr["layoutHash"], L.layout_hash()))
    if hdr["conventionsHash"] != CONVENTIONS_HASH:
        bad.append("conventionsHash 0x%016x != 0x%016x" % (hdr["conventionsHash"], CONVENTIONS_HASH))
    if not ended:
        bad.append("no end record (replayed to its last whole record)")
    for i, (kind, _t, _q, p) in enumerate(recs):
        if kind in (KIND_C3, KIND_C4):
            try:
                mtype, body = unframe(p)
            except (ValueError, struct.error) as e:
                bad.append("record %d: %s" % (i, e))
                break
            names = L.C3_TYPES if kind == KIND_C3 else L.C4_TYPES
            if mtype not in names.values():
                bad.append("record %d: unknown %s type %d" % (i, KIND_NAMES[kind], mtype))
        elif kind == KIND_C1 and len(p) < L.SE_FRAME.size:
            bad.append("record %d: C1 record %d B" % (i, len(p)))
    side = os.path.splitext(path)[0] + ".json"
    if os.path.exists(side):
        try:
            with open(side, encoding="utf-8") as f:
                sj = json.load(f)
            if sj.get("sha256") and sj["sha256"] != sha256_file(path):
                bad.append("sidecar sha256 differs from the file")
        except (OSError, ValueError) as e:
            bad.append("sidecar unreadable: %s" % e)
    else:
        bad.append("no JSON sidecar")
    return bad

def summarize(path: str) -> dict:
    hdr, recs, ended = read_derec(path)
    counts, types = {}, {}
    for kind, _t, _q, p in recs:
        counts[KIND_NAMES.get(kind, str(kind))] = counts.get(KIND_NAMES.get(kind, str(kind)), 0) + 1
        if kind in (KIND_C3, KIND_C4) and len(p) >= 8:
            mtype = struct.unpack_from("<I", p, 0)[0]
            table = L.C3_TYPES if kind == KIND_C3 else L.C4_TYPES
            name = next((k for k, v in table.items() if v == mtype), str(mtype))
            types[name] = types.get(name, 0) + 1
    ticks = [t for _k, t, _q, _p in recs]
    out = dict(hdr, layoutHash="0x%016x" % hdr["layoutHash"], conventionsHash="0x%016x" % hdr["conventionsHash"],
               path=path, records=len(recs), ended=ended, counts=counts, messageTypes=types,
               firstTick=min(ticks) if ticks else None, lastTick=max(ticks) if ticks else None,
               problems=recording_problems(path))
    return out

# ======================================================================================================================
#.mwm reader
# ======================================================================================================================

TECHNIQUES = ("MESH", "VOXELS_DEBRIS", "VOXEL_MAP", "ALPHA_MASKED", "ALPHA_MASKED_SINGLE_SIDED", "FOLIAGE", "DECAL",
              "DECAL_NOPREMULT", "DECAL_CUTOUT", "HOLO", "VOXEL_MAP_SINGLE", "VOXEL_MAP_MULTI", "SKINNED",
              "MESH_INSTANCED", "MESH_INSTANCED_SKINNED", "GLASS", "MESH_INSTANCED_GENERIC",
              "MESH_INSTANCED_GENERIC_MASKED", "ATMOSPHERE", "CLOUD_LAYER", "SHIELD", "SHIELD_LIT")   # byte enum order
MF_GLASS_CW, MF_GLASS_CCW, MF_ALPHA_MASKED = 1, 2, 4      # CONTRACT 5.4 MaterialDef flags (bits 0, 1, 2)

class _Reader:
    def __init__(self, data: bytes):
        self.d, self.p = data, 0

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.p)[0]
        self.p += 4
        return v

    def f32(self):
        v = struct.unpack_from("<f", self.d, self.p)[0]
        self.p += 4
        return v

    def boolean(self):
        return self.u8() != 0

    def string(self):          #.NET BinaryReader.ReadString: 7-bit encoded length, UTF-8
        n, shift = 0, 0
        while True:
            b = self.u8()
            n |= (b & 0x7F) << shift
            shift += 7
            if not b & 0x80:
                break
        s = self.d[self.p:self.p + n].decode("utf-8", "replace")
        self.p += n
        return s

    def array(self, fmt: str, size: int):
        n = self.i32()
        if n < 0 or self.p + n * size > len(self.d):
            raise ValueError("array of %d x %d B beyond the file" % (n, size))
        vals = struct.unpack_from("<%d%s" % (n, fmt), self.d, self.p)
        self.p += n * size
        return vals

def _halves(u: int, count: int):
    return struct.unpack("<%de" % count, struct.pack("<%dH" % count, *((u >> (16 * i)) & 0xFFFF for i in range(count))))

def _unpack_position(packed: int):
    x, y, z, w = _halves(packed, 4)
    return (x * w, y * w, z * w)

def _unpack_normal(packed: int):
    X, Y, Z, Wb = packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF, (packed >> 24) & 0xFF
    sign = 1.0 if Y > 127.5 else -1.0
    if sign > 0:
        Y -= 128
    a = (X + 256.0 * Y) / 32767.0
    b = (Z + 256.0 * Wb) / 32767.0
    nx, ny = 2 * a - 1, 2 * b - 1
    return (nx, ny, sign * math.sqrt(max(0.0, 1 - nx * nx - ny * ny)))

def _read_parts(r: _Reader, version: int) -> list:
    parts = []
    for _ in range(r.i32()):
        r.i32()                                  # material hash
        if version < 1052001:
            r.i32()
        idx = r.array("i", 4)
        mat = {"name": None, "technique": "MESH", "textures": {}, "glassCw": "", "glassCcw": ""}
        if r.boolean():
            mat["name"] = r.string() or None
            if version < 1052002:
                for key in ("DiffuseTexture", "NormalTexture"):
                    v = r.string()
                    if v:
                        mat["textures"][key] = v
            else:
                for _ in range(r.i32()):
                    k = r.string()
                    mat["textures"][k] = r.string()
            if version >= 1068001:
                for _ in range(r.i32()):
                    r.string()
                    r.string()
            if version < 1157001:
                for _ in range(7):
                    r.f32()
            mat["technique"] = r.string() if version >= 1052001 else str(r.i32())
            if mat["technique"] == "GLASS":
                if version >= 1043001:
                    mat["glassCw"] = r.string()
                    mat["glassCcw"] = r.string()
                    r.boolean()
                else:
                    for _ in range(4):
                        r.f32()
        parts.append({"material": mat, "indices": idx})
    return parts

def _content_file(content: str, rel: str) -> str:
    rel = rel.replace("/", "\\")
    if not rel.lower().endswith(".mwm"):
        rel += ".mwm"
    return os.path.join(content, *rel.split("\\"))

def load_mwm(content: str, rel: str, _depth: int = 0) -> dict:
    """{asset, file, version, verts, normals, uvs, tangents, binormals, parts [{material, indices}], bbox, patternScale,
 rescale, geometryAsset} of Content\\<rel>; a stub with GeometryDataAsset takes its geometry from that asset (as
 MyModel.LoadData does), keeping the stub's own bounding box and pattern scale."""
    path = _content_file(content, rel)
    with open(path, "rb") as f:
        r = _Reader(f.read())
    r.string()
    header = [r.string() for _ in range(r.i32())]
    version = int(header[0].replace("Version:", "")) if header and "Version:" in header[0] else 0
    if version < 1066002:
        raise ValueError("%s: model version %d without a tag index" % (rel, version))
    index = {}
    for _ in range(r.i32()):
        k = r.string()
        index[k] = r.i32()

    def at(tag):
        if tag not in index:
            return False
        r.p = index[tag]
        r.string()
        return True

    m = {"asset": rel, "file": path, "version": version, "verts": [], "normals": [], "uvs": [], "tangents": [],
         "binormals": [], "parts": [], "bbox": None, "patternScale": 1.0, "rescale": 1.0, "geometryAsset": None}
    if at("BoundingBox"):
        m["bbox"] = tuple(r.f32() for _ in range(6))
    if at("PatternScale"):
        m["patternScale"] = r.f32()
    if at("RescaleFactor"):
        m["rescale"] = r.f32()
    if "Vertices" not in index and at("GeometryDataAsset") and _depth == 0:
        asset = r.string()
        g = load_mwm(content, asset, _depth + 1)
        for k in ("verts", "normals", "uvs", "tangents", "binormals", "parts"):
            m[k] = g[k]
        m["geometryAsset"] = asset
        m["bbox"] = m["bbox"] or g["bbox"]
        return m
    if at("Vertices"):
        m["verts"] = [_unpack_position(v) for v in r.array("Q", 8)]
    if at("Normals"):
        m["normals"] = [_unpack_normal(v) for v in r.array("I", 4)]
    if at("TexCoords0"):
        m["uvs"] = [_halves(v, 2) for v in r.array("I", 4)]
    if at("Tangents"):
        m["tangents"] = [_unpack_normal(v) for v in r.array("I", 4)]
    if at("Binormals"):
        m["binormals"] = [_unpack_normal(v) for v in r.array("I", 4)]
    if at("MeshParts"):
        m["parts"] = _read_parts(r, version)
    return m

def resolve_texture(content: str, raw: str) -> str:
    """Absolute path of a Content-relative texture as the SE plugin's ModelLoader.Resolve: "" when absent."""
    if not raw:
        return ""
    full = raw if os.path.isabs(raw) else os.path.join(content, *raw.replace("/", "\\").split("\\"))
    return os.path.abspath(full) if os.path.exists(full) else ""

def export_model(content: str, rel: str, chunk_bytes: int = 1 << 20) -> dict:
    """The C3 export of one model: {asset, modelKey, verts, tris, bounds, patternScale, materials [(key, payload, info)],
 messages [(type, payload)], glass, alphaMasked, hasTangents, payloadBytes, loadMs}."""
    t0 = time.perf_counter()
    m = load_mwm(content, rel)
    nv = len(m["verts"])
    if nv == 0 or not m["parts"]:
        raise ValueError("%s: no geometry" % rel)
    normals = m["normals"] if len(m["normals"]) == nv else [(0.0, 1.0, 0.0)] * nv
    uvs = m["uvs"] if len(m["uvs"]) == nv else [(0.0, 0.0)] * nv
    has_t = len(m["tangents"]) == nv
    tangents = m["tangents"] if has_t else [(0.0, 0.0, 0.0)] * nv
    if has_t and len(m["binormals"]) == nv:
        binormals = m["binormals"]
    elif has_t:
        binormals = [(n[1] * t[2] - n[2] * t[1], n[2] * t[0] - n[0] * t[2], n[0] * t[1] - n[1] * t[0])
                     for n, t in zip(normals, tangents)]
    else:
        binormals = [(0.0, 0.0, 0.0)] * nv
    vert_blob = bytearray()
    pack = struct.Struct("<14f").pack
    for i in range(nv):
        p, n, t, b, uv = m["verts"][i], normals[i], tangents[i], binormals[i], uvs[i]
        vert_blob += pack(p[0], p[1], p[2], n[0], n[1], n[2], t[0], t[1], t[2], b[0], b[1], b[2], uv[0], uv[1])
    idx_out, subs, mats, mat_seen = [], [], [], {}
    glass = alpha = False
    for part in m["parts"]:
        mat = part["material"]
        tech_name = mat["technique"] or "MESH"
        tech = TECHNIQUES.index(tech_name) if tech_name in TECHNIQUES else 0
        tex = mat["textures"]
        cm, ng = tex.get("ColorMetalTexture") or "", tex.get("NormalGlossTexture") or ""
        add, am = tex.get("AddMapsTexture") or "", tex.get("AlphamaskTexture") or ""
        flags = (MF_GLASS_CW if mat["glassCw"] else 0) | (MF_GLASS_CCW if mat["glassCcw"] else 0)
        if am or tech_name in ("ALPHA_MASKED", "ALPHA_MASKED_SINGLE_SIDED"):
            flags |= MF_ALPHA_MASKED
        glass = glass or tech_name == "GLASS" or bool(flags & (MF_GLASS_CW | MF_GLASS_CCW))
        alpha = alpha or bool(flags & MF_ALPHA_MASKED)
        mkey = W.material_key(tech, flags, cm, ng, add, am)
        if mkey not in mat_seen:
            mat_seen[mkey] = True
            payload = W.material_def_payload(mkey, tech, flags, resolve_texture(content, cm), resolve_texture(content, ng),
                                             resolve_texture(content, add), resolve_texture(content, am))
            mats.append((mkey, payload, {"name": mat["name"], "technique": tech_name, "flags": flags}))
        ix = part["indices"]
        start = len(idx_out) // 3
        for k in range(0, len(ix) - 2, 3):
            idx_out += (ix[k], ix[k + 2], ix[k + 1])       # file order -> MyModel.Triangles (2nd and 3rd swapped)
        subs.append((mkey, start, len(idx_out) // 3 - start, tech))
    if any(i < 0 or i >= nv for i in idx_out):
        raise ValueError("%s: index outside %d vertices" % (rel, nv))
    payload = bytes(vert_blob) + struct.pack("<%dI" % len(idx_out), *idx_out) + b"".join(
        struct.pack("<QIIII", k, s, c, t, 0) for k, s, c, t in subs)
    bounds = m["bbox"]
    if bounds is None or any(not math.isfinite(x) for x in bounds):
        xs = list(zip(*m["verts"]))
        bounds = (min(xs[0]), min(xs[1]), min(xs[2]), max(xs[0]), max(xs[1]), max(xs[2]))
    xs = list(zip(*m["verts"]))   # the drawn vertices must lie inside the bounds the consumer checks
    bounds = (min(bounds[0], min(xs[0])), min(bounds[1], min(xs[1])), min(bounds[2], min(xs[2])),
              max(bounds[3], max(xs[0])), max(bounds[4], max(xs[1])), max(bounds[5], max(xs[2])))
    key = model_key(rel)
    chunk_bytes = max(64, min(int(chunk_bytes), L.C3_MAX_CHUNK))
    chunks = [payload[i:i + chunk_bytes] for i in range(0, len(payload), chunk_bytes)]
    R = L.RECORDS
    msgs = [(L.C3_TYPES["ModelBegin"], R["ModelBegin"].pack(
        modelKey=key, lod=0, vertexCount=nv, indexCount=len(idx_out), submeshCount=len(subs),
        flags=L.CONSTS["ModelFlags"]["HasTangents"] if has_t else 0, bounds=bounds, patternScale=m["patternScale"] or 1.0,
        chunkCount=len(chunks), totalBytes=len(payload)))]
    for i, c in enumerate(chunks):
        msgs.append((L.C3_TYPES["ModelChunk"], R["ModelChunkHeader"].pack(modelKey=key, chunkIndex=i, bytes=len(c)) + c))
    msgs.append((L.C3_TYPES["ModelEnd"], R["ModelEnd"].pack(modelKey=key, payloadHash=L.fnv1a64(payload))))
    return {"asset": rel, "modelKey": key, "verts": nv, "tris": len(idx_out) // 3, "bounds": tuple(bounds),
            "patternScale": m["patternScale"] or 1.0, "materials": mats, "messages": msgs, "glass": glass,
            "alphaMasked": alpha, "hasTangents": has_t, "payloadBytes": len(payload),
            "geometryAsset": m["geometryAsset"], "loadMs": round((time.perf_counter() - t0) * 1000.0, 2)}

def winding_fraction(content: str, rel: str):
    """Raw Unity-space winding agreement of a model as Subnautica measures it (CONTRACT 2.5; no flip): the fraction of
 non-degenerate triangles whose file-order geometric normal, mirrored, agrees with the mirrored vertex normals."""
    m = load_mwm(content, rel)
    v, n = m["verts"], m["normals"]
    agree = total = 0
    for part in m["parts"]:
        ix = part["indices"]
        for k in range(0, len(ix) - 2, 3):
            tri = (ix[k], ix[k + 1], ix[k + 2])          # Unity (I0, I2, I1) of Triangles = the file order
            mv = [(v[i][0], v[i][1], -v[i][2]) for i in tri]
            mn = [(n[i][0], n[i][1], -n[i][2]) for i in tri]
            e1 = [mv[1][j] - mv[0][j] for j in range(3)]
            e2 = [mv[2][j] - mv[0][j] for j in range(3)]
            g = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
            s = [mn[0][j] + mn[1][j] + mn[2][j] for j in range(3)]
            gl, sl = math.sqrt(sum(x * x for x in g)), math.sqrt(sum(x * x for x in s))
            if gl < 1e-9 or sl < 1e-6:
                continue
            d = sum(g[j] * s[j] for j in range(3))
            if abs(d) < 0.05 * gl * sl:
                continue
            total += 1
            agree += d > 0
    return (agree / total if total else None), agree, total

# ======================================================================================================================
# Rigid matrices in SE row-vector convention (VRage: p' = p x M, rows Right, Up, Backward, Translation; child x parent)
# ======================================================================================================================

IDENTITY = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, 0.0))

def m_mul(a, b):
    """a then b (VRage a * b): rows of a's rotation through b's rotation, a's translation through b."""
    rot = tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))
    t = tuple(sum(a[3][k] * b[k][j] for k in range(3)) + b[3][j] for j in range(3))
    return rot + (t,)

def m_inv(a):
    """Inverse of a rigid matrix: rotation transposed, translation -t R^T."""
    rt = tuple(tuple(a[j][i] for j in range(3)) for i in range(3))
    t = tuple(-sum(a[3][k] * rt[k][j] for k in range(3)) for j in range(3))
    return rt + (t,)

def m_point(m, p):
    return tuple(p[0] * m[0][j] + p[1] * m[1][j] + p[2] * m[2][j] + m[3][j] for j in range(3))

def m_trans(t):
    return IDENTITY[:3] + (tuple(float(x) for x in t),)

def m_rot(axis: str, ang: float):
    c, s = math.cos(ang), math.sin(ang)
    if axis == "x":
        r = ((1.0, 0.0, 0.0), (0.0, c, s), (0.0, -s, c))
    elif axis == "y":
        r = ((c, 0.0, -s), (0.0, 1.0, 0.0), (s, 0.0, c))
    else:
        r = ((c, s, 0.0), (-s, c, 0.0), (0.0, 0.0, 1.0))
    return r + ((0.0, 0.0, 0.0),)

def m_rows12(m):
    return [float(x) for row in m for x in row]

def m_from12(v):
    return (tuple(v[0:3]), tuple(v[3:6]), tuple(v[6:9]), tuple(v[9:12]))

def orientations24():
    """The 24 proper rotations of the cube (rows are signed axis permutations with determinant +1), in a fixed order."""
    out = []
    axes = ((1, 0, 0), (0, 1, 0), (0, 0, 1))
    for perm in ((0, 1, 2), (0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0)):
        for sx in (1, -1):
            for sy in (1, -1):
                for sz in (1, -1):
                    rows = tuple(tuple(float(s * a) for a in axes[p]) for p, s in zip(perm, (sx, sy, sz)))
                    det = (rows[0][0] * (rows[1][1] * rows[2][2] - rows[1][2] * rows[2][1])
                           - rows[0][1] * (rows[1][0] * rows[2][2] - rows[1][2] * rows[2][0])
                           + rows[0][2] * (rows[1][0] * rows[2][1] - rows[1][1] * rows[2][0]))
                    if det > 0:
                        out.append(rows)
    return out

ORIENTATIONS = orientations24()
assert len(ORIENTATIONS) == 24

def aabb_corners(b):
    """8 corners of (min xyz, max xyz); corner i: bit 0 max x, bit 1 max y, bit 2 max z (wire_ref.aabb_corner)."""
    return [(b[3] if i & 1 else b[0], b[4] if i & 2 else b[1], b[5] if i & 4 else b[2]) for i in range(8)]

# ======================================================================================================================
# The synthetic ship (S5 stand-in)
# ======================================================================================================================

LARGE, SMALL = 2.5, 0.5
SYNTH_ROOT_ID = 0x0044455335000001          # "DES5" ids: never collide with SE's entity ids
SYNTH_ROTOR_ID = SYNTH_ROOT_ID + 1
SYNTH_PISTON_ID = SYNTH_ROOT_ID + 2
SYNTH_DOCK_ID = SYNTH_ROOT_ID + 3
PALETTE = ((0.58, 0.4, 0.1), (0.0, -0.8, 0.55), (0.08, 0.6, 0.2), (0.33, 0.3, -0.1), (0.0, -1.0, -0.6))
FAT_HSV = (0.0, -0.8, 0.55)
SYNTH_SIZES = (500, 2000, 5000)
ARMOR_DIR = r"Models\Cubes\Large\Armor"
FAT_MODELS = {   # role -> Content-relative model (read in place)
    "cockpit": r"Models\Cubes\Large\Cockpit.mwm", "door": r"Models\Cubes\Large\Door.mwm",
    "doorLeft": r"Models\Cubes\Large\DoorLeft.mwm", "doorRight": r"Models\Cubes\Large\DoorRight.mwm",
    "stator": r"Models\Cubes\Large\MotorStator.mwm", "rotor": r"Models\Cubes\Large\MotorRotor.mwm",
    "pistonBase": r"Models\Cubes\Large\PistonBase.mwm", "piston1": r"Models\Cubes\Large\PistonSubpart1.mwm",
    "piston2": r"Models\Cubes\Large\PistonSubpart2.mwm", "piston3": r"Models\Cubes\Large\PistonSubpart3.mwm",
    "pistonTop": r"Models\Cubes\Large\PistonTop.mwm",
    "inset": r"Models\Cubes\Small\InsetConnectorConstruction_1.mwm",
    "stage1": r"Models\Cubes\Large\Armor\CornerSquareConstruction_1.mwm",
    "stage2": r"Models\Cubes\Large\Armor\CornerSquareConstruction_2.mwm",
    "stage3": r"Models\Cubes\Large\Armor\CornerSquareConstruction_3.mwm",
}
SMALL_ARMOR = (r"Models\Cubes\Small\ArmorCenter.mwm", r"Models\Cubes\Small\ArmorCorner.mwm",
               r"Models\Cubes\Small\ArmorSide.mwm", r"Models\Cubes\Small\ArmorInvCorner.mwm")
CENSUS_INVERTED = ("roundcornertrianglerightplate", "insetconnectorconstruction_1", "grinderreskindisk2", "cylinder_low",
                   "cockpitglass")   # gap-1 §2.3 (block and tool parts; foliage and loose items are never mirrored)
ROTOR_RPM = 10.0
PISTON_STROKE_M = 2.0
DOOR_TRAVEL_M = 1.1

def armor_catalogue(content: str) -> list:
    """The large armor base models (no LOD, no construction stage), sorted: 202 in SE 1.210.014."""
    folder = os.path.join(content, "Models", "Cubes", "Large", "Armor")
    out = []
    for f in sorted(os.listdir(folder), key=str.lower):
        low = f.lower()
        if not low.endswith(".mwm") or "_lod" in low or "construction" in low:
            continue
        out.append(ARMOR_DIR + "\\" + f)
    return out

_EXPORT_CACHE = {}       # (content, asset, chunk bytes) -> export_model: ships of several sizes share their models

class Part:
    __slots__ = ("grid", "key", "model", "cls", "flags", "local", "hsv", "cell", "block", "role", "names")

    def __init__(self, grid, key, model, cls, flags, local, hsv, cell=None, block=None, role="", names=None):
        self.grid, self.key, self.model, self.cls, self.flags = grid, key, model, cls, flags
        self.local, self.hsv, self.cell, self.block, self.role, self.names = local, hsv, cell, block, role, names

    def part_add(self) -> bytes:
        return L.RECORDS["PartAdd"].pack(entityId=self.grid, partKey=self.key, modelKey=self.model,
                                         local=m_rows12(self.local), colorMaskHSV=self.hsv, uvOffset=(0.0, 0.0),
                                         skinId=0, flags=self.flags)

class Ship:
    """N blocks of real SE models: grids, parts, models, the moving children and the live edits (module docstring)."""

    def __init__(self, content: str = SE_CONTENT, n_blocks: int = 500, seed: int = 1, chunk_bytes: int = 1 << 20,
                 log=None):
        self.content, self.n, self.seed, self.chunk_bytes = content, int(n_blocks), int(seed), chunk_bytes
        self.log = log or (lambda *a, **k: None)
        self.models = {}            # modelKey -> export dict
        self.model_errors = {}      # asset -> error text
        self.by_asset = {}          # asset -> modelKey
        self.grids = {}             # id -> {cls, parent, flags, g, bounds}
        self.parts = {}             # (grid, key) -> Part
        self.blocks = {}            # block id -> [(grid, key)]
        self.rotor_angle = 0.0
        self.rotor_rpm = 0.0
        self.piston_ext = 0.0
        self.piston_target = 0.0
        self.door_open = 0.0
        self.door_target = 0.0
        self.edits = 0
        self.stage_block = None
        self._build()

    # ---- models ---------------------------------------------------------------------------------------------------
    def model(self, asset: str):
        if asset in self.by_asset:
            return self.by_asset[asset]
        if asset in self.model_errors:
            return None
        try:
            ck = (self.content, asset, self.chunk_bytes)
            ex = _EXPORT_CACHE.get(ck)
            if ex is None:
                ex = _EXPORT_CACHE[ck] = export_model(self.content, asset, self.chunk_bytes)
        except (OSError, ValueError, struct.error, IndexError) as e:
            self.model_errors[asset] = "%s: %s" % (type(e).__name__, e)
            self.log("model", asset=asset, error=self.model_errors[asset])
            return None
        self.models[ex["modelKey"]] = ex
        self.by_asset[asset] = ex["modelKey"]
        return ex["modelKey"]

    # ---- layout ---------------------------------------------------------------------------------------------------
    def _add(self, grid, key, asset, cls, flags, local, hsv, cell=None, block=None, role="", names=None):
        mk = self.model(asset)
        if mk is None:
            return None
        p = Part(grid, key, mk, cls, flags | PF_VERSION1 | cls, local, tuple(hsv), cell, block, role, names)
        self.parts[(grid, key)] = p
        self.blocks.setdefault(block if block is not None else key, []).append((grid, key))
        return p

    def _armor(self, grid, g, cell, asset, rot_i, hsv, block=None):
        rot = ORIENTATIONS[rot_i % 24]
        t = tuple(c * g for c in cell)
        mk = self.model(asset)
        if mk is None:
            return None
        local = rot + (t,)
        key = cube_part_key(mk, W.f32(t[0]), W.f32(t[1]), W.f32(t[2]), float(rot_i % 24))
        return self._add(grid, key, asset, CLS_CUBE, 0, local, hsv, cell, block if block is not None else
                         ("%x:%d,%d,%d" % (grid, cell[0], cell[1], cell[2])), "armor")

    def _fat(self, grid, g, cell, asset, rot, hsv, role, flags=0):
        t = tuple(c * g for c in cell)
        key = fat_part_key(*cell)
        block = "%x:fat:%d,%d,%d" % (grid, cell[0], cell[1], cell[2])
        return self._add(grid, key, asset, CLS_FAT, flags, rot[:3] + (t,), hsv, cell, block, role)

    def _sub(self, grid, fat, names, asset, local, role):
        key = subpart_key(fat.key, names)
        return self._add(grid, key, asset, CLS_SUBPART, PF_DYNAMIC, local, fat.hsv, fat.cell, fat.block, role,
                         names=names)

    def _build(self):
        cat = armor_catalogue(self.content)
        self.catalogue = cat
        n_special = 4 + 5 + 3 + 9          # root fat blocks, rotor top, piston top, docked small grid
        n_armor = max(1, self.n - n_special)
        # hull box ~ 3:2:6 (width, height, length) holding n_armor cells
        k = (n_armor / 36.0) ** (1.0 / 3.0)
        X, Y = max(3, int(round(3 * k))), max(2, int(round(2 * k)))
        Z = max(4, -(-n_armor // (X * Y)))
        self.dims = (X, Y, Z)
        x0, y0, z0 = -(X // 2), -(Y // 2), -(Z // 2)
        self.hull = (x0, y0, z0, x0 + X - 1, y0 + Y - 1, z0 + Z - 1)
        root = SYNTH_ROOT_ID
        self.grids[root] = {"cls": 1, "parent": 0, "flags": GRID_VERSION1, "g": LARGE}
        i = 0
        for kz in range(Z):
            for jy in range(Y):
                for ix in range(X):
                    if i >= n_armor:
                        break
                    cell = (x0 + ix, y0 + jy, z0 + kz)
                    asset = cat[(i * 37) % len(cat)]
                    hsv = PALETTE[(jy + kz // 4) % len(PALETTE)]
                    self._armor(root, LARGE, cell, asset, (i * 7) % 24, hsv)
                    i += 1
        hx0, hy0, hz0, hx1, hy1, hz1 = self.hull
        ident = IDENTITY
        # cockpit at the front top (Forward = -Z), door at the front bottom with two sliding leaves
        self.cockpit = self._fat(root, LARGE, (0, hy1, hz0 - 1), FAT_MODELS["cockpit"], ident, FAT_HSV, "cockpit")
        door = self._fat(root, LARGE, (0, hy0, hz0 - 1), FAT_MODELS["door"], ident, FAT_HSV, "door")
        self.door = door
        self.door_leaves = []
        if door:
            for name, role, sx in (("DoorLeft", "doorLeft", -1.0), ("DoorRight", "doorRight", 1.0)):
                p = self._sub(root, door, name, FAT_MODELS[role], m_mul(m_trans((0.0, 0.0, 0.0)), m_trans(door.local[3])),
                              role)
                if p:
                    p.cell = sx
                    p.local = self.subpart_local(p)
                    self.door_leaves.append(p)
        # rotor: stator on the roof, its top a large subgrid turning about +Y
        self.stator = self._fat(root, LARGE, (0, hy1 + 1, 0), FAT_MODELS["stator"], ident, FAT_HSV, "stator")
        self.rotor_base = (0.0, (hy1 + 2) * LARGE, 0.0)
        self.grids[SYNTH_ROTOR_ID] = {"cls": 1, "parent": root, "flags": GRID_VERSION1 | GRID_SUBGRID, "g": LARGE}
        self._fat(SYNTH_ROTOR_ID, LARGE, (0, 0, 0), FAT_MODELS["rotor"], ident, FAT_HSV, "rotor")
        for j, c in enumerate(((1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1))):
            self._armor(SYNTH_ROTOR_ID, LARGE, c, cat[(j * 11 + 5) % len(cat)], j * 5, PALETTE[2])
        # piston on the +X side: base on the root with three dynamic subparts, its top a large subgrid along +X
        up_x = ((0.0, -1.0, 0.0), (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, 0.0))
        base = self._fat(root, LARGE, (hx1 + 1, 0, 0), FAT_MODELS["pistonBase"], up_x, FAT_HSV, "pistonBase")
        self.piston_base = base
        self.piston_parts = []
        if base:
            for n_, role in ((1, "piston1"), (2, "piston2"), (3, "piston3")):
                names = ["PistonSubpart%d" % n_] if n_ == 1 else ["PistonSubpart1", "PistonSubpart%d" % n_]
                p = self._sub(root, base, names, FAT_MODELS[role], base.local, role)
                if p:
                    p.cell = n_
                    p.local = self.subpart_local(p)
                    self.piston_parts.append(p)
        self.piston_origin = ((hx1 + 2) * LARGE, 0.0, 0.0)
        self.grids[SYNTH_PISTON_ID] = {"cls": 1, "parent": root, "flags": GRID_VERSION1 | GRID_SUBGRID, "g": LARGE}
        self._fat(SYNTH_PISTON_ID, LARGE, (0, 0, 0), FAT_MODELS["pistonTop"], ident, FAT_HSV, "pistonTop")
        for j, c in enumerate(((0, 0, 1), (0, 0, -1))):
            self._armor(SYNTH_PISTON_ID, LARGE, c, cat[(j * 13 + 3) % len(cat)], j * 3, PALETTE[3])
        # a small grid docked at the back (static relative matrix): the inset connector under construction
        self.dock_offset = (0.0, 0.0, (hz1 + 1) * LARGE + 1.0)
        self.grids[SYNTH_DOCK_ID] = {"cls": 2, "parent": root, "flags": GRID_VERSION1 | GRID_SUBGRID, "g": SMALL}
        self.inset = self._fat(SYNTH_DOCK_ID, SMALL, (0, 0, 0), FAT_MODELS["inset"], ident, PALETTE[4], "inset",
                               PF_BUILD_STAGE)
        j = 0
        for c in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (1, 1, 0), (-1, 1, 0), (1, -1, 0), (-1, -1, 0)):
            self._armor(SYNTH_DOCK_ID, SMALL, c, SMALL_ARMOR[j % 4], j * 3, PALETTE[(j + 1) % 5])
            j += 1
        for role in ("stage1", "stage2", "stage3"):
            self.model(FAT_MODELS[role])        # the weld / stage / damage models are known from the start
        self.cockpit_seat = None
        if self.cockpit:
            c = self.cockpit.local[3]
            self.cockpit_seat = (c[0], c[1] + 0.35, c[2] + 0.2)
            self.cockpit_point = (c[0] + 0.25, c[1] + 0.05, c[2] - 0.9)    # on the dashboard, 1.1 m ahead of the eye
        else:
            c = (0.0, (hy1 + 1) * LARGE, (hz0 - 1) * LARGE)
            self.cockpit_seat, self.cockpit_point = c, (c[0] + 0.25, c[1] - 0.3, c[2] - 1.1)

    # ---- geometry queries -----------------------------------------------------------------------------------------
    def model_bounds(self, mk):
        return self.models[mk]["bounds"]

    def grid_bounds(self, gid):
        """Grid-local AABB of a grid's parts (their model bounds under their part rows)."""
        lo, hi = [1e30] * 3, [-1e30] * 3
        for (g, _k), p in self.parts.items():
            if g != gid:
                continue
            for c in aabb_corners(self.model_bounds(p.model)):
                w = m_point(p.local, c)
                for a in range(3):
                    lo[a], hi[a] = min(lo[a], w[a]), max(hi[a], w[a])
        if lo[0] > hi[0]:
            return (-1.25, -1.25, -1.25, 1.25, 1.25, 1.25)
        return tuple(lo) + tuple(hi)

    def counts(self, gid) -> dict:
        n = verts = tris = 0
        by = {}
        for (g, _k), p in self.parts.items():
            if g != gid:
                continue
            n += 1
            by[CLASS_NAMES[p.cls]] = by.get(CLASS_NAMES[p.cls], 0) + 1
            verts += self.models[p.model]["verts"]
            tris += self.models[p.model]["tris"]
        return {"parts": n, "verts": verts, "tris": tris, "byClass": by}

    # ---- motion of the children (C2 rows) -------------------------------------------------------------------------
    def advance(self, dt: float):
        self.rotor_angle = (self.rotor_angle + 2.0 * math.pi * self.rotor_rpm / 60.0 * dt) % (2.0 * math.pi)
        step = 0.5 * dt      # m/s piston and door speeds (SE's defaults are in that order)
        for attr, target in (("piston_ext", self.piston_target), ("door_open", self.door_target)):
            v = getattr(self, attr)
            setattr(self, attr, min(target, v + step) if v < target else max(target, v - step))

    def rel(self, gid):
        """Subgrid world x root world^-1 (SE rows): the C2 subgrid row."""
        if gid == SYNTH_ROTOR_ID:
            return m_mul(m_rot("y", self.rotor_angle), m_trans(self.rotor_base))
        if gid == SYNTH_PISTON_ID:
            o = self.piston_origin
            return m_trans((o[0] + self.piston_ext, o[1], o[2]))
        if gid == SYNTH_DOCK_ID:
            return m_trans(self.dock_offset)
        return IDENTITY

    def subpart_local(self, p: Part):
        """Composed grid-local matrix of a dynamic subpart now."""
        if p.role in ("doorLeft", "doorRight"):
            off = (p.cell * (0.6 + DOOR_TRAVEL_M * self.door_open), 0.0, 0.0)
            return m_mul(m_trans(off), self.door.local)
        if p.role.startswith("piston"):
            off = (0.0, 1.25 + self.piston_ext * p.cell / 3.0, 0.0)      # along the base's Up (= grid +X)
            return m_mul(m_trans(off), self.piston_base.local)
        return p.local

    def to_root(self, p: Part):
        """Part frame -> root grid-local (SE rows) now."""
        local = self.subpart_local(p) if p.flags & PF_DYNAMIC else p.local
        if p.grid == SYNTH_ROOT_ID:
            return local
        return m_mul(local, self.rel(p.grid))

    def c2_rows(self):
        """[(ownerId, partKey, local rows)] in the contract's order: subgrids, then dynamic subparts (no tool here)."""
        rows = [(SYNTH_ROOT_ID, gid, self.rel(gid)) for gid in sorted(self.grids) if gid != SYNTH_ROOT_ID]
        for (g, k), p in sorted(self.parts.items(), key=lambda kv: kv[0]):
            if p.flags & PF_DYNAMIC:
                rows.append((g, k, self.subpart_local(p)))
        return rows

    def dynamic_parts(self):
        return [p for p in self.parts.values() if p.flags & PF_DYNAMIC]

    # ---- records ----------------------------------------------------------------------------------------------------
    def grid_add(self, gid, world_epoch) -> bytes:
        g = self.grids[gid]
        return L.RECORDS["GridAdd"].pack(entityId=gid, targetClass=g["cls"], flags=g["flags"],
                                         parentEntityId=g["parent"], worldEpoch=world_epoch)

    def stream(self, world_epoch: int = 1):
        """The first send in the contract's order: GridAdds (hierarchy), then per part (root first, nearest cell to the
 hull centre first) its materials and model on first use (C3) and its PartAdd (C4). [(ring, type, payload, meta)]"""
        out = []
        for gid in sorted(self.grids, key=lambda g: (g != SYNTH_ROOT_ID, g)):
            out.append(("C4", L.C4_TYPES["GridAdd"], self.grid_add(gid, world_epoch), ("grid", gid)))
        sent_mat, sent_model = set(), set()

        def dist(p):
            # nearest first (the contract's order), except the census-listed inverted models, sent first so that the
 # first 200 distinct models of S5.G1-E4 hold them whatever the ship size (a stand-in choice)
            t = self.to_root(p)[3]
            census = any(c in os.path.basename(self.models[p.model]["asset"]).lower() for c in CENSUS_INVERTED)
            return (not census, p.grid != SYNTH_ROOT_ID, t[0] * t[0] + t[1] * t[1] + t[2] * t[2], p.key)
        for p in sorted(self.parts.values(), key=dist):
            if p.flags & PF_DYNAMIC:
                p.local = self.subpart_local(p)       # composed grid-local matrix at send time
            out.extend(self.model_messages(p.model, sent_mat, sent_model))
            out.append(("C4", L.C4_TYPES["PartAdd"], p.part_add(), ("part", p.grid, p.key)))
        for role in ("stage1", "stage2", "stage3"):
            mk = self.by_asset.get(FAT_MODELS[role])
            if mk is not None:
                out.extend(self.model_messages(mk, sent_mat, sent_model))
        return out

    def model_messages(self, mk, sent_mat=None, sent_model=None):
        out = []
        ex = self.models[mk]
        for key, payload, _info in ex["materials"]:
            if sent_mat is None or key not in sent_mat:
                if sent_mat is not None:
                    sent_mat.add(key)
                out.append(("C3", L.C3_TYPES["MaterialDef"], payload, ("material", key)))
        if sent_model is None or mk not in sent_model:
            if sent_model is not None:
                sent_model.add(mk)
            for t, payload in ex["messages"]:
                out.append(("C3", t, payload, ("model", mk) if t == L.C3_TYPES["ModelEnd"] else None))
        return out

    # ---- live edits (N2) --------------------------------------------------------------------------------------------
    def free_cell(self):
        hx0, hy0, hz0, hx1, hy1, hz1 = self.hull
        taken = {p.cell for p in self.parts.values() if p.grid == SYNTH_ROOT_ID and isinstance(p.cell, tuple)}
        for k in range(hz0, hz1 + 1):
            for i in range(hx0, hx1 + 1):
                c = (i, hy1 + 1, k)
                if c not in taken:
                    return c
        return (hx1 + 3, 0, 0)

    def edit(self, kind: str, sent_models: set) -> dict:
        """One SE event on the root grid: the records it causes (C3 for a model not yet sent) and its part keys.
 weld = a new block at construction stage 1 (FatBlock, BuildStage); stage = that block one stage further
 (same fat key: a replace); damage = a complete armor block falls to stage 3 (its cube part removed, a FatBlock
 stage model added); paint = PartRecolor of a block's parts; grind = PartRemove of a block's parts."""
        root = SYNTH_ROOT_ID
        recs, keys = [], []
        self.edits += 1
        if kind == "weld":
            cell = self.free_cell()
            p = self._fat(root, LARGE, cell, FAT_MODELS["stage1"], IDENTITY, PALETTE[1], "stage", PF_BUILD_STAGE)
            self.stage_block = (p, 1)
            recs += self.model_messages(p.model, None, sent_models) if p.model not in sent_models else []
            recs.append(("C4", L.C4_TYPES["PartAdd"], p.part_add(), ("part", root, p.key)))
            keys.append(p.key)
        elif kind == "stage":
            if not self.stage_block:
                return self.edit("weld", sent_models)
            old, n = self.stage_block
            n = min(3, n + 1)
            mk = self.model(FAT_MODELS["stage%d" % n])
            p = Part(root, old.key, mk, CLS_FAT, PF_VERSION1 | CLS_FAT | PF_BUILD_STAGE, old.local, old.hsv, old.cell,
                     old.block, "stage")
            self.parts[(root, p.key)] = p
            self.stage_block = (p, n)
            if mk not in sent_models:
                recs += self.model_messages(mk, None, sent_models)
            recs.append(("C4", L.C4_TYPES["PartAdd"], p.part_add(), ("part", root, p.key)))
            keys.append(p.key)
        elif kind in ("damage", "grind", "paint"):
            cands = sorted((p for p in self.parts.values() if p.grid == root and p.role == "armor"),
                           key=lambda p: (-(p.local[3][1]), p.local[3][2], p.local[3][0]))
            if not cands:
                return {"kind": kind, "records": [], "keys": []}
            victim = cands[(self.edits * 7) % len(cands)]
            block = [self.parts[gk] for gk in self.blocks.get(victim.block, []) if gk in self.parts]
            if kind == "paint":
                hsv = PALETTE[(self.edits + 2) % len(PALETTE)]
                for p in block:
                    p.hsv = hsv
                    recs.append(("C4", L.C4_TYPES["PartRecolor"], L.RECORDS["PartRecolor"].pack(
                        entityId=root, partKey=p.key, hsv=hsv), ("recolor", root, p.key)))
                    keys.append(p.key)
            else:
                for p in block:
                    del self.parts[(p.grid, p.key)]
                    recs.append(("C4", L.C4_TYPES["PartRemove"], L.RECORDS["PartRemove"].pack(
                        entityId=root, partKey=p.key), ("remove", root, p.key)))
                    keys.append(p.key)
                self.blocks.pop(victim.block, None)
                if kind == "damage":
                    cell = victim.cell
                    p = self._fat(root, LARGE, cell, FAT_MODELS["stage3"], victim.local, victim.hsv, "damaged",
                                  PF_BUILD_STAGE)
                    if p.model not in sent_models:
                        recs += self.model_messages(p.model, None, sent_models)
                    recs.append(("C4", L.C4_TYPES["PartAdd"], p.part_add(), ("part", root, p.key)))
                    keys.append(p.key)
        return {"kind": kind, "records": recs, "keys": keys}

    def random_parts(self, n: int = 20, seed: int = 0) -> list:
        """n parts spread over every grid and class (deterministic for a seed): [(grid, key)]."""
        import random
        rnd = random.Random(seed)
        allp = sorted(self.parts.keys())
        pick = []
        for gid in sorted(self.grids):
            g = [k for k in allp if k[0] == gid]
            if g:
                pick.append(rnd.choice(g))
        rest = [k for k in allp if k not in pick]
        rnd.shuffle(rest)
        pick += rest[:max(0, n - len(pick))]
        return pick[:n]

def synth_recording(n: int, name: str = None, seed: int = 1, content: str = SE_CONTENT, world_epoch: int = 1,
                    path: str = None, log=None) -> dict:
    """Writes the synthetic ship's first send as a.derec (synthetic flag; C3/C4 only, tick 0) and returns the
 sidecar plus the Ship. The live C2 rows and edits are the producer's (the ship moves)."""
    ship = Ship(content, n, seed, log=log)
    name = name or "synth-%d" % n
    path = path or recording_path(name)
    wr = DerecWriter(path, synthetic=True, world_epoch=world_epoch)
    wr.marker({"ev": "synth", "blocks": n, "seed": seed, "grids": len(ship.grids), "parts": len(ship.parts),
               "models": len(ship.models)}, 0)
    for ring, mtype, payload, _meta in ship.stream(world_epoch):
        wr.message(ring, mtype, payload, 0)
    side = wr.close(name)
    side["path"] = path
    side["ship"] = ship
    return side

# ======================================================================================================================
# Self-test and CLI
# ======================================================================================================================

def selftest(content: str = SE_CONTENT, n: int = 500) -> int:
    import tempfile
    import stream_py as ST   # tests/standins: the Subnautica-side validators of real models and parts
    fails = []

    def check(ok, what, detail=""):
        print("  %-4s %s%s" % ("ok" if ok else "FAIL", what, (" (" + detail + ")") if detail and not ok else ""))
        if not ok:
            fails.append(what)

    print("S5 stream self-test")
    bad = conventions_problems()
    check(not bad, "conventions twin: text hash 0x%016x, 1,539 B / 37 lines, the example vectors, skeleton canon1 "
          "0x%016x" % (CONVENTIONS_HASH, SKELETON_HASH), "; ".join(bad))
    for name in ("GridAdd", "EntityRemove", "PartAdd", "PartRemove", "PartRecolor", "CharacterAdd", "ClearAll"):
        check(name in L.C4_TYPES and name in L.RECORDS, "layout_py names C4 %s" % name)
    for name in ("ModelBegin", "ModelChunk", "ModelEnd", "MaterialDef", "SkinnedBind"):
        check(name in L.C3_TYPES, "layout_py names C3 %s" % name)
    check(L.RECORDS["PartAdd"].size == 100 and L.RECORDS["GridAdd"].size == 32 and
          L.RECORDS["CharacterAdd"].size == 24 and L.RECORDS["SeSubpart"].size == 64, "record sizes as CONTRACT 5.4")
    ortho = all(abs(sum(o[i][k] * o[j][k] for k in range(3)) - (1.0 if i == j else 0.0)) < 1e-12
                for o in ORIENTATIONS for i in range(3) for j in range(3))
    check(ortho and len({tuple(map(tuple, o)) for o in ORIENTATIONS}) == 24, "24 distinct cube orientations")
    if not os.path.isdir(content):
        check(False, "SE Content folder %s" % content)
        return 1
    t0 = time.perf_counter()
    side = synth_recording(n, path=os.path.join(tempfile.mkdtemp(prefix="de-s5-"), "synth-test.derec"), content=content)
    ship = side.pop("ship")
    secs = time.perf_counter() - t0
    check(not ship.model_errors, "synth %d: %d grids, %d parts, %d models read in place in %.1f s" % (
        n, len(ship.grids), len(ship.parts), len(ship.models), secs), json.dumps(ship.model_errors)[:300])
    check(len(ship.models) >= 200, ">= 200 distinct real models (%d)" % len(ship.models))
    names = {os.path.basename(m["asset"]).lower()[:-4] for m in ship.models.values()}
    check("roundcornertrianglerightplate" in names and "insetconnectorconstruction_1" in names,
          "the census-listed inverted models are in the set (S5.M2 can turn S5.G1-E4 red)")
    total_blocks = len(ship.blocks)
    check(abs(total_blocks - n) <= 4, "blocks %d ~ %d" % (total_blocks, n))
    path = side["path"]                       # read the file back as the replayer does
    hdr, recs, ended = read_derec(path)
    check(ended and hdr["synthetic"] and hdr["conventionsHash"] == CONVENTIONS_HASH and
          hdr["layoutHash"] == L.layout_hash() and hdr["seVersion"] == 0, "derec header: synthetic, hashes, end record")
    check(not recording_problems(path), "derec framing and sidecar sha256", "; ".join(recording_problems(path)))
    # validate every record as the Subnautica side reads it
    begins, payloads, problems, mats = {}, {}, [], set()
    parts_seen, grids_seen = 0, {}
    for kind, _t, _q, blob in recs:
        if kind not in (KIND_C3, KIND_C4):
            continue
        mtype, body = unframe(blob)
        if kind == KIND_C3:
            if mtype == L.C3_TYPES["ModelBegin"]:
                v = L.RECORDS["ModelBegin"].unpack(body)
                err = ST.real_begin_error(v)
                if err:
                    problems.append("begin %x: %s" % (v["modelKey"], err))
                begins[v["modelKey"]] = v
                payloads[v["modelKey"]] = bytearray()
            elif mtype == L.C3_TYPES["ModelChunk"]:
                h = L.RECORDS["ModelChunkHeader"].unpack(body)
                payloads[h["modelKey"]] += body[16:16 + h["bytes"]]
            elif mtype == L.C3_TYPES["ModelEnd"]:
                e = L.RECORDS["ModelEnd"].unpack(body)
                pl = bytes(payloads[e["modelKey"]])
                if L.fnv1a64(pl) != e["payloadHash"]:
                    problems.append("hash %x" % e["modelKey"])
                d, err = ST.decode_real_model(begins[e["modelKey"]], pl)
                if err:
                    problems.append("model %x: %s" % (e["modelKey"], err))
                else:
                    for s in d["submeshes"]:
                        if s["materialKey"] not in mats:
                            problems.append("model %x submesh material %x before its MaterialDef" % (
                                e["modelKey"], s["materialKey"]))
            elif mtype == L.C3_TYPES["MaterialDef"]:
                m, err = ST.decode_material_def(body)
                if err:
                    problems.append("material: %s" % err)
                else:
                    mats.add(m["materialKey"])
        else:
            if mtype == L.C4_TYPES["GridAdd"]:
                g = L.RECORDS["GridAdd"].unpack(body)
                grids_seen[g["entityId"]] = g
                if version_nibble(g["flags"]) != 1:
                    problems.append("GridAdd %x without Version1" % g["entityId"])
            elif mtype == L.C4_TYPES["PartAdd"]:
                v = L.RECORDS["PartAdd"].unpack(body)
                parts_seen += 1
                err = ST.real_part_error(v)
                if err:
                    problems.append("part %x: %s" % (v["partKey"], err))
                if version_nibble(v["flags"]) != 1 or part_class(v["flags"]) not in (1, 3, 4):
                    problems.append("part %x flags %x" % (v["partKey"], v["flags"]))
                if v["entityId"] not in grids_seen:
                    problems.append("part before its GridAdd")
                if v["modelKey"] not in begins:
                    problems.append("part %x before its model" % v["partKey"])
    check(not problems, "every C3/C4 record valid for the Subnautica side (%d models, %d materials, %d parts)" % (
        len(begins), len(mats), parts_seen), "; ".join(problems[:5]))
    check(parts_seen == len(ship.parts) and set(grids_seen) == set(ship.grids), "records = the ship (parts, grids)")
    subs = [g for g in grids_seen.values() if g["flags"] & GRID_SUBGRID]
    check(len(subs) == 3 and all(g["parentEntityId"] == SYNTH_ROOT_ID for g in subs),
          "3 subgrids, flattened to the root (rotor, piston, small docked grid)")
    check(any(g["targetClass"] == 2 for g in subs), "a small-grid subgrid")
    check(any(p.flags & PF_BUILD_STAGE for p in ship.parts.values()), "a build-stage variant (InsetConnectorConstruction_1)")
    check(len({p.hsv for p in ship.parts.values()}) >= 4, "colours")
    # motion: the rotor at 10 rpm, a subgrid row is rigid; dynamic subpart rows follow the door/piston
    ship.rotor_rpm = ROTOR_RPM
    r0 = ship.rel(SYNTH_ROTOR_ID)
    for _ in range(60):
        ship.advance(1.0 / 60.0)
    r1 = ship.rel(SYNTH_ROTOR_ID)
    ang = math.degrees(math.atan2(r1[0][2], r1[0][0]) - math.atan2(r0[0][2], r0[0][0]))
    check(abs(abs(ang) - 60.0) < 1e-6, "rotor turns 60 deg/s at 10 rpm (%.6f deg in 1 s)" % abs(ang))
    rows = ship.c2_rows()
    check(len(rows) == 3 + len(ship.dynamic_parts()) and len(rows) <= L.C2_MAX_SUBPARTS,
          "C2 rows: 3 subgrids + %d dynamic subparts" % len(ship.dynamic_parts()))
    ship.door_target = 1.0
    for _ in range(240):
        ship.advance(1.0 / 60.0)
    lv = [p for p in ship.dynamic_parts() if p.role == "doorLeft"]
    check(bool(lv) and abs(ship.subpart_local(lv[0])[3][0] - (ship.door.local[3][0] - 0.6 - DOOR_TRAVEL_M)) < 1e-9,
          "door leaf slides open %.1f m" % DOOR_TRAVEL_M)
    # edits
    sent = set(ship.models)
    before = ship.counts(SYNTH_ROOT_ID)["parts"]
    w = ship.edit("weld", sent)
    s = ship.edit("stage", sent)
    d = ship.edit("damage", sent)
    pa = ship.edit("paint", sent)
    g = ship.edit("grind", sent)
    after = ship.counts(SYNTH_ROOT_ID)["parts"]
    kinds = lambda e: [m[0] for _r, _t, _p, m in e["records"] if m]   # noqa: E731
    check(kinds(w) == ["part"] and kinds(s) == ["part"] and "remove" in kinds(d) and "part" in kinds(d) and
          set(kinds(pa)) == {"recolor"} and set(kinds(g)) == {"remove"}, "edits: weld, stage, damage, paint, grind records")
    check(after == before + 1 - len(g["keys"]), "part count after the edits (%d -> %d)" % (before, after))
    rp = ship.random_parts(20, 7)
    check(len(rp) == 20 and len({k[0] for k in rp}) == len(ship.grids), "20 random parts over every grid")
    print("S5 STREAM SELFTEST %s (%d failed)" % ("PASS" if not fails else "FAIL", len(fails)))
    return 0 if not fails else 1

def main(argv=None) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="S5 stream tools (.derec, synthetic ships, conventions twin)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("selftest")
    p.add_argument("--content", default=SE_CONTENT)
    p.add_argument("--blocks", type=int, default=500)
    p = sub.add_parser("info")
    p.add_argument("recording")
    p = sub.add_parser("synth")
    p.add_argument("blocks", type=int)
    p.add_argument("--name", default=None)
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--content", default=SE_CONTENT)
    a = ap.parse_args(argv)
    if a.cmd == "selftest":
        return selftest(a.content, a.blocks)
    if a.cmd == "info":
        info = summarize(resolve_recording(a.recording))
        print(json.dumps(info, indent=1))
        return 0 if not info["problems"] else 1
    side = synth_recording(a.blocks, a.name, a.seed, a.content)
    side.pop("ship")
    print(json.dumps(side, indent=1))
    return 0

if __name__ == "__main__":
    sys.exit(main())
