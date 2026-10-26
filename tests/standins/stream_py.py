"""Synthetic, self-checking traffic for the S0 stand-ins (fake_se_producer.py <-> fake_sn_host.py).

Every message is a CONTRACT §5.4 record whose content is a deterministic function of its ids, so the consumer can
recompute it and compare byte for byte: a misparsed or misframed message cannot pass. On top of that:
- C3 model payloads are synthetic bytes, not meshes (S0 only; S3/S5 replace them with real models): each ModelChunk
 payload is a 16-byte stamp {u64 sendSerial, i64 qpcAtWrite} followed by a deterministic body; ModelEnd.payloadHash
 is FNV-1a 64 over the concatenated bodies. The stamp lets a consumer detect data written before its own re-sync
 point (stale replay, e.g. after a creator zeroed the producer heads: S0.M7).
- C4 GridAdd/PartAdd/PartRecolor fields are functions of (entityId, partKey).
- C8 damage records are functions of (snGeneration, dedupSeq).

S3 (real models, `real=True`; the fake SN host's --s3): records outside the S0 catalogue (a ModelBegin with vertexCount
> 0, MaterialDef, grids and parts whose entity id is not in the S0 GRID_BASE block, PartRemove) are validated the way
the SN plugin's MirrorWire does (CONTRACT 5.4 "C3/C4 conventions for S3"): ModelBegin counts and totalBytes =
vertexCount*stride + indexCount*4 + submeshCount*24, chunks in order (1 B.. 4 MiB of data each, the bytes after the
16-byte header), ModelEnd payloadHash = FNV-1a 64 over the concatenated chunk data, the payload decoded (indices below
vertexCount, submesh triangle ranges inside the model, finite positions); MaterialDef = 16-byte header + four u16-prefixed
strict UTF-8 strings, exact length; PartAdd local rows rigid and finite. A part is pending until its model ended and every
submesh material was defined (the SN plugin's C7 pendingParts). The S0 catalogue traffic keeps its byte-for-byte checks.
"""
from __future__ import annotations

import json
import os
import random
import struct
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_WIRE = os.path.normpath(os.path.join(_HERE, "..", "wire"))
for p in (_WIRE, _HERE):
    if p not in sys.path:
        sys.path.insert(0, p)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402

R = L.RECORDS

# ----------------------------------------------------------------------------------------------------------------
# Event output (JSON lines on stdout; one clock: QPC seconds)
# ----------------------------------------------------------------------------------------------------------------

class Emitter:
    def __init__(self, src: str, quiet: bool = False, human: bool = False):
        self.src = src
        self.pid = S.current_pid()
        self.quiet = quiet
        self.human = human

    def __call__(self, ev: str, msg: str = None, **kw):
        if self.quiet and ev == "stats":
            return
        rec = {"t": round(S.now_s(), 6), "src": self.src, "pid": self.pid, "ev": ev}
        if msg:
            rec["msg"] = msg
        rec.update(kw)
        try:
            if self.human:
                line = "%.3f %s %s %s" % (rec["t"], self.src, ev, msg or json.dumps(kw, separators=(",", ":")))
            else:
                line = json.dumps(rec, separators=(",", ":"), default=str)
            sys.stdout.write(line + "\n")
            sys.stdout.flush()
        except (OSError, ValueError):
            pass

# ----------------------------------------------------------------------------------------------------------------
# Models (C3)
# ----------------------------------------------------------------------------------------------------------------

CHUNK_BODY = 16 * 1024
STAMP = struct.Struct("<Qq")
N_MODEL_CATALOGUE = 64
MODEL_KEYS = [L.fnv1a64(b"de.standin.model:%d" % i) for i in range(N_MODEL_CATALOGUE)]
MODEL_INDEX = {k: i for i, k in enumerate(MODEL_KEYS)}
_body_cache = {}
_hash_cache = {}

def model_size(key: int) -> int:
    """Body bytes of a catalogue model: 16..128 KiB."""
    return CHUNK_BODY * (1 + MODEL_INDEX[key] % 8)

def model_chunk_count(key: int) -> int:
    return (model_size(key) + CHUNK_BODY - 1) // CHUNK_BODY

def model_chunk_body(key: int, idx: int) -> bytes:
    k = (key, idx)
    b = _body_cache.get(k)
    if b is None:
        n = min(CHUNK_BODY, model_size(key) - idx * CHUNK_BODY)
        b = random.Random((key << 16) ^ idx).randbytes(n)
        _body_cache[k] = b
    return b

def model_hash(key: int) -> int:
    h = _hash_cache.get(key)
    if h is None:
        h = L.FNV64_OFFSET
        for i in range(model_chunk_count(key)):
            h = L.fnv1a64(model_chunk_body(key, i), h)
        _hash_cache[key] = h
    return h

def model_begin(key: int) -> bytes:
    n = model_chunk_count(key)
    return R["ModelBegin"].pack(modelKey=key, lod=0, vertexCount=0, indexCount=0, submeshCount=0, flags=0,
                                bounds=(-1.0, -1.0, -1.0, 1.0, 1.0, 1.0), patternScale=1.0, chunkCount=n,
                                totalBytes=model_size(key) + n * STAMP.size)

def model_chunk(key: int, idx: int, serial: int) -> bytes:
    body = model_chunk_body(key, idx)
    data = STAMP.pack(serial, S.qpc()) + body
    return R["ModelChunkHeader"].pack(modelKey=key, chunkIndex=idx, bytes=len(data)) + data

def model_end(key: int) -> bytes:
    return R["ModelEnd"].pack(modelKey=key, payloadHash=model_hash(key))

class ModelAssembler:
    """C3 consumer state machine with full validation. misparse: a message that cannot come from a correct
 producer; stale: chunk data stamped before the consumer's re-sync point (minus a grace); orphan: chunks or
 ends seen after a re-sync before the next ModelBegin (expected: the re-sync dropped the begin)."""

    STALE_GRACE_S = 0.25

    def __init__(self, real: bool = False):
        self.real = real               # S3: accept real models (module docstring); S0 catalogue traffic still exact
        self.models = {}               # S3: modelKey -> decoded summary of a completed real model
        self.materials = {}            # S3: materialKey -> MaterialDef dict
        self.completed_real = []       # S3: real model keys completed since the host last took them
        self.complete = set()
        self.cur = None
        self.synced = True
        self.resync_qpc = None
        self.misparse = 0
        self.stale = 0
        self.orphans = 0
        self.abandoned = 0
        self.unexpected = 0
        self.messages = 0
        self.bytes = 0
        self.details = []

    def _bad(self, what):
        self.misparse += 1
        if len(self.details) < 20:
            self.details.append(what)

    def on_resync(self):
        self.cur = None
        self.synced = False
        self.resync_qpc = S.qpc()

    def reset(self):
        self.complete.clear()
        self.on_resync()

    def feed(self, mtype: int, payload: bytes):
        self.messages += 1
        self.bytes += len(payload) + 8
        if mtype == L.C3_TYPES["ModelBegin"]:
            if len(payload) != 64:
                return self._bad("ModelBegin size %d" % len(payload))
            v = R["ModelBegin"].unpack(payload)
            key = v["modelKey"]
            if self.real and v["vertexCount"] > 0:
                err = real_begin_error(v)
                if err:
                    return self._bad("ModelBegin key=%x: %s" % (key, err))
                if self.cur is not None:
                    self.abandoned += 1
                self.cur = [key, 0, v["chunkCount"], v, bytearray(), "real"]
                self.synced = True
                return None
            if key not in MODEL_INDEX or payload != model_begin(key):
                return self._bad("ModelBegin content key=%x" % key)
            if self.cur is not None:
                self.abandoned += 1
            self.cur = [key, 0, model_chunk_count(key)]
            self.synced = True
            return None
        if mtype == L.C3_TYPES["ModelChunk"]:
            if self.real and len(payload) >= 16 and R["ModelChunkHeader"].unpack(payload)["modelKey"] not in MODEL_INDEX:
                return self._real_chunk(payload)
            if len(payload) < 16 + STAMP.size:
                return self._bad("ModelChunk size %d" % len(payload))
            h = R["ModelChunkHeader"].unpack(payload)
            data = payload[16:]
            if h["bytes"] != len(data):
                return self._bad("ModelChunk bytes field %d != %d" % (h["bytes"], len(data)))
            key, idx = h["modelKey"], h["chunkIndex"]
            if key not in MODEL_INDEX or idx >= model_chunk_count(key) or data[STAMP.size:] != model_chunk_body(key, idx):
                return self._bad("ModelChunk content key=%x idx=%d" % (key, idx))
            serial, stamp = STAMP.unpack_from(data, 0)
            if self.resync_qpc is not None and stamp < self.resync_qpc - int(self.STALE_GRACE_S * S.QPC_FREQUENCY):
                self.stale += 1
                if len(self.details) < 20:
                    self.details.append("stale chunk serial=%d age-before-resync=%.3fs" % (
                        serial, (self.resync_qpc - stamp) / S.QPC_FREQUENCY))
                return None
            if self.cur is None:
                if not self.synced:
                    self.orphans += 1
                    return None
                return self._bad("ModelChunk without ModelBegin key=%x" % key)
            if len(self.cur) > 3 or key != self.cur[0] or idx != self.cur[1]:
                return self._bad("ModelChunk out of order key=%x idx=%d expected %x/%d" % (key, idx, self.cur[0],
                                                                                          self.cur[1]))
            self.cur[1] += 1
            return None
        if mtype == L.C3_TYPES["ModelEnd"]:
            if len(payload) != 16:
                return self._bad("ModelEnd size %d" % len(payload))
            v = R["ModelEnd"].unpack(payload)
            key = v["modelKey"]
            if self.real and key not in MODEL_INDEX:
                return self._real_end(key, v["payloadHash"])
            if key not in MODEL_INDEX or v["payloadHash"] != model_hash(key):
                return self._bad("ModelEnd hash key=%x" % key)
            if self.cur is None:
                if not self.synced:
                    self.orphans += 1
                    return None
                return self._bad("ModelEnd without ModelBegin key=%x" % key)
            if len(self.cur) > 3 or key != self.cur[0] or self.cur[1] != self.cur[2]:
                return self._bad("ModelEnd incomplete key=%x chunks %d/%d" % (key, self.cur[1], self.cur[2]))
            self.complete.add(key)
            self.cur = None
            return key
        if mtype == L.C3_TYPES["MaterialDef"] and self.real:
            m, err = decode_material_def(payload)
            if err:
                return self._bad("MaterialDef: %s" % err)
            self.materials[m["materialKey"]] = m
            return None
        if mtype in (L.C3_TYPES["MaterialDef"], L.C3_TYPES["SkinnedBind"]):
            self.unexpected += 1
            return None
        return self._bad("unknown C3 type %d" % mtype)

    # ---- S3 real models -----------------------------------------------------------------------------------------
    def _real_chunk(self, payload: bytes):
        h = R["ModelChunkHeader"].unpack(payload)
        data = payload[16:]
        key, idx = h["modelKey"], h["chunkIndex"]
        if h["bytes"] != len(data):
            return self._bad("ModelChunk bytes field %d != %d" % (h["bytes"], len(data)))
        if not 1 <= len(data) <= L.C3_MAX_CHUNK:
            return self._bad("ModelChunk key=%x idx=%d carries %d B (1 B .. 4 MiB)" % (key, idx, len(data)))
        if self.cur is None:
            if not self.synced:
                self.orphans += 1
                return None
            return self._bad("ModelChunk without ModelBegin key=%x" % key)
        c = self.cur
        if len(c) <= 3 or key != c[0] or idx != c[1]:
            self.cur, self.synced = None, False      # the rest of this model's chunks are orphans (the SN plugin)
            return self._bad("ModelChunk out of order key=%x idx=%d expected %x/%d" % (key, idx, c[0], c[1]))
        if len(c[4]) + len(data) > c[3]["totalBytes"]:
            self.cur, self.synced = None, False
            return self._bad("ModelChunk key=%x idx=%d beyond totalBytes %d" % (key, idx, c[3]["totalBytes"]))
        c[4] += data
        c[1] += 1
        return None

    def _real_end(self, key: int, payload_hash: int):
        if self.cur is None:
            if not self.synced:
                self.orphans += 1
                return None
            return self._bad("ModelEnd without ModelBegin key=%x" % key)
        c, self.cur = self.cur, None
        if len(c) <= 3 or key != c[0]:
            return self._bad("ModelEnd key=%x closes %x" % (key, c[0]))
        if c[1] != c[2] or len(c[4]) != c[3]["totalBytes"]:
            return self._bad("ModelEnd incomplete key=%x chunks %d/%d bytes %d/%d" % (key, c[1], c[2], len(c[4]),
                                                                                     c[3]["totalBytes"]))
        got = L.fnv1a64(bytes(c[4]))
        if got != payload_hash:
            return self._bad("ModelEnd key=%x payloadHash %016x != computed %016x" % (key, payload_hash, got))
        model, err = decode_real_model(c[3], bytes(c[4]))
        if err:
            return self._bad("model key=%x payload: %s" % (key, err))
        self.models[key] = model
        self.complete.add(key)
        self.completed_real.append(key)
        return key

# ----------------------------------------------------------------------------------------------------------------
# Grids and parts (C4)
# ----------------------------------------------------------------------------------------------------------------

GRID_BASE = 0x0044450000000000
PARTS_PER_GRID = 6

def grid_id(serial: int) -> int:
    return GRID_BASE + serial

def part_key(entity_id: int, i: int) -> int:
    return ((entity_id & 0xFFFFFFFFFFFF) << 8) | (i & 0xFF)

N_SCENE_MODELS = 12   # the scene uses the first 12 catalogue models (both sides know it)
SCENE_MODELS = MODEL_KEYS[:N_SCENE_MODELS]

def part_model(entity_id: int, i: int) -> int:
    return SCENE_MODELS[((entity_id & 0xFFFF) + i) % N_SCENE_MODELS]

def grid_add(entity_id: int, world_epoch: int) -> bytes:
    return R["GridAdd"].pack(entityId=entity_id, targetClass=1 + (entity_id & 1), flags=0, parentEntityId=0,
                             worldEpoch=world_epoch)

def part_add(entity_id: int, i: int) -> bytes:
    s = entity_id & 0xFFFF
    return R["PartAdd"].pack(entityId=entity_id, partKey=part_key(entity_id, i),
                             modelKey=part_model(entity_id, i),
                             local=(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 2.5 * i, float(s % 100), 0.5),
                             colorMaskHSV=(i / 8.0, 0.5, 0.25), uvOffset=(0.0, 0.0), skinId=0, flags=i)

def part_recolor(entity_id: int, i: int, k: int) -> bytes:
    return R["PartRecolor"].pack(entityId=entity_id, partKey=part_key(entity_id, i), hsv=((k % 8) / 8.0, 0.5, 0.75))

def entity_remove(entity_id: int) -> bytes:
    return R["EntityRemove"].pack(entityId=entity_id)

def clear_all(world_epoch: int, link_epoch: int) -> bytes:
    return R["ClearAll"].pack(worldEpoch=world_epoch, linkEpoch=link_epoch)

class Mirror:
    """C4 consumer with validation (the host's mirrored grids and parts)."""

    def __init__(self, real: bool = False):
        self.real = real               # S3: accept grids and parts outside the S0 GRID_BASE block (module docstring)
        self.grids = {}
        self.parts = {}
        self.part_info = {}            # S3: (entityId, partKey) -> PartAdd dict of a real part
        self.version = 0               # S3: bumped on every change of grids or parts (the host rebuilds its view)
        self.misparse = 0
        self.orphans = 0
        self.unexpected = 0
        self.clear_alls = 0
        self.messages = 0
        self.details = []

    def _bad(self, what):
        self.misparse += 1
        if len(self.details) < 20:
            self.details.append(what)

    def clear(self):
        self.grids.clear()
        self.parts.clear()
        self.part_info.clear()
        self.version += 1

    def _is_real(self, eid: int) -> bool:
        return self.real and (eid & ~0xFFFFFFFF) != GRID_BASE

    def feed(self, mtype: int, payload: bytes, world_epoch: int):
        self.messages += 1
        t = L.C4_TYPES
        if mtype == t["GridAdd"]:
            if len(payload) != 32:
                return self._bad("GridAdd size %d" % len(payload))
            v = R["GridAdd"].unpack(payload)
            eid = v["entityId"]
            if self._is_real(eid):
                if eid == 0 or v["targetClass"] not in L.CONSTS["GridClass"].values():
                    return self._bad("GridAdd %x targetClass %d" % (eid, v["targetClass"]))
                self.grids[eid] = v["worldEpoch"]
                self.version += 1
                return None
            if (eid & ~0xFFFFFFFF) != GRID_BASE or payload != grid_add(eid, v["worldEpoch"]):
                return self._bad("GridAdd content %x" % eid)
            self.grids[eid] = v["worldEpoch"]
            return None
        if mtype == t["PartAdd"]:
            if len(payload) != 100:
                return self._bad("PartAdd size %d" % len(payload))
            v = R["PartAdd"].unpack(payload)
            eid, pk = v["entityId"], v["partKey"]
            if self._is_real(eid):
                err = real_part_error(v)
                if err:
                    return self._bad("PartAdd %x/%x: %s" % (eid, pk, err))
                if eid not in self.grids:
                    self.orphans += 1
                self.parts[(eid, pk)] = (eid, v["modelKey"])
                self.part_info[(eid, pk)] = v
                self.version += 1
                return None
            i = pk & 0xFF
            mk = v["modelKey"]
            if (eid & ~0xFFFFFFFF) != GRID_BASE or pk != part_key(eid, i) or payload != part_add(eid, i):
                return self._bad("PartAdd content %x/%x" % (eid, pk))
            if eid not in self.grids:
                self.orphans += 1
            self.parts[pk] = (eid, mk)
            return None
        if mtype == t["PartRecolor"]:
            if len(payload) != 32:
                return self._bad("PartRecolor size %d" % len(payload))
            v = R["PartRecolor"].unpack(payload)
            eid, pk = v["entityId"], v["partKey"]
            if self._is_real(eid):
                if not all(_finite(x) for x in v["hsv"]):
                    return self._bad("PartRecolor %x/%x hsv not finite" % (eid, pk))
                if (eid, pk) in self.part_info:
                    self.part_info[(eid, pk)] = dict(self.part_info[(eid, pk)], colorMaskHSV=v["hsv"])
                    self.version += 1
                else:
                    self.orphans += 1
                return None
            h = v["hsv"]
            k = int(round(h[0] * 8))
            if pk != part_key(eid, pk & 0xFF) or payload != part_recolor(eid, pk & 0xFF, k):
                return self._bad("PartRecolor content %x/%x" % (eid, pk))
            return None
        if mtype == t["EntityRemove"]:
            if len(payload) != 8:
                return self._bad("EntityRemove size %d" % len(payload))
            eid = R["EntityRemove"].unpack(payload)["entityId"]
            self.grids.pop(eid, None)
            for pk in [pk for pk, (e, _m) in self.parts.items() if e == eid]:
                del self.parts[pk]
                self.part_info.pop(pk, None)
            self.version += 1
            return None
        if mtype == t["ClearAll"]:
            if len(payload) != 8:
                return self._bad("ClearAll size %d" % len(payload))
            self.clear_alls += 1
            self.clear()
            return R["ClearAll"].unpack(payload)
        if mtype == t["PartRemove"] and self.real:
            if len(payload) != 16:
                return self._bad("PartRemove size %d" % len(payload))
            v = R["PartRemove"].unpack(payload)
            key = (v["entityId"], v["partKey"])
            if key in self.parts:
                del self.parts[key]
                self.part_info.pop(key, None)
                self.version += 1
            else:
                self.orphans += 1
            return None
        if mtype in (t["PartRemove"], t["SkinSet"], t["RoomSet"], t["CharacterAdd"]):
            self.unexpected += 1
            return None
        return self._bad("unknown C4 type %d" % mtype)

    def pending_parts(self, complete_models, models=None, materials=None) -> int:
        """Parts whose model has not ended; with S3's models/materials also parts whose submesh materials are not all
 defined (the SN plugin's C7 pendingParts)."""
        n = 0
        for (_e, mk) in self.parts.values():
            if mk not in complete_models:
                n += 1
            elif models is not None and mk in models and materials is not None and \
                    any(sm["materialKey"] not in materials for sm in models[mk]["submeshes"]):
                n += 1
        return n

# ----------------------------------------------------------------------------------------------------------------
# S3 real-model validation (CONTRACT 5.4 "C3/C4 conventions for S3"; the SN plugin's MirrorWire rules)
# ----------------------------------------------------------------------------------------------------------------

def _finite(x) -> bool:
    return x == x and x not in (float("inf"), float("-inf"))

def real_stride(flags: int) -> int:
    return 76 if flags & L.CONSTS["ModelFlags"]["Skinned"] else 56

def real_begin_error(v: dict):
    """None when a real ModelBegin is self-consistent, else the reason."""
    nv, ni, ns = v["vertexCount"], v["indexCount"], v["submeshCount"]
    if ni == 0 or ni % 3:
        return "indexCount %d is not a positive multiple of 3" % ni
    if nv > 16 << 20 or ni > 64 << 20 or ns > 4096:
        return "counts beyond the plugin's limits"
    if v["flags"] & ~0x3:
        return "unknown flag bits %x" % v["flags"]
    expected = nv * real_stride(v["flags"]) + ni * 4 + ns * 24
    if v["totalBytes"] != expected:
        return "totalBytes %d != %d for the counts" % (v["totalBytes"], expected)
    if not 1 <= v["chunkCount"] <= v["totalBytes"]:
        return "chunkCount %d for %d B" % (v["chunkCount"], v["totalBytes"])
    b = v["bounds"]
    if not all(_finite(x) for x in b) or any(b[k] > b[3 + k] for k in range(3)):
        return "bounds %s (min xyz, max xyz)" % (list(b),)
    if not (_finite(v["patternScale"]) and v["patternScale"] > 0):
        return "patternScale %r" % v["patternScale"]
    return None

def decode_real_model(begin: dict, payload: bytes):
    """(summary dict, None) or (None, reason): positions/normals (SE model space), triangles in MyModel.Triangles order,
 submeshes {materialKey, triStart, triCount, drawTechnique}."""
    nv, ni, ns, flags = begin["vertexCount"], begin["indexCount"], begin["submeshCount"], begin["flags"]
    stride = real_stride(flags)
    pos, nrm, tan, bin_, uv = [], [], [], [], []
    for i in range(nv):
        f = struct.unpack_from("<14f", payload, i * stride)
        if not all(_finite(x) for x in f[0:3]):
            return None, "non-finite position at vertex %d" % i
        pos.append(f[0:3])
        nrm.append(f[3:6])
        tan.append(f[6:9])
        bin_.append(f[9:12])
        uv.append(f[12:14])
    off = nv * stride
    idx = struct.unpack_from("<%dI" % ni, payload, off)
    bad = next((k for k, x in enumerate(idx) if x >= nv), None)
    if bad is not None:
        return None, "index %d at %d >= vertexCount %d" % (idx[bad], bad, nv)
    off += ni * 4
    tris = ni // 3
    subs = []
    for i in range(ns):
        key, start, count, tech, _pad = struct.unpack_from("<QIIII", payload, off + 24 * i)
        if start + count > tris:
            return None, "submesh %d triangles [%d, +%d) outside %d" % (i, start, count, tris)
        subs.append({"materialKey": key, "triStart": start, "triCount": count, "drawTechnique": tech})
    return {"key": begin["modelKey"], "verts": nv, "tris": tris, "flags": flags, "bounds": tuple(begin["bounds"]),
            "patternScale": begin["patternScale"], "positions": pos, "normals": nrm, "tangents": tan, "binormals": bin_,
            "uvs": uv, "triangles": [tuple(idx[k:k + 3]) for k in range(0, ni, 3)], "submeshes": subs}, None

def decode_material_def(payload: bytes):
    """(dict, None) or (None, reason): header + four u16-prefixed strict UTF-8 strings, exact length."""
    if len(payload) < 16 + 8:
        return None, "%d B (header 16 + four u16 lengths)" % len(payload)
    m = R["MaterialDefHeader"].unpack(payload)
    pos, strings = 16, []
    for i in range(4):
        if pos + 2 > len(payload):
            return None, "string %d length beyond the payload" % i
        n = struct.unpack_from("<H", payload, pos)[0]
        if pos + 2 + n > len(payload):
            return None, "string %d (%d B) beyond the payload" % (i, n)
        try:
            strings.append(payload[pos + 2:pos + 2 + n].decode("utf-8", "strict"))
        except UnicodeDecodeError as e:
            return None, "string %d not UTF-8 (%s)" % (i, e)
        pos += 2 + n
    if pos != len(payload):
        return None, "%d trailing bytes" % (len(payload) - pos)
    m["paths"] = strings
    return m, None

def real_part_error(v: dict):
    """None when a real PartAdd is finite and its local rotation rigid (CONTRACT 5.4: PartAdd local is rigid)."""
    vals = list(v["local"]) + list(v["colorMaskHSV"]) + list(v["uvOffset"])
    if not all(_finite(x) for x in vals):
        return "non-finite field"
    r = v["local"]
    rows = (r[0:3], r[3:6], r[6:9])
    for i in range(3):
        for j in range(3):
            d = sum(rows[i][k] * rows[j][k] for k in range(3))
            if abs(d - (1.0 if i == j else 0.0)) > 1e-3:
                return "local rotation rows not orthonormal (%d.%d = %.6f)" % (i, j, d)
    if v["modelKey"] == 0:
        return "modelKey 0"
    return None

# ----------------------------------------------------------------------------------------------------------------
# Damage records (C8)
# ----------------------------------------------------------------------------------------------------------------

def damage_record(gen: int, seq: int, world_epoch: int) -> bytes:
    return R["DamageRecord"].pack(
        dedupSeq=seq, snGeneration=gen, unit=L.DAMAGE_UNIT_SN_HP, amount=float(seq % 997) + 0.5, seScaleExpected=25.0,
        snDamageType=seq % 7, flags=seq & 0xF, targetKind=1 + (seq & 1), sectionId=seq % 13,
        targetEntityId=GRID_BASE + 1 + seq % 5, rootEntityId=GRID_BASE + 1, snapshotTick=seq * 2,
        localHit=(float(seq % 10), 1.0, 2.0), snWorldHit=(0.5, -100.0, 0.25), attackDirSn=(0.0, 0.0, 1.0),
        dealerTechType=42, dealerInstanceId=7, worldEpoch=world_epoch, snTimeSec=seq * 0.25)

class DamageChecker:
    """SE-side C8 validation: content from (snGeneration, dedupSeq); dedupSeq +1 within a generation (gaps are
 overflow drops, counted, not misparses); a record of another generation than the current peer is stale."""

    def __init__(self):
        self.gen = None
        self.last_seq = None
        self.records = 0
        self.misparse = 0
        self.stale = 0
        self.gaps = 0
        self.details = []

    def reset(self, gen: int):
        self.gen = gen
        self.last_seq = None

    def feed(self, rec: bytes):
        self.records += 1
        v = R["DamageRecord"].unpack(rec)
        if rec != damage_record(v["snGeneration"], v["dedupSeq"], v["worldEpoch"]):
            self.misparse += 1
            if len(self.details) < 20:
                self.details.append("damage content seq=%d gen=%d" % (v["dedupSeq"], v["snGeneration"]))
            return
        if self.gen is not None and v["snGeneration"] != self.gen:
            self.stale += 1
            if len(self.details) < 20:
                self.details.append("damage of generation %d while the peer is %d" % (v["snGeneration"], self.gen))
            return
        if self.last_seq is not None and v["dedupSeq"] != self.last_seq + 1:
            if v["dedupSeq"] <= self.last_seq:
                self.misparse += 1
                if len(self.details) < 20:
                    self.details.append("damage dedupSeq went back %d -> %d" % (self.last_seq, v["dedupSeq"]))
            else:
                self.gaps += v["dedupSeq"] - self.last_seq - 1
        self.last_seq = v["dedupSeq"]

def site_hash() -> int:
    """siteHash of CONTRACT §3, recomputed from tools/worldmap/site_v1.py when importable (else the pinned value)."""
    try:
        wm = os.path.normpath(os.path.join(_HERE, "..", "..", "tools", "worldmap"))
        if wm not in sys.path:
            sys.path.insert(0, wm)
        import site_v1  # noqa
        text = json.dumps(site_v1.record(), sort_keys=True, separators=(",", ":")).encode("utf-8")
        return L.fnv1a64(text)
    except Exception:
        return L.SITE_HASH_V1
