"""C1 SeFrame oracle for tests/DeepEngineers.SE.Tests (FrameComposerTests): the expected C1 field values computed with
the independent stdlib references tools/worldmap/wire_ref.py and worldmap_ref.py (never with the C# code).

Usage: python -I c1_oracle.py IN.json OUT.json

IN.json:
 {"site": "v1" | {"C": [x, y, z], "Rs": r}, # "v1" = site_v1.SITE_V1; a dict = the site-v1 anchor around C, Rs
 "cases": [{"id": "...",
 "cam": [[Rx,Ry,Rz],[Ux,Uy,Uz],[Bx,By,Bz],[Tx,Ty,Tz]], # SE camera world matrix rows
 "fovRad": 1.22,
 "char": null | {"m": rows, "members": [{"m": rows, "min": [..], "max": [..]},...], "pelvis": [x, y, z]},
 "se_points": [[x, y, z],...], # optional: F(P)
 "sn_points": [[x, y, z],...]}]} # optional: F^-1(Q)
OUT.json: {"cases": [{"id", "camPos", "camRot", "vFovDeg", "pivot", "charO", "charQ", "charS", "pelvisSn",
 "se_points", "sn_points"}]}
P* (CONTRACT 2.4) = the point of the members' boxes nearest to the camera, ties to the lowest index (the camera itself when
inside a box); with no members, the character's own position.
"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
WORLDMAP = os.path.normpath(os.path.join(HERE, "..", "..", "tools", "worldmap"))
sys.path.insert(0, WORLDMAP)  # python -I drops the script folder; the oracles live in tools/worldmap

import worldmap_ref as wm  # noqa: E402
import site_v1  # noqa: E402
import wire_ref  # noqa: E402

def make_site(spec):
    if spec == "v1":
        return site_v1.SITE_V1
    a = site_v1.ANCHOR
    return wm.Site(C=tuple(spec["C"]), Rs=float(spec["Rs"]), U0=a["U0"], N0=a["N0"], psi_deg=a["psi_deg"], O=a["O"])

def rows(m):
    return tuple(tuple(float(c) for c in r) for r in m)

def dist2(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b))

def main(argv):
    if len(argv) != 3:
        print(__doc__)
        return 2
    with open(argv[1], "r", encoding="utf-8") as fh:
        spec = json.load(fh)
    site = make_site(spec["site"])
    out = []
    for c in spec["cases"]:
        cam = rows(c["cam"])
        pos, rot = wire_ref.camera_pose(site, cam)
        r = {"id": c["id"], "camPos": list(pos), "camRot": list(rot),
             "vFovDeg": float(c["fovRad"]) * 180.0 / math.pi}
        ch = c.get("char")
        if ch:
            m = rows(ch["m"])
            members = ch.get("members") or []
            if members:
                best, best_d = None, None
                for mem in members:
                    p = wire_ref.pivot_nearest(rows(mem["m"]), tuple(mem["min"]), tuple(mem["max"]), cam[3])
                    d = dist2(p, cam[3])
                    if best is None or d < best_d:
                        best, best_d = p, d
                pivot = best
            else:
                pivot = m[3]
            o, q, s = wire_ref.wire_pose(site, m, pivot)
            r.update({"pivot": list(pivot), "charO": list(o), "charQ": list(q), "charS": s,
                      "pelvisSn": list(site.se_to_sn(tuple(ch["pelvis"])))})
        r["se_points"] = [list(site.se_to_sn(tuple(p))) for p in c.get("se_points", [])]
        r["sn_points"] = [list(site.sn_to_se(tuple(p))) for p in c.get("sn_points", [])]
        out.append(r)
    with open(argv[2], "w", encoding="utf-8") as fh:
        json.dump({"cases": out}, fh)
    print("C1 ORACLE OK cases=%d" % len(out))
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv))
