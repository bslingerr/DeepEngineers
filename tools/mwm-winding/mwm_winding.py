"""Read-only winding census of Space Engineers.mwm models (own code, stdlib only).

For every triangle, in the order the file stores it (MeshParts indices, the order MyModel copies into its index
buffer before CopyTriangleIndices swaps the 2nd and 3rd index), compare the geometric normal
cross(V1 - V0, V2 - V0) with the average of the three stored vertex normals. Prints counts only; no game data
is written anywhere. Format and unpacking follow the decompiled SE importer (described in docs/recon/gap-1.md):
 VRageRender.Import.MyModelImporter (tag index for versions >= 1066002), MyMeshPartInfo.Import,
 MyMaterialDescriptor.Read, VRage.Import.PositionPacker.UnpackPosition (half4 xyz * w),
 VRage.Import.VF_Packer.UnpackNormal (Byte4, two 15-bit components + sign).

Usage: python -I mwm_winding.py <file-or-folder> [more...]
"""
import math
import os
import struct
import sys

class R:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.p)[0]
        self.p += 4
        return v

    def u32(self):
        v = struct.unpack_from("<I", self.d, self.p)[0]
        self.p += 4
        return v

    def u64(self):
        v = struct.unpack_from("<Q", self.d, self.p)[0]
        self.p += 8
        return v

    def f32(self):
        v = struct.unpack_from("<f", self.d, self.p)[0]
        self.p += 4
        return v

    def boolean(self):
        return self.u8() != 0

    def string(self):  #.NET BinaryReader.ReadString: 7-bit encoded length, UTF-8
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

def half(u):
    return struct.unpack("<e", struct.pack("<H", u & 0xFFFF))[0]

def unpack_position(packed):
    x, y, z, w = (half(packed >> s) for s in (0, 16, 32, 48))
    return (x * w, y * w, z * w)

def unpack_normal(packed):
    X, Y, Z, W = (packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF, (packed >> 24) & 0xFF)
    sign = 1.0 if Y > 127.5 else -1.0
    if sign > 0:
        Y -= 128
    a = (X + 256.0 * Y) / 32767.0
    b = (Z + 256.0 * W) / 32767.0
    nx, ny = 2 * a - 1, 2 * b - 1
    return (nx, ny, sign * math.sqrt(max(0.0, 1 - nx * nx - ny * ny)))

def read_parts(r, version):
    parts = []
    for _ in range(r.i32()):
        r.i32()  # material hash
        if version < 1052001:
            r.i32()
        idx = [r.i32() for _ in range(r.i32())]
        technique = None
        if r.boolean():
            r.string()  # material name
            if version < 1052002:
                r.string()
                r.string()
            else:
                for _ in range(r.i32()):
                    r.string()
                    r.string()
            if version >= 1068001:
                for _ in range(r.i32()):
                    r.string()
                    r.string()
            if version < 1157001:
                for _ in range(7):
                    r.f32()
            technique = r.string() if version >= 1052001 else str(r.i32())
            if technique == "GLASS":
                if version >= 1043001:
                    r.string()
                    r.string()
                    r.boolean()
                else:
                    for _ in range(4):
                        r.f32()
        parts.append((technique, idx))
    return parts

def load(path):
    with open(path, "rb") as fh:
        r = R(fh.read())
    r.string()
    header = [r.string() for _ in range(r.i32())]
    version = 0
    if header and "Version:" in header[0]:
        version = int(header[0].replace("Version:", ""))
    if version < 1066002:
        return None, version
    index = {}
    for _ in range(r.i32()):
        k = r.string()
        index[k] = r.i32()
    out = {"version": version}

    def at(tag):
        if tag not in index:
            return False
        r.p = index[tag]
        r.string()
        return True
    if at("Vertices"):
        out["v"] = [unpack_position(r.u64()) for _ in range(r.i32())]
    if at("Normals"):
        out["n"] = [unpack_normal(r.u32()) for _ in range(r.i32())]
    if at("MeshParts"):
        out["parts"] = read_parts(r, version)
    if at("SwapWindingOrder"):
        out["swap"] = r.boolean()
    if at("BoundingBox"):
        out["bb"] = [r.f32() for _ in range(6)]
    return out, version

def census(path):
    m, version = load(path)
    if m is None or "v" not in m or "n" not in m or "parts" not in m:
        return None
    v, n = m["v"], m["n"]
    agree = disagree = degenerate = 0
    by_tech = {}
    for tech, idx in m["parts"]:
        for k in range(0, len(idx) - 2, 3):
            a, b, c = idx[k], idx[k + 1], idx[k + 2]
            if max(a, b, c) >= len(v) or max(a, b, c) >= len(n):
                continue
            e1 = [v[b][i] - v[a][i] for i in range(3)]
            e2 = [v[c][i] - v[a][i] for i in range(3)]
            g = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
            avg = [n[a][i] + n[b][i] + n[c][i] for i in range(3)]
            d = sum(g[i] * avg[i] for i in range(3))
            gl = math.sqrt(sum(x * x for x in g))
            al = math.sqrt(sum(x * x for x in avg))
            if gl < 1e-9 or al < 1e-6 or abs(d) < 0.05 * gl * al:
                degenerate += 1
                continue
            t = by_tech.setdefault(tech, [0, 0])
            if d > 0:
                agree += 1
                t[0] += 1
            else:
                disagree += 1
                t[1] += 1
    bb_ok = None
    if "bb" in m:
        lo, hi = m["bb"][:3], m["bb"][3:]
        bb_ok = all(lo[i] - 1e-2 <= p[i] <= hi[i] + 1e-2 for p in v for i in range(3))
    return {"version": version, "verts": len(v), "agree": agree, "disagree": disagree, "degenerate": degenerate,
            "swap": m.get("swap"), "bb_ok": bb_ok, "by_tech": by_tech}

def main(args):
    files = []
    for a in args:
        if os.path.isdir(a):
            for root, _, names in os.walk(a):
                files += [os.path.join(root, x) for x in names if x.lower().endswith(".mwm")]
        else:
            files.append(a)
    tot_a = tot_d = 0
    per_tech = {}
    models = flips = mixed = 0
    for f in sorted(files):
        try:
            c = census(f)
        except Exception as ex:  # noqa: BLE001 - report and continue
            print(f"ERR  {os.path.basename(f)}: {ex}")
            continue
        if c is None or c["agree"] + c["disagree"] == 0:
            continue
        models += 1
        tot_a += c["agree"]
        tot_d += c["disagree"]
        for t, (x, y) in c["by_tech"].items():
            pt = per_tech.setdefault(t, [0, 0])
            pt[0] += x
            pt[1] += y
        frac = c["agree"] / (c["agree"] + c["disagree"])
        if frac > 0.5:
            flips += 1
        if 0.05 < frac < 0.95:
            mixed += 1
        if len(files) <= 40 or frac > 0.5:
            print(f"{os.path.basename(f):48s} v{c['version']} verts {c['verts']:6d} file-order cross agrees with "
                  f"normals {c['agree']:6d} / disagrees {c['disagree']:6d} (degenerate {c['degenerate']}) "
                  f"swapTag={c['swap']} bbox={'ok' if c['bb_ok'] else c['bb_ok']}")
    tot = tot_a + tot_d
    if tot:
        print(f"\nmodels {models}; triangles {tot}; file-order cross(V1-V0,V2-V0) along the vertex normals: "
              f"{tot_a} ({100*tot_a/tot:.2f} %), against: {tot_d} ({100*tot_d/tot:.2f} %)")
        print(f"models where most triangles agree: {flips}; models with 5-95 % mixed: {mixed}")
        for t, (x, y) in sorted(per_tech.items(), key=lambda kv: -(kv[1][0] + kv[1][1])):
            print(f"  technique {t}: agree {x}, against {y}")

if __name__ == "__main__":
    main(sys.argv[1:])
