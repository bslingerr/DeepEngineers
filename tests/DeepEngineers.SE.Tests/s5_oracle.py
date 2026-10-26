"""S5 oracle for tests/DeepEngineers.SE.Tests (S5Tests): recomputes, independently of the C# code, what the SE producer
puts on the wire for slice S5 "Whole grids + character", with the stdlib references tests/wire/layout_py.py (record
layouts, the layout hash) and tools/worldmap/wire_ref.py (the S5 mirror conventions, keys and the subgrid relative
matrix), plus an independent re-implementation of the character split and of the.derec reader.

Usage: python -I s5_oracle.py IN.json

IN.json (written by S5Tests; u64 values as "0x..." strings, floats as G17 doubles of the float32 values):
 {"keys": [{"kind": "fat", "min": [x, y, z], "key": "0x.."},
 {"kind": "sub", "fatKey": "0x..", "names": ["a", "b"], "key": "0x.."},
 {"kind": "cube" | "edge", "modelKey": "0x..", "t": [tx, ty, tz, rot], "key": "0x.."},
 {"kind": "model", "path": "...", "scale": s, "skin": null | "...", "char": 0 | 1 | 2, "key": "0x.."},
 {"kind": "skin", "skin": "...", "id": n}],
 "flags": [{"cls": n, "extra": n, "flags": n}],
 "records": [{"record": "PartAdd" | "GridAdd" | "CharacterAdd", "fields": {...}, "hex": "..."}],
 "split": {"model": {...}, "head": ["names"], "body": {...} | null, "headPart": {...} | null},
 "relative": [{"child": [12], "parent": [12], "row": [12 floats]}],
 "recording": {"path": "...", "sidecar": "...", "kinds": [n,...]}}
Prints "S5 ORACLE OK <n> checks" and exits 0, or lists the failures and exits 1.
"""
import hashlib
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
for p in (os.path.join(REPO, "tests", "wire"), os.path.join(REPO, "tools", "worldmap")):
    sys.path.insert(0, p)  # python -I drops the script folder

import layout_py as L  # noqa: E402
import wire_ref as W  # noqa: E402

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

# ---------------------------------------------------------------------------------------------------------- keys

def check_keys(keys):
    for k in keys:
        kind = k["kind"]
        if kind == "fat":
            want = W.s5_text_key(W.s5_fat_key_text(*k["min"]))
        elif kind == "sub":
            want = W.s5_text_key(W.s5_subpart_key_text(u64(k["fatKey"]), k["names"]))
        elif kind == "cube":
            want = W.s5_cube_key(u64(k["modelKey"]), *[f32(x) for x in k["t"]])
        elif kind == "edge":
            want = W.s5_edge_key(u64(k["modelKey"]), *[f32(x) for x in k["t"]])
        elif kind == "model":
            want = W.s5_model_key(k["path"], 0, k.get("scale", 1.0), k.get("skin"), k.get("char", 0))
        elif kind == "skin":
            check(W.s5_skin_id(k["skin"]) == k["id"], "skinId(%r) python 0x%08x C# 0x%08x" % (k["skin"], W.s5_skin_id(k["skin"]), k["id"]))
            continue
        else:
            check(False, "unknown key kind " + kind)
            continue
        got = u64(k["key"])
        check(want == got, "%s key %s: python 0x%016x C# 0x%016x" % (kind, json.dumps(k), want, got))

def check_flags(flags):
    for f in flags:
        want = W.s5_part_flags(f["cls"], f["extra"])
        check(want == f["flags"], "part flags cls %d extra %d: python %d C# %d" % (f["cls"], f["extra"], want, f["flags"]))

# ---------------------------------------------------------------------------------------------------------- records

def fields_for(name, fields):
    out = {}
    for k, v in fields.items():
        f = R[name].field(k)
        if f.count > 1:
            out[k] = [f32(x) if f.base == "f32" else int(x) for x in v]
        elif f.base in ("u64", "i64"):
            out[k] = u64(v)
        elif f.base == "f32":
            out[k] = f32(v)
        else:
            out[k] = int(v)
    return out

def check_records(recs):
    for r in recs:
        name = r["record"]
        want = R[name].pack(**fields_for(name, r["fields"]))
        got = bytes.fromhex(r["hex"])
        check(want == got, "%s bytes differ:\n  python %s\n  C#     %s" % (name, want.hex(), got.hex()))

# ---------------------------------------------------------------------------------------------------------- split

def split_part(m, head_names, want_head):
    """Each triangle belongs to the first submesh covering it; a part's triangles grouped by submesh in order, vertices
 renumbered in first-use order; submeshes owning no triangle dropped; triangles outside every range go to the body."""
    subs = m["submeshes"]
    ntri = len(m["indices"]) // 3
    owner = []
    for t in range(ntri):
        o = -1
        for s, sm in enumerate(subs):
            if sm["triStart"] <= t < sm["triStart"] + sm["triCount"]:
                o = s
                break
        owner.append(o)
    is_head = [sm["name"] is not None and sm["name"].lower() in head_names for sm in subs]
    tris, ranges = [], []
    for s, sm in enumerate(subs):
        if is_head[s] != want_head:
            continue
        start = len(tris)
        tris += [t for t in range(ntri) if owner[t] == s]
        if len(tris) > start:
            ranges.append({"materialKey": sm["materialKey"], "triStart": start, "triCount": len(tris) - start, "name": sm["name"]})
    if not want_head:
        tris += [t for t in range(ntri) if owner[t] < 0]
    if not tris:
        return None
    remap, order, idx = {}, [], []
    for t in tris:
        for e in range(3):
            v = m["indices"][3 * t + e]
            if v not in remap:
                remap[v] = len(order)
                order.append(v)
            idx.append(remap[v])
    pos = [f32(m["positions"][3 * v + k]) for v in order for k in range(3)]
    bi = [m["boneIndices"][4 * v + k] for v in order for k in range(4)]
    bw = [f32(m["boneWeights"][4 * v + k]) for v in order for k in range(4)]
    return {"positions": pos, "indices": idx, "submeshes": ranges, "boneIndices": bi, "boneWeights": bw}

def check_split(sp):
    m = sp["model"]
    heads = set(h.lower() for h in sp["head"])
    for key, want_head in (("body", False), ("headPart", True)):
        want = split_part(m, heads, want_head)
        got = sp.get(key)
        if want is None or got is None:
            check(want is None and got is None, "split %s: python %s, C# %s" % (key, "none" if want is None else "a part", "none" if got is None else "a part"))
            continue
        check(want["indices"] == got["indices"], "split %s indices differ" % key)
        check(want["positions"] == [f32(x) for x in got["positions"]], "split %s positions differ" % key)
        check(want["boneIndices"] == got["boneIndices"], "split %s bone indices differ" % key)
        check(want["boneWeights"] == [f32(x) for x in got["boneWeights"]], "split %s bone weights differ" % key)
        ws = [(s["materialKey"], s["triStart"], s["triCount"], s["name"]) for s in want["submeshes"]]
        gs = [(s["materialKey"], s["triStart"], s["triCount"], s["name"]) for s in got["submeshes"]]
        check(ws == gs, "split %s submeshes: python %s C# %s" % (key, ws, gs))

# ---------------------------------------------------------------------------------------------------------- relative

def check_relative(rels):
    for r in rels:
        child = W.s5_matrix(r["child"])
        parent = W.s5_matrix(r["parent"])
        want = [f32(x) for x in W.s5_rows(W.s5_relative(child, parent))]
        got = [f32(x) for x in r["row"]]
        check(want == got, "subgrid row: python %s C# %s" % (want, got))

# ---------------------------------------------------------------------------------------------------------- recording

def check_recording(rec):
    with open(rec["path"], "rb") as fh:
        data = fh.read()
    check(len(data) >= 64, "recording shorter than its header")
    if len(data) < 64:
        return
    (magic, fmt, hdr, layout, conv, wire, sever, created, flags, epoch, reserved) = struct.unpack_from("<8sIIQQIIQIIQ", data, 0)
    check(magic == b"DEREC001", "magic %r" % magic)
    check(fmt == 1 and hdr == 64, "formatVersion %d headerBytes %d" % (fmt, hdr))
    check(layout == L.PINNED_LAYOUT_HASH_1_0, "layoutHash 0x%016x" % layout)
    conv_want = W.fnv1a64(W.s5_conv_text().encode("ascii"))
    check(conv == conv_want, "conventionsHash 0x%016x != 0x%016x" % (conv, conv_want))
    check(reserved == 0, "reserved not 0")
    check(flags == rec.get("flags", flags), "flags %d" % flags)
    kinds = []
    o = hdr
    saw_end = False
    while o + 24 <= len(data):
        kind, pad, n, tick, qpc = struct.unpack_from("<HHIQq", data, o)
        check(pad == 0, "record pad not 0 at %d" % o)
        body = data[o + 24:o + 24 + n]
        padded = (n + 7) & ~7
        check(all(b == 0 for b in data[o + 24 + n:o + 24 + padded]), "record padding not zero at %d" % o)
        o += 24 + padded
        if kind == 0xFFFF:
            saw_end = True
            check(n == 0, "end record carries bytes")
            break
        kinds.append(kind)
        if kind in (1, 2):
            t, pb = struct.unpack_from("<II", body, 0)
            check(pb == n - 8, "message payloadBytes %d != %d" % (pb, n - 8))
        if kind == 5:
            json.loads(body.decode("utf-8"))
    check(saw_end, "no end record")
    check(o == len(data), "bytes after the end record")
    check(kinds == rec["kinds"], "record kinds python %s C# %s" % (kinds, rec["kinds"]))
    if rec.get("sidecar"):
        with open(rec["sidecar"], encoding="utf-8") as fh:
            side = json.load(fh)
        check(side.get("sha256") == hashlib.sha256(data).hexdigest(), "sidecar sha256 differs")
        check(side.get("formatVersion") == 1, "sidecar formatVersion")
        check(side.get("layoutHash") == "0x%016x" % layout and side.get("conventionsHash") == "0x%016x" % conv, "sidecar hashes")
        check(side.get("records") == len(kinds), "sidecar records %s != %d" % (side.get("records"), len(kinds)))

def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    with open(sys.argv[1], encoding="utf-8") as fh:
        spec = json.load(fh)
    check_keys(spec.get("keys", []))
    check_flags(spec.get("flags", []))
    check_records(spec.get("records", []))
    if spec.get("split"):
        check_split(spec["split"])
    check_relative(spec.get("relative", []))
    if spec.get("recording"):
        check_recording(spec["recording"])
    if FAILS:
        for f in FAILS:
            print("FAIL " + f)
        print("S5 ORACLE FAILED %d of %d checks" % (len(FAILS), CHECKS[0]))
        return 1
    print("S5 ORACLE OK %d checks" % CHECKS[0])
    return 0

if __name__ == "__main__":
    sys.exit(main())
