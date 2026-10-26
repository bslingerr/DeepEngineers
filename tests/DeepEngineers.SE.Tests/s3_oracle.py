"""S3 oracle for tests/DeepEngineers.SE.Tests (S3Tests): recomputes, independently of the C# code, what the SE producer
puts on the wire and in its JSONL pose rows, with the stdlib references tests/wire/layout_py.py (record layouts, FNV-1a)
and tools/worldmap/wire_ref.py (the wire pose).

Usage: python -I s3_oracle.py IN.json

IN.json (written by S3Tests; u64 values as "0x..." strings, floats as G17 doubles of the float32 values):
 {"site": "v1",
 "keys": [{"text": "...", "key": "0x..."}], FNV-1a 64 of the UTF-8 text
 "uv": [{"w5": n, "w6": n, "w7": n, "u": f, "v": f}], SE's shader pattern offset (VertexTemplateBase.hlsli)
 "records": [{"record": "PartAdd", "fields": {...}, "hex": "..."}], layout_py.RECORDS[record].pack(**fields) == bytes
 "model": {"modelKey", "patternScale", "hasTangents", "positions", "normals", "tangents", "binormals", "uvs",
 "indices", "submeshes": [{"materialKey", "triStart", "triCount", "drawTechnique"}], "chunkBytes",
 "messages": [{"type": n, "hex": "..."}]},
 "material": {"materialKey", "drawTechnique", "flags", "strings": [cm, ng, add, alpha], "hex": "..."},
 "poses": [<s3 pose rows as the plugin writes them>]}
Prints "S3 ORACLE OK <n> checks" and exits 0, or lists the failures and exits 1.
"""
import json
import math
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
for p in (os.path.join(REPO, "tests", "wire"), os.path.join(REPO, "tools", "worldmap")):
    sys.path.insert(0, p)  # python -I drops the script folder

import layout_py as L  # noqa: E402
import site_v1  # noqa: E402
import wire_ref  # noqa: E402
import worldmap_ref as wm  # noqa: E402

R = L.RECORDS
FAILS = []
CHECKS = [0]

def check(cond, what):
    CHECKS[0] += 1
    if not cond:
        FAILS.append(what)

def u64(v):
    return int(v, 16) if isinstance(v, str) else int(v)

def f32(x):
    return struct.unpack("<f", struct.pack("<f", float(x)))[0]

def make_site(spec):
    if spec == "v1":
        return site_v1.SITE_V1
    a = site_v1.ANCHOR
    return wm.Site(C=tuple(spec["C"]), Rs=float(spec["Rs"]), U0=a["U0"], N0=a["N0"], psi_deg=a["psi_deg"], O=a["O"])

def fields_for(record, fields):
    out = {}
    for k, v in fields.items():
        f = R[record].field(k)
        if f.base in ("u64",) and isinstance(v, str):
            out[k] = u64(v)
        elif f.count > 1:
            out[k] = tuple(v)
        else:
            out[k] = v
    return out

def check_keys(keys):
    for k in keys:
        got = L.fnv1a64(k["text"].encode("utf-8"))
        check(got == u64(k["key"]), "key of %r: python 0x%016x, C# %s" % (k["text"], got, k["key"]))

def check_uv(cases):
    for c in cases:
        w5, w6, w7 = c["w5"], c["w6"], c["w7"]
        # SE's VertexTemplateBase.hlsli (USE_DEFORMED_CUBE_INSTANCING): offsetU = w5, offsetV = w6,
 # patternU = 1 + ((w7 >> 4) & 0xF), patternV = 1 + (w7 & 0xF); uv += (offsetU / patternU, offsetV / patternV)
        u = f32(w5 / (1 + ((w7 >> 4) & 0xF)))
        v = f32(w6 / (1 + (w7 & 0xF)))
        check(u == c["u"] and v == c["v"], "uv offset of (%d, %d, %d): python (%r, %r), C# (%r, %r)" % (w5, w6, w7, u, v, c["u"], c["v"]))

def check_records(recs):
    for r in recs:
        name = r["record"]
        want = R[name].pack(**fields_for(name, r["fields"]))
        got = bytes.fromhex(r["hex"])
        check(want == got, "%s bytes differ:\n  python %s\n  C#     %s" % (name, want.hex(), got.hex()))

def check_model(m):
    v = len(m["positions"]) // 3
    ni = len(m["indices"])
    payload = bytearray()
    for i in range(v):
        payload += struct.pack("<14f", *(m["positions"][3 * i:3 * i + 3] + m["normals"][3 * i:3 * i + 3] +
                                         m["tangents"][3 * i:3 * i + 3] + m["binormals"][3 * i:3 * i + 3] +
                                         m["uvs"][2 * i:2 * i + 2]))
    for i in m["indices"]:
        payload += struct.pack("<I", i)
    for s in m["submeshes"]:
        payload += R["ModelSubmesh"].pack(materialKey=u64(s["materialKey"]), triStart=s["triStart"], triCount=s["triCount"],
                                          drawTechnique=s["drawTechnique"])
    msgs = m["messages"]
    check(len(msgs) >= 3, "model: at least Begin, one chunk, End")
    check(msgs[0]["type"] == L.C3_TYPES["ModelBegin"] and msgs[-1]["type"] == L.C3_TYPES["ModelEnd"], "model message types")
    begin = R["ModelBegin"].unpack(bytes.fromhex(msgs[0]["hex"]))
    chunk_msgs = msgs[1:-1]
    cb = m["chunkBytes"]
    want_chunks = max(1, (len(payload) + cb - 1) // cb)
    lo = [min(f32(m["positions"][3 * i + k]) for i in range(v)) for k in range(3)]
    hi = [max(f32(m["positions"][3 * i + k]) for i in range(v)) for k in range(3)]
    check(begin["modelKey"] == u64(m["modelKey"]) and begin["lod"] == 0 and begin["vertexCount"] == v and
          begin["indexCount"] == ni and begin["submeshCount"] == len(m["submeshes"]), "ModelBegin counts %r" % (begin,))
    check(begin["flags"] == (L.CONSTS["ModelFlags"]["HasTangents"] if m["hasTangents"] else 0), "ModelBegin flags %d" % begin["flags"])
    check(list(begin["bounds"]) == lo + hi, "ModelBegin bounds %r != %r" % (begin["bounds"], lo + hi))
    check(begin["patternScale"] == f32(m["patternScale"]), "ModelBegin patternScale")
    check(begin["chunkCount"] == want_chunks == len(chunk_msgs), "chunk count %d / %d / %d" % (begin["chunkCount"], want_chunks, len(chunk_msgs)))
    check(begin["totalBytes"] == len(payload), "totalBytes %d != %d" % (begin["totalBytes"], len(payload)))
    joined = bytearray()
    for idx, cmsg in enumerate(chunk_msgs):
        b = bytes.fromhex(cmsg["hex"])
        h = R["ModelChunkHeader"].unpack(b)
        check(cmsg["type"] == L.C3_TYPES["ModelChunk"] and h["modelKey"] == u64(m["modelKey"]) and h["chunkIndex"] == idx and
              h["bytes"] == len(b) - 16 and h["bytes"] <= cb, "chunk %d header %r" % (idx, h))
        joined += b[16:]
    check(bytes(joined) == bytes(payload), "concatenated chunks == the payload rebuilt in python (%d vs %d bytes)" % (len(joined), len(payload)))
    end = R["ModelEnd"].unpack(bytes.fromhex(msgs[-1]["hex"]))
    check(end["modelKey"] == u64(m["modelKey"]) and end["payloadHash"] == L.fnv1a64(bytes(payload)), "ModelEnd hash")
    for k in range(ni):
        check(0 <= m["indices"][k] < v, "index range")
    # every framed message fits half of the C3 ring (CONTRACT §5.3)
    for msg in msgs:
        check(L.framed_size(len(bytes.fromhex(msg["hex"]))) <= L.REGION["C3"].size // 2, "C3 message fits half the ring")

def check_material(m):
    want = R["MaterialDefHeader"].pack(materialKey=u64(m["materialKey"]), drawTechnique=m["drawTechnique"], flags=m["flags"])
    for s in m["strings"]:
        b = s.encode("utf-8")
        want += struct.pack("<H", len(b)) + b
    check(want == bytes.fromhex(m["hex"]), "MaterialDef bytes:\n  python %s\n  C#     %s" % (want.hex(), m["hex"]))

def check_poses(site, rows):
    for row in rows:
        mrows = row["seMatrix"]
        m = (tuple(mrows[0:3]), tuple(mrows[3:6]), tuple(mrows[6:9]), tuple(mrows[9:12]))
        pivot = tuple(row["pivot"])
        o, q, s = wire_ref.wire_pose(site, m, pivot)
        # P* (CONTRACT 2.4): the point of the grid box nearest to the camera, recomputed
        p2 = wire_ref.pivot_nearest(m, tuple(row["aabbMin"]), tuple(row["aabbMax"]), tuple(row["camSe"]))
        check(max(abs(a - b) for a, b in zip(p2, pivot)) <= 1e-6, "pivot %r vs python %r" % (pivot, p2))
        check(all(abs(f32(a) - b) <= 2e-4 for a, b in zip(o, row["o"])), "o %r vs python %r" % (row["o"], o))
        qd = min(math.sqrt(sum((f32(a) - b) ** 2 for a, b in zip(q, row["q"]))),
                 math.sqrt(sum((f32(a) + b) ** 2 for a, b in zip(q, row["q"]))))
        check(qd <= 1e-6 and row["q"][3] >= 0, "q %r vs python %r" % (row["q"], q))
        check(abs(f32(s) - row["s"]) <= 1e-7, "s %r vs python %r" % (row["s"], s))
        check(row["k"] == 1.0, "k")
        # the 8 AABB corners in SN space (S3.C2's quantity) agree between the two poses to 1 mm
        lo, hi = row["aabbMin"], row["aabbMax"]
        for c in range(8):
            l = (hi[0] if c & 1 else lo[0], hi[1] if c & 2 else lo[1], hi[2] if c & 4 else lo[2])
            a = wire_ref.sn_apply(o, q, s, l)
            b = wire_ref.sn_apply(tuple(row["o"]), tuple(row["q"]), row["s"], l)
            check(math.dist(a, b) <= 1e-3, "corner %d: %r vs %r" % (c, a, b))

def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2
    with open(argv[1], "r", encoding="utf-8") as fh:
        spec = json.load(fh)
    site = make_site(spec.get("site", "v1"))
    check_keys(spec.get("keys", []))
    check_uv(spec.get("uv", []))
    check_records(spec.get("records", []))
    if spec.get("model"):
        check_model(spec["model"])
    if spec.get("material"):
        check_material(spec["material"])
    check_poses(site, spec.get("poses", []))
    if FAILS:
        for f in FAILS[:40]:
            print("FAIL " + f)
        print("S3 ORACLE FAIL %d of %d checks" % (len(FAILS), CHECKS[0]))
        return 1
    print("S3 ORACLE OK %d checks" % CHECKS[0])
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv))
