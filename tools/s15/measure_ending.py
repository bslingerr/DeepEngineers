"""Measure the orbit palette of DE_4546B from frames of Subnautica's ending (4546B through the rocket's window).

 python -I tools/s15/measure_ending.py FRAME [FRAME...] [--json] [--debug SCRATCH_DIR]

this script reads colours from them and prints the numbers the
twin pins (tools/s15/s15_ref.py ORBIT_PALETTE, CONTRACT §5.4 "orbit"); nothing of a frame is written or kept, and no test
needs the frames.

Per frame (downscaled to at most 640 px wide), with the twin's own pixel classes (format air.colour):
 sea the larger of green and blue ahead of red by SeaLeadMin
 frame the rocket's window frame: connected neutral-grey regions larger than FrameFrac of the picture (a cloud's grey
 heart is a small one)
 face pixels off the frame and off space where at least FaceDensity of a box (BoxFrac of the short side) is sea: the
 planet seen through the window, its clouds included
 cloud a face pixel that is not sea with luma >= CloudLuma (the warm white cumulus)
 space luma < SpaceLuma
 rim a pixel of the limb band (a box of space) with luma >= RimLuma: warm (the sunward orange-pink) when red leads blue
 by WarmLead, cool (pale blue-white) when blue leads red by CoolLead and it is not sea
 limb the other face pixels of the limb band (the haze-softened edge of the face)
and prints the sea mean (the disc centre's target), the cloud mean and cover (cloud / face), the limb mean, the rim's warm
and cool means. The summary is the pixel-weighted mean over the frames given.
"""
from __future__ import annotations

import json
import os
import sys

import numpy as np
from PIL import Image
from scipy import ndimage

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
import s15_ref as T  # noqa: E402

MAX_W = 640
BOX_FRAC = 0.16
FACE_DENSITY = 0.05
CLOUD_LUMA = 85.0
SPACE_LUMA = 22.0
WARM_LEAD = 30
COOL_LEAD = 5
RIM_LUMA = 60.0
NEUTRAL = 4
FRAME_FRAC = 0.015

def box_mean(a, r):
    """The mean of a over a (2r+1)^2 box (an integral image; edges clamp)."""
    h, w = a.shape
    p = np.pad(a.astype(np.float64), r, mode="edge")
    c = p.cumsum(0).cumsum(1)
    c = np.pad(c, ((1, 0), (1, 0)))
    k = 2 * r + 1
    s = c[k:k + h, k:k + w] - c[0:h, k:k + w] - c[k:k + h, 0:w] + c[0:h, 0:w]
    return s / float(k * k)

def measure(path, debug_dir=None):
    im = Image.open(path).convert("RGB")
    if im.width > MAX_W:
        im = im.resize((MAX_W, max(1, round(im.height * MAX_W / im.width))), Image.BILINEAR)
    a = np.asarray(im).astype(np.int32)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    luma = 0.2126 * r + 0.7152 * g + 0.0722 * b
    sea = np.maximum(g, b) - r >= T.CO["SeaLeadMin"]
    space = luma < SPACE_LUMA
    grey = (np.abs(r - g) <= NEUTRAL) & (np.abs(g - b) <= NEUTRAL) & (luma >= 50)
    lab, n = ndimage.label(grey)
    sizes = ndimage.sum(grey, lab, index=np.arange(1, n + 1)) if n else np.zeros(0)
    big = np.zeros(n + 1, bool)
    big[1:] = sizes >= FRAME_FRAC * grey.size
    neutral = ndimage.binary_dilation(big[lab], iterations=2)   # the frame and its anti-aliased edge
    rad = max(2, int(BOX_FRAC * min(im.width, im.height) / 2))
    face_d = box_mean(sea, rad)
    by_space = box_mean(space, rad) >= 0.02                      # the limb: the band of the picture next to space
    face = (face_d >= FACE_DENSITY) & ~space & ~neutral
    edge = by_space & ~space & ~neutral & (face_d >= 0.02) & (luma >= RIM_LUMA)
    warm = edge & (r - b >= WARM_LEAD)                           # the sunward rim: orange-pink
    cool = edge & ~warm & (b - r >= COOL_LEAD) & ~sea            # the rim elsewhere: pale blue-white
    rim = warm | cool
    cloud = face & ~sea & ~rim & (luma >= CLOUD_LUMA) & (r >= b - 8)
    limb = face & by_space & ~rim
    if debug_dir:
        # the class map (scratch use only: sea blue, cloud white, limb cyan, rim warm orange, rim cool violet, space black, rest grey)
        m = np.full(a.shape, 90, np.uint8)
        m[space] = 0
        m[face] = (40, 40, 140)
        m[sea & face] = (0, 90, 255)
        m[cloud] = (255, 255, 255)
        m[limb & ~cloud] = (0, 220, 220)
        m[cool] = (160, 60, 220)
        m[warm] = (255, 140, 0)
        Image.fromarray(m).save(os.path.join(debug_dir, os.path.splitext(os.path.basename(path))[0] + "-classes.png"))

    def mean(m):
        n = int(m.sum())
        return (tuple(float(v) for v in a[m].mean(0)), n) if n else (None, 0)

    out = {"frame": os.path.basename(path), "size": [im.width, im.height], "facePx": int(face.sum())}
    out["sea"], out["seaPx"] = mean(sea & face)
    out["cloud"], out["cloudPx"] = mean(cloud)
    out["cover"] = out["cloudPx"] / float(out["facePx"]) if out["facePx"] else 0.0
    out["limb"], out["limbPx"] = mean(limb)
    out["rimWarm"], out["rimWarmPx"] = mean(warm)
    out["rimCool"], out["rimCoolPx"] = mean(cool)
    return out

def combine(frames):
    def wmean(key, nkey):
        tot = sum(f[nkey] for f in frames if f[key] is not None)
        if not tot:
            return None
        return tuple(round(sum(f[key][i] * f[nkey] for f in frames if f[key] is not None) / tot) for i in range(3))
    face = sum(f["facePx"] for f in frames)
    return {"sea": wmean("sea", "seaPx"), "cloud": wmean("cloud", "cloudPx"), "limb": wmean("limb", "limbPx"),
            "rimWarm": wmean("rimWarm", "rimWarmPx"), "rimCool": wmean("rimCool", "rimCoolPx"),
            "coverPermille": round(1000.0 * sum(f["cloudPx"] for f in frames) / face) if face else 0}

def _rgb(c):
    return "-" if c is None else "%d,%d,%d" % tuple(round(v) for v in c)

def main(argv):
    as_json = "--json" in argv
    debug_dir = None
    if "--debug" in argv:
        i = argv.index("--debug")
        debug_dir = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]
    paths = [p for p in argv if p != "--json"]
    if not paths:
        print(__doc__)
        return 2
    frames = [measure(p, debug_dir) for p in paths]
    summary = combine(frames)
    if as_json:
        print(json.dumps({"frames": frames, "summary": summary}, indent=1))
        return 0
    for f in frames:
        print("%-10s face %6d px  sea %-12s cloud %-12s cover %4.0f permille  limb %-12s rim warm %-12s (%d px) cool %-12s (%d px)" %
              (f["frame"], f["facePx"], _rgb(f["sea"]), _rgb(f["cloud"]), 1000.0 * f["cover"], _rgb(f["limb"]), _rgb(f["rimWarm"]), f["rimWarmPx"],
               _rgb(f["rimCool"]), f["rimCoolPx"]))
    print("summary    sea %s  cloud %s  cover %d permille  limb %s  rim warm %s  rim cool %s" %
          (_rgb(summary["sea"]), _rgb(summary["cloud"]), summary["coverPermille"], _rgb(summary["limb"]), _rgb(summary["rimWarm"]), _rgb(summary["rimCool"])))
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
