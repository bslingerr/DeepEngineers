"""S3 image oracles: ROADMAP S3 C5-C10, C13, C14 and N1 measured on Subnautica's in-game captures and their sidecars.

Every check works on what the capture shows, never on what the plugin says it drew: the regions come from projecting
the objects' known world geometry (the block from the SE side's pose and its SE-local box, the reference Seamoth from
its renderer bounds) through the capture's own Unity camera (sidecar), and the numbers come from the pixels.

PNG reader: Pillow when it is importable, else a stdlib zlib decoder (8-bit grey/RGB/RGBA/palette, all five row filters, no
interlace; slow on 2560 x 1440 but correct). `--selftest` cross-checks both decoders on the same files.

Conventions:
- camera: Unity's projection with the sidecar's `unityCamX/Y/Z`, `unityQx/y/z/w` (Unity quaternion), `unityFov`
 (vertical degrees) and `screenW/H` (fallback: the S1 keys `camX…`, `qx…`, `vFovDeg`, and the PNG size); pixels from
 the top-left corner, y down, as DE.SnPixel and SE's de.pixel report them (S1);
- luminance: relative luminance of the decoded linear sRGB values (Rec. 709 weights); colour differences: CIE76 ΔE in
 CIELAB (D65); "contrast" is Weber contrast |L_obj - L_bg| / L_bg of mean linear luminances;
- object regions: the convex hull of the projected box corners shrunk towards its centroid (interior, default 0.6),
 and a background ring between 1.25x and 1.8x of the hull with every object's hull removed;
- inputs: a capture is a PNG with the capture service's sidecar <png>.json (camera) and, for Subnautica's lab runs, the
 S3 sidecar <png>.s3.json (the sun light's forward: toSun = -forward; Seamoth bounds; block pose); a capture may show
 one target ("block", "seamoth", "glyph") or "both" (the lab plan frames block and Seamoth together); every check
 looks for the target's own capture first, then a "both" one.

Checks (thresholds from ROADMAP S3; numbers marked HYPOTHESIS are this tool's own, to revisit after the first real run):
 C5 P2: the buried half's screen region (inside the projected lower box, outside the upper one) shows something else
 than the block: median CIELAB distance to the visible half >= 12 (HYPOTHESIS)
 C6 contrast(P4)/contrast(P1) of the block within +-20 % of the same ratio for the Seamoth
 C7 luminance(noon)/luminance(midnight) of the block within +-25 % of the Seamoth's
 C8 P3 burst: temporal coefficient of variation of the top-face luminance > 30 % of the Seamoth's (the ratio of
 relative variations, so the albedo difference between grey armor and the white Seamoth cancels; HYPOTHESIS)
 C9 P3: the seabed patch just beyond the object on the anti-sun side is darker than a lit reference patch; the block's
 darkening >= 50 % of the Seamoth's (inconclusive when the Seamoth's own darkening is < 5 %)
 C10 two P3 captures 1 s apart differ (mean |difference| >= 0.05 levels or >= 0.1 % of pixels by > 2 levels) and the
 game used CPU in between (> 0 s, from the launcher's process-time samples)
 C13 painted face region at P1 within ΔE < 5 of SE's exported PNG at the same UV rectangle (S3b)
 C14 across plate seams the mean absolute luma gradient < 1.5x the in-block mean (S3b)
 N1 glyph: the four quadrant colours of the N1 map appear in the expected corners of the most camera-facing glyph face
 (classified by chromaticity, so lighting cannot flip the answer); bump: the bump map is
 8 x 8 cells, per colour quadrant an up/down checker (top two rows) and a right/left checker (bottom two); on a level
 side face the up-tilted cells are brighter than the down-tilted ones of the same colour (geometric mean of the four
 quadrant ratios >= 1.03, HYPOTHESIS; MISSING when too dark): light from above favours them at any sun azimuth, so
 G' = 1 - Y and the tangent w are settled; N1x (information) judges the right/left cells when the sun lies along
 the face. S3.M8 (an sRGB conversion target) is not visible here (the encode is monotonic): S3.N1d, in the plugin
 C6/C7 noise floor: a region's mean carries half an 8-bit level when the region is flat at one
 level, 1/sqrt(n) of it otherwise; the ratio of ratios with its propagated relative floor must lie wholly inside the
 band to PASS and wholly outside to FAIL, else MISSING (inconclusive); a contrast or luminance within two floors of 0
 is MISSING

Usage (python -I):
 python -I tests/s3/image_checks.py --selftest synthetic scenes + every seeded failure (stdlib only)
 python -I tests/s3/image_checks.py info CAPTURE.png size, decoder, sidecar camera
 python -I tests/s3/image_checks.py run MANIFEST.json the checks of a manifest written by analyze_s3.py
"""
from __future__ import annotations

import json
import math
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))

# thresholds (ROADMAP S3 oracle; HYPOTHESIS where noted in the docstring)
C5_MIN_DELTA_E = 12.0
C6_TOL = 0.20
C7_TOL = 0.25
C8_MIN_RATIO = 0.30
C9_MIN_RATIO = 0.50
C9_MIN_REF_DARKENING = 0.05
C10_MIN_MEAN_ABS = 0.05
C10_MIN_FRAC = 0.001
C13_MAX_DELTA_E = 5.0
C14_MAX_RATIO = 1.5
N1_BUMP_MIN_RATIO = 1.03
MIN_REGION_PX = 30
PLANNED_GLYPH = {"tl": (200, 40, 40), "tr": (40, 190, 40), "bl": (40, 60, 210), "br": (225, 225, 225)}
CORNER_SIGNS = tuple((sx, sy, sz) for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1))

# ----------------------------------------------------------------------------------------------------------------
# PNG (Pillow fast path, stdlib fallback) and a stdlib writer
# ----------------------------------------------------------------------------------------------------------------

class Img:
    """8-bit RGB pixels, rows top-down."""

    def __init__(self, w: int, h: int, data, decoder: str = ""):
        self.w, self.h = int(w), int(h)
        self.data = bytes(data)
        self.decoder = decoder
        if len(self.data) != self.w * self.h * 3:
            raise ValueError("RGB buffer %d bytes for %dx%d" % (len(self.data), self.w, self.h))

    def px(self, x: int, y: int):
        i = (y * self.w + x) * 3
        d = self.data
        return d[i], d[i + 1], d[i + 2]

def _pil():
    try:
        from PIL import Image as PILImage   # noqa: E402
        return PILImage
    except ImportError:
        return None

def decoder_name() -> str:
    p = _pil()
    if p is None:
        return "stdlib zlib"
    import PIL
    return "Pillow %s" % getattr(PIL, "__version__", "?")

def read_png(path: str, force_stdlib: bool = False) -> Img:
    if not force_stdlib:
        P = _pil()
        if P is not None:
            with P.open(path) as im:
                rgb = im.convert("RGB")
                return Img(rgb.width, rgb.height, rgb.tobytes(), decoder_name())
    with open(path, "rb") as f:
        return decode_png(f.read())

def decode_png(raw: bytes) -> Img:
    """Stdlib PNG decoder: 8-bit (16-bit reduced to the high byte) grey, grey+alpha, RGB, RGBA and palette images,
 filters 0-4, no interlace."""
    if raw[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG file")
    pos, idat, plte, ihdr = 8, [], None, None
    while pos + 8 <= len(raw):
        n, kind = struct.unpack_from(">I4s", raw, pos)
        body = raw[pos + 8:pos + 8 + n]
        pos += 12 + n
        if kind == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", body)
        elif kind == b"PLTE":
            plte = body
        elif kind == b"IDAT":
            idat.append(body)
        elif kind == b"IEND":
            break
    if ihdr is None:
        raise ValueError("PNG without IHDR")
    w, h, depth, ctype, _comp, _filt, interlace = ihdr
    if interlace:
        raise ValueError("interlaced PNG not supported by the stdlib decoder")
    if depth not in (8, 16):
        raise ValueError("bit depth %d not supported" % depth)
    chans = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    bpp = chans * depth // 8
    stride = w * bpp
    data = zlib.decompress(b"".join(idat))
    out = bytearray(w * h * 3)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        ft = data[p]
        row = bytearray(data[p + 1:p + 1 + stride])
        p += 1 + stride
        if ft == 1:
            for i in range(bpp, stride):
                row[i] = (row[i] + row[i - bpp]) & 255
        elif ft == 2:
            row = bytearray((a + b) & 255 for a, b in zip(row, prev))
        elif ft == 3:
            for i in range(stride):
                left = row[i - bpp] if i >= bpp else 0
                row[i] = (row[i] + ((left + prev[i]) >> 1)) & 255
        elif ft == 4:
            for i in range(stride):
                a = row[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                row[i] = (row[i] + pr) & 255
        elif ft != 0:
            raise ValueError("bad PNG filter %d" % ft)
        prev = row
        o = y * w * 3
        step = depth // 8
        if ctype == 2 and depth == 8:
            out[o:o + w * 3] = row
            continue
        for x in range(w):
            q = x * bpp
            if ctype == 3:
                k = row[q] * 3
                out[o + 3 * x:o + 3 * x + 3] = plte[k:k + 3]
            elif ctype in (0, 4):
                g = row[q]
                out[o + 3 * x] = out[o + 3 * x + 1] = out[o + 3 * x + 2] = g
            else:
                out[o + 3 * x] = row[q]
                out[o + 3 * x + 1] = row[q + step]
                out[o + 3 * x + 2] = row[q + 2 * step]
    return Img(w, h, bytes(out), "stdlib zlib")

def write_png(path: str, img: Img, filter_type: int = 0):
    """Stdlib PNG writer (8-bit RGB). filter_type 0 (None) or 1 (Sub) for the self-test of both decoders."""
    rows = bytearray()
    for y in range(img.h):
        line = img.data[y * img.w * 3:(y + 1) * img.w * 3]
        if filter_type == 1:
            f = bytearray(line)
            for i in range(len(f) - 1, 2, -1):
                f[i] = (line[i] - line[i - 3]) & 255
            rows += b"\x01" + f
        else:
            rows += b"\x00" + line

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", img.w, img.h, 8, 2, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(bytes(rows), 6)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(data)

# ----------------------------------------------------------------------------------------------------------------
# Colour
# ----------------------------------------------------------------------------------------------------------------

_LIN = [((c / 255.0) / 12.92) if c / 255.0 <= 0.04045 else (((c / 255.0) + 0.055) / 1.055) ** 2.4 for c in range(256)]

def lum(rgb) -> float:
    return 0.2126 * _LIN[rgb[0]] + 0.7152 * _LIN[rgb[1]] + 0.0722 * _LIN[rgb[2]]

def luma8(rgb) -> float:
    """Non-linear 8-bit luma (gradients of C14 are judged on the encoded picture)."""
    return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]

def lab_of_linear(r, g, b):
    x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047
    y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b
    z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883

    def f(t):
        return t ** (1.0 / 3.0) if t > 216.0 / 24389.0 else (24389.0 / 27.0 * t + 16.0) / 116.0
    fx, fy, fz = f(x), f(y), f(z)
    return 116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz)

def lab(rgb8):
    return lab_of_linear(_LIN[int(rgb8[0])], _LIN[int(rgb8[1])], _LIN[int(rgb8[2])])

def delta_e(a, b) -> float:
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))

def chroma(rgb):
    s = float(rgb[0] + rgb[1] + rgb[2]) or 1.0
    return rgb[0] / s, rgb[1] / s, rgb[2] / s

# ----------------------------------------------------------------------------------------------------------------
# Vector maths and the Unity camera
# ----------------------------------------------------------------------------------------------------------------

def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def mul(a, k): return (a[0] * k, a[1] * k, a[2] * k)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a): return math.sqrt(dot(a, a))

def unit(a):
    n = norm(a)
    return (a[0] / n, a[1] / n, a[2] / n) if n > 0 else (0.0, 0.0, 0.0)

def qrot(q, v):
    """Unity `q * v`."""
    u = (q[0], q[1], q[2])
    t = mul(cross(u, v), 2.0)
    return add(add(v, mul(t, q[3])), cross(u, t))

def qconj(q):
    return (-q[0], -q[1], -q[2], q[3])

def look_rotation(f, u):
    """Unity Quaternion.LookRotation(forward, up) (tools/worldmap/wire_ref.look_rotation, inlined)."""
    Z = unit(f)
    X = unit(cross(u, Z))
    Y = cross(Z, X)
    m00, m01, m02, m10, m11, m12, m20, m21, m22 = X[0], Y[0], Z[0], X[1], Y[1], Z[1], X[2], Y[2], Z[2]
    tr = m00 + m11 + m22
    if tr > 0:
        S = math.sqrt(tr + 1.0) * 2
        q = ((m21 - m12) / S, (m02 - m20) / S, (m10 - m01) / S, 0.25 * S)
    elif m00 > m11 and m00 > m22:
        S = math.sqrt(1.0 + m00 - m11 - m22) * 2
        q = (0.25 * S, (m01 + m10) / S, (m02 + m20) / S, (m21 - m12) / S)
    elif m11 > m22:
        S = math.sqrt(1.0 + m11 - m00 - m22) * 2
        q = ((m01 + m10) / S, 0.25 * S, (m12 + m21) / S, (m02 - m20) / S)
    else:
        S = math.sqrt(1.0 + m22 - m00 - m11) * 2
        q = ((m02 + m20) / S, (m12 + m21) / S, 0.25 * S, (m10 - m01) / S)
    return q if q[3] >= 0 else tuple(-c for c in q)

def sn_apply(o, q, s, l_se):
    """SE-local point -> SN world (CONTRACT 2.3 Apply: o + S (q Mirror(l)))."""
    v = qrot(q, (l_se[0], l_se[1], -l_se[2]))
    return (o[0] + s * v[0], o[1] + v[1], o[2] + s * v[2])

def sn_dir(q, s, d_se):
    v = qrot(q, (d_se[0], d_se[1], -d_se[2]))
    return unit((s * v[0], v[1], s * v[2]))

class Camera:
    """A Unity camera: position, rotation (x, y, z, w), vertical FOV in degrees, screen size."""

    def __init__(self, pos, rot, fov, w, h, near=0.05):
        self.pos, self.rot, self.fov, self.w, self.h, self.near = tuple(pos), tuple(rot), float(fov), int(w), int(h), near
        self.t = math.tan(math.radians(self.fov) / 2.0)
        self.aspect = self.w / float(self.h)

    @classmethod
    def from_sidecar(cls, d: dict, img: Img = None):
        def pick(*keys):
            for k in keys:
                if isinstance(d.get(k), (int, float)) and not isinstance(d.get(k), bool):
                    return float(d[k])
            return None
        pos = [pick("unityCamX", "camX"), pick("unityCamY", "camY"), pick("unityCamZ", "camZ")]
        rot = [pick("unityQx", "qx"), pick("unityQy", "qy"), pick("unityQz", "qz"), pick("unityQw", "qw")]
        fov = pick("unityFov", "vFovDeg")
        w, h = pick("screenW"), pick("screenH")
        if img is not None and (not w or not h or abs(w / h - img.w / float(img.h)) > 1e-3):
            w, h = img.w, img.h
        if img is not None and w and int(w) != img.w:
            w, h = img.w, img.h           # a capture at another resolution than the screen (super size): use the PNG
        if None in pos or None in rot or fov is None or not w or not h:
            return None
        return cls(pos, rot, fov, w, h)

    def project(self, p):
        """(x, y, depth) of a world point, pixels from the top-left corner, y down; None behind the near plane."""
        c = qrot(qconj(self.rot), sub(p, self.pos))
        if c[2] <= self.near:
            return None
        return ((c[0] / (c[2] * self.t * self.aspect) + 1.0) * 0.5 * self.w,
                (1.0 - c[1] / (c[2] * self.t)) * 0.5 * self.h, c[2])

    def ray(self, px, py):
        nx = (px + 0.5) / self.w * 2.0 - 1.0
        ny = 1.0 - (py + 0.5) / self.h * 2.0
        return unit(qrot(self.rot, (nx * self.t * self.aspect, ny * self.t, 1.0)))

# ----------------------------------------------------------------------------------------------------------------
# Regions
# ----------------------------------------------------------------------------------------------------------------

def hull(points):
    """Convex hull (monotone chain), counter-clockwise in screen coordinates (y down)."""
    pts = sorted(set((round(p[0], 6), round(p[1], 6)) for p in points))
    if len(pts) <= 2:
        return pts

    def turn(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and turn(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and turn(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]

def centroid(poly):
    return (sum(p[0] for p in poly) / len(poly), sum(p[1] for p in poly) / len(poly))

def scale_poly(poly, k, about=None):
    c = about or centroid(poly)
    return [(c[0] + (p[0] - c[0]) * k, c[1] + (p[1] - c[1]) * k) for p in poly]

def inside(poly, x, y) -> bool:
    if len(poly) < 3:
        return False
    sign = 0
    n = len(poly)
    for i in range(n):
        ax, ay = poly[i]
        bx, by = poly[(i + 1) % n]
        c = (bx - ax) * (y - ay) - (by - ay) * (x - ax)
        if c != 0:
            s = 1 if c > 0 else -1
            if sign == 0:
                sign = s
            elif s != sign:
                return False
    return True

def area(poly) -> float:
    n = len(poly)
    return abs(sum(poly[i][0] * poly[(i + 1) % n][1] - poly[(i + 1) % n][0] * poly[i][1] for i in range(n))) / 2.0

def projected_hull(cam: Camera, pts):
    pr = [cam.project(p) for p in pts]
    if any(p is None for p in pr):
        return None
    return hull([(p[0], p[1]) for p in pr])

def region_pixels(img: Img, poly, exclude=(), max_px: int = 40000):
    """Pixel centres inside `poly` and outside every polygon of `exclude`, clipped to the image, subsampled to about
 max_px pixels."""
    if not poly or len(poly) < 3:
        return []
    x0 = max(0, int(math.floor(min(p[0] for p in poly))))
    x1 = min(img.w - 1, int(math.ceil(max(p[0] for p in poly))))
    y0 = max(0, int(math.floor(min(p[1] for p in poly))))
    y1 = min(img.h - 1, int(math.ceil(max(p[1] for p in poly))))
    if x1 < x0 or y1 < y0:
        return []
    est = max(1.0, area(poly))
    step = max(1, int(math.sqrt(est / float(max_px))))
    out = []
    for y in range(y0, y1 + 1, step):
        for x in range(x0, x1 + 1, step):
            cx, cy = x + 0.5, y + 0.5
            if inside(poly, cx, cy) and not any(e and inside(e, cx, cy) for e in exclude):
                out.append((x, y))
    return out

def stats(img: Img, pix) -> dict:
    if not pix:
        return {"n": 0}
    ls = [lum(img.px(x, y)) for x, y in pix]
    rs = sorted(img.px(x, y) for x, y in pix)
    cols = [img.px(x, y) for x, y in pix]
    med = tuple(sorted(c[k] for c in cols)[len(cols) // 2] for k in range(3))
    mean_lin = tuple(sum(_LIN[c[k]] for c in cols) / len(cols) for k in range(3))
    mean_l = sum(ls) / len(ls)
    l8 = [luma8(c) for c in cols]
    m8 = sum(l8) / len(l8)
    sd8 = math.sqrt(sum((x - m8) ** 2 for x in l8) / max(1, len(l8) - 1))
    # the mean's 8-bit floor: a region spread over >= 1 level averages its rounding away
 # (about 1/sqrt(n) of half a level); a flat region (one level) can be off by the whole half level
    floor = lum_half_level(mean_l) * (1.0 if sd8 < 1.0 else 1.0 / math.sqrt(len(ls)))
    return {"n": len(pix), "meanLum": mean_l, "medianRgb": med, "meanLab": lab_of_linear(*mean_lin),
            "medianLab": lab(med), "_first": rs[0], "lumaStd": sd8, "meanLumFloor": floor}

def box_corners(centre, axes, half):
    """8 world corners of a box: centre + sum(sign_i * half_i * axis_i), corner i = bit0 x, bit1 y, bit2 z."""
    return [add(add(add(centre, mul(axes[0], sg[0] * half[0])), mul(axes[1], sg[1] * half[1])),
                mul(axes[2], sg[2] * half[2])) for sg in CORNER_SIGNS]

def aabb_corners(lo, hi):
    c = tuple((lo[k] + hi[k]) / 2.0 for k in range(3))
    h = tuple((hi[k] - lo[k]) / 2.0 for k in range(3))
    return box_corners(c, ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)), h)

# ----------------------------------------------------------------------------------------------------------------
# Scene objects (dicts): {"name", "corners": 8 SN points,...}; the block also carries its pose for faces
# ----------------------------------------------------------------------------------------------------------------

def block_object(pose: dict, half=1.25, offset=(0.0, 0.0, 0.0), name="block") -> dict:
    """pose = {"o", "q", "s"} (the SE side's wire pose of the grid); the box of one part at grid-local `offset`."""
    o, q, s = pose["o"], pose["q"], pose["s"]
    corners = [sn_apply(o, q, s, (offset[0] + sg[0] * half, offset[1] + sg[1] * half, offset[2] + sg[2] * half))
               for sg in CORNER_SIGNS]
    return {"name": name, "corners": corners, "pose": {"o": tuple(o), "q": tuple(q), "s": float(s)}, "half": half,
            "offset": tuple(offset), "centre": sn_apply(o, q, s, offset)}

def aabb_object(lo, hi, name="seamoth") -> dict:
    c = tuple((lo[k] + hi[k]) / 2.0 for k in range(3))
    return {"name": name, "corners": aabb_corners(lo, hi), "centre": c, "lo": tuple(lo), "hi": tuple(hi)}

def object_regions(img: Img, cam: Camera, obj: dict, others=(), shrink=0.6, ring=(1.25, 1.8), above=False):
    """(interior pixels, background-ring pixels, full hull) of an object in a capture. above=True keeps only the ring
 pixels above the object's top in the picture (for an object on the seabed seen level: the far water, i.e. the
 fog colour)."""
    full = projected_hull(cam, obj["corners"])
    if not full or len(full) < 3:
        return [], [], None
    other_hulls = [projected_hull(cam, o["corners"]) for o in others]
    other_hulls = [scale_poly(h, 1.15) for h in other_hulls if h]
    interior = region_pixels(img, scale_poly(full, shrink), exclude=other_hulls)
    if above:
        top = min(p[1] for p in full)
        hgt = max(p[1] for p in full) - top
        wid = max(p[0] for p in full) - min(p[0] for p in full)
        c = centroid(full)
        y1 = top - 0.1 * hgt
        y0 = y1 - max(0.6 * hgt, 4.0)
        band = [(c[0] - 0.6 * wid, y0), (c[0] + 0.6 * wid, y0), (c[0] + 0.6 * wid, y1), (c[0] - 0.6 * wid, y1)]
        ringp = region_pixels(img, band, exclude=other_hulls)
    else:
        ringp = region_pixels(img, scale_poly(full, ring[1]), exclude=[scale_poly(full, ring[0])] + other_hulls)
    return interior, ringp, full

def clip_above(obj: dict, y: float) -> dict:
    """The part of a level box above SN height y (the visible part of a half-buried object), as a new object; the
 object itself when y is below it, None when it is entirely below."""
    if obj is None or y is None:
        return obj
    cs = obj["corners"]
    lo, hi = min(p[1] for p in cs), max(p[1] for p in cs)
    if y <= lo:
        return obj
    if y >= hi:
        return None
    srt = sorted(cs, key=lambda p: p[1])
    bottom4, top4 = srt[:4], srt[4:]
    out = []
    for b in bottom4:
        t = min(top4, key=lambda p: (p[0] - b[0]) ** 2 + (p[2] - b[2]) ** 2)
        f = (y - b[1]) / (t[1] - b[1]) if t[1] != b[1] else 0.0
        out.append(add(b, mul(sub(t, b), f)))
    out += top4
    c = tuple(sum(p[k] for p in out) / 8.0 for k in range(3))
    return dict(obj, corners=out, centre=c, clippedAt=y)

def top_face(obj: dict):
    """The 4 corners with the highest SN y (the face seen from above)."""
    cs = sorted(obj["corners"], key=lambda p: p[1], reverse=True)
    return cs[:4]

def row_result(cid, status, measured, threshold, what, **detail):
    r = {"id": cid, "status": status, "measured": measured, "threshold": threshold, "what": what}
    if "required" in detail:
        r["required"] = bool(detail.pop("required"))
    if detail:
        r["detail"] = detail
    return r

def _fmt(v, f="%.4g"):
    return "-" if v is None else (f % v)

# ----------------------------------------------------------------------------------------------------------------
# The checks
# ----------------------------------------------------------------------------------------------------------------

def c5_occlusion(img: Img, cam: Camera, block: dict, seabed_y: float) -> dict:
    """S3.C5 (P2): the buried part of the block is hidden by the terrain."""
    cs = block["corners"]
    lo_y, hi_y = min(p[1] for p in cs), max(p[1] for p in cs)
    if not (lo_y < seabed_y < hi_y):
        return row_result("S3.C5", "MISSING", "seabed %.2f outside the block [%.2f, %.2f]" % (seabed_y, lo_y, hi_y),
                          ">= %.0f dE" % C5_MIN_DELTA_E, "P2 buried half hidden (image region)")
    # the block is level in the lab scenes (Up = the local vertical): interpolate each vertical edge (bottom corner ->
 # the top corner above it) at the seabed height
    bottom4 = sorted(cs, key=lambda p: p[1])[:4]
    top4 = sorted(cs, key=lambda p: p[1])[4:]
    pairs = []
    for b in bottom4:
        t = min(top4, key=lambda p: (p[0] - b[0]) ** 2 + (p[2] - b[2]) ** 2)
        pairs.append((b, t))

    def at(y):
        out = []
        for b, t in pairs:
            f = (y - b[1]) / (t[1] - b[1]) if t[1] != b[1] else 0.0
            out.append(add(b, mul(sub(t, b), f)))
        return out
    buried = [b for b, _t in pairs] + at(seabed_y)
    visible = at(seabed_y) + [t for _b, t in pairs]
    hb, hv = projected_hull(cam, buried), projected_hull(cam, visible)
    if not hb or not hv:
        return row_result("S3.C5", "MISSING", "block behind the camera", ">= %.0f dE" % C5_MIN_DELTA_E,
                          "P2 buried half hidden (image region)")
    vis = region_pixels(img, scale_poly(hv, 0.7))
    bur = region_pixels(img, scale_poly(hb, 0.85), exclude=[scale_poly(hv, 1.12)])
    if len(vis) < MIN_REGION_PX or len(bur) < MIN_REGION_PX:
        return row_result("S3.C5", "MISSING", "regions %d / %d px" % (len(vis), len(bur)), ">= %.0f dE" % C5_MIN_DELTA_E,
                          "P2 buried half hidden (image region)", visiblePx=len(vis), buriedPx=len(bur))
    sv, sb = stats(img, vis), stats(img, bur)
    de = delta_e(sv["medianLab"], sb["medianLab"])
    return row_result("S3.C5", "PASS" if de >= C5_MIN_DELTA_E else "FAIL", "dE %.1f (%d / %d px)" % (de, len(vis), len(bur)),
                      ">= %.0f dE" % C5_MIN_DELTA_E, "P2: buried-half region differs from the visible half (terrain in front)",
                      deltaE=de, visibleMedianRgb=sv["medianRgb"], buriedMedianRgb=sb["medianRgb"])

def lum_half_level(L: float) -> float:
    """The 8-bit noise floor of a mean linear luminance L: the change of L for half an
 8-bit level of its sRGB encoding (a region that sits on one level can be biased by that much; dark regions have
 large relative floors)."""
    L = max(0.0, min(1.0, L))
    v = _enc_unit(L)
    up = _dec_unit(min(1.0, v + 0.5 / 255.0)) - L
    dn = L - _dec_unit(max(0.0, v - 0.5 / 255.0))
    return max(up, dn, 0.5 / 255.0 / 12.92)

def _dec_unit(v: float) -> float:
    v = max(0.0, min(1.0, v))
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4

def ratio_verdict(r: float, rel_err: float, tol: float):
    """A ratio of ratios with its relative uncertainty against 1 +- tol: PASS when the whole interval is inside,
 FAIL when it is wholly outside, MISSING (inconclusive, the noise floor) otherwise."""
    lo, hi = r * (1.0 - rel_err), r * (1.0 + rel_err)
    if 1.0 - tol <= lo and hi <= 1.0 + tol:
        return "PASS"
    if lo > 1.0 + tol or hi < 1.0 - tol:
        return "FAIL"
    return "MISSING"

def contrast(img: Img, cam: Camera, obj: dict, others=(), above=True):
    """Weber contrast of the object against the water above it (the fog colour: with L(d) = L0 f + Linf (1 - f) the
 contrast is |L0 - Linf| f / Linf, so contrast(P4)/contrast(P1) = f(d4)/f(d1) whatever the albedo)."""
    inter, ringp, full = object_regions(img, cam, obj, others, above=above)
    if len(inter) < MIN_REGION_PX or len(ringp) < MIN_REGION_PX:
        return None, {"interiorPx": len(inter), "ringPx": len(ringp)}
    so, sb = stats(img, inter), stats(img, ringp)
    lb = max(sb["meanLum"], 1e-6)
    c = abs(so["meanLum"] - sb["meanLum"]) / lb
    # the 8-bit floor of the contrast (finding 9): the floors of both means
    dlo, dlb = so["meanLumFloor"], sb["meanLumFloor"]
    dc = (dlo + dlb) / lb + c * dlb / lb
    return c, {"obj": so["meanLum"], "bg": sb["meanLum"], "interiorPx": len(inter), "ringPx": len(ringp), "floor": dc}

def facing_side_face(cam, obj: dict, seabed_y=None):
    """The vertical face of a level box (corners in CORNER_SIGNS order) most facing the camera - or, given several
 cameras, the face best seen by all of them (the largest smallest cosine) - clipped above the seabed: an object
 {"corners": 4 points}. The same face at P1 and P4 keeps the albedo and the lighting of the region constant (a whole
 hull mixes faces in proportions that change with the distance, and a lab plan sees the block obliquely at P1)."""
    cams = cam if isinstance(cam, (list, tuple)) else [cam]
    cs = obj["corners"]
    ctr = tuple(sum(p[k] for p in cs) / 8.0 for k in range(3))
    best, best_d = None, 0.0
    for k in range(3):
        for sgn in (-1, 1):
            pts = [cs[i] for i, sg in enumerate(CORNER_SIGNS) if sg[k] == sgn]
            fc = tuple(sum(p[j] for p in pts) / 4.0 for j in range(3))
            n = unit(sub(fc, ctr))
            if abs(n[1]) > 0.7:
                continue
            d = min(dot(n, unit(sub(c.pos, fc))) for c in cams)
            if d > best_d:
                best, best_d = pts, d
    if best is None:
        return None
    if seabed_y is not None:
        srt = sorted(best, key=lambda p: p[1])
        low, high = srt[:2], srt[2:]
        out = list(high)
        for b in low:
            t = min(high, key=lambda p: (p[0] - b[0]) ** 2 + (p[2] - b[2]) ** 2)
            if b[1] < seabed_y < t[1]:
                b = add(b, mul(sub(t, b), (seabed_y - b[1]) / (t[1] - b[1])))
            elif t[1] <= seabed_y:
                return None
            out.append(b)
        best = out
    return {"name": obj.get("name", "") + ".face", "corners": best}

def c6_fog(shots: dict, block: dict, seamoth: dict, seabed: dict = None) -> dict:
    """shots[(pose, target)] = (img, cam) for pose P1/P4 and target block/seamoth; seabed[target] = the seabed height
 under it (the regions are the camera-facing vertical face above it)."""
    seabed = seabed or {}
    vals, det = {}, {}
    for pose in ("P1", "P4"):
        for name, obj, other in (("block", block, seamoth), ("seamoth", seamoth, block)):
            sh = shots.get((pose, name))
            if sh is None or obj is None:
                det["%s.%s" % (pose, name)] = "no capture"
                continue
            cams = [v[1] for (p_, n_), v in shots.items() if n_ == name and v is not None]
            face = facing_side_face(cams or sh[1], obj, seabed.get(name))
            if face is None:
                det["%s.%s" % (pose, name)] = "no face towards the camera"
                continue
            c, d = contrast(sh[0], sh[1], face, [other] if other else ())
            vals[(pose, name)] = c
            det["%s.%s" % (pose, name)] = d
    try:
        rb = vals[("P4", "block")] / vals[("P1", "block")]
        rs = vals[("P4", "seamoth")] / vals[("P1", "seamoth")]
    except (KeyError, TypeError, ZeroDivisionError):
        return row_result("S3.C6", "MISSING", "contrast %s" % {"%s.%s" % k: v for k, v in vals.items()},
                          "+-%d %%" % (C6_TOL * 100), "fog: contrast(P4)/contrast(P1) block vs Seamoth", **det)
    # noise floor: a contrast within 2 floors of 0 cannot be judged, and the ratio of
 # ratios carries the relative floors of its four contrasts
    floors = {k: (det.get("%s.%s" % k) or {}).get("floor", 0.0) for k in vals}
    weak = ["%s.%s" % k for k, v in vals.items() if v <= 2.0 * floors[k]]
    if weak or not rs:
        return row_result("S3.C6", "MISSING", "contrast at the 8-bit floor: %s" % ",".join(weak or ["seamoth"]),
                          "+-%d %%" % (C6_TOL * 100), "fog: contrast(P4)/contrast(P1) block vs Seamoth",
                          contrasts={"%s.%s" % k: v for k, v in vals.items()}, **det)
    r = rb / rs
    rel_err = sum(floors[k] / vals[k] for k in vals)
    status = ratio_verdict(r, rel_err, C6_TOL)
    return row_result("S3.C6", status, "block %.3f / Seamoth %.3f (%+.0f %% +- %.0f %%)" % (
        rb, rs, 100 * (r - 1.0), 100 * rel_err), "+-%d %%" % (C6_TOL * 100),
        "fog: contrast(P4)/contrast(P1) block vs Seamoth (MISSING when the 8-bit floor straddles the band)",
        blockRatio=rb, seamothRatio=rs, relErr=rel_err, contrasts={"%s.%s" % k: v for k, v in vals.items()}, **det)

def object_luminance(img, cam, obj, others=()):
    inter, _r, _f = object_regions(img, cam, obj, others)
    if len(inter) < MIN_REGION_PX:
        return None, len(inter)
    return stats(img, inter)["meanLum"], len(inter)

def object_luminance_floor(img, cam, obj, others=()):
    """(mean linear luminance, its 8-bit floor, pixels) of the object's interior region; (None, None, n) when too small."""
    inter, _r, _f = object_regions(img, cam, obj, others)
    if len(inter) < MIN_REGION_PX:
        return None, None, len(inter)
    st = stats(img, inter)
    return st["meanLum"], st["meanLumFloor"], len(inter)

def c7_sun(shots: dict, block: dict, seamoth: dict, seabed: dict = None) -> dict:
    """shots[(time, target)], time in {"noon", "midnight"}."""
    seabed = seabed or {}
    block, seamoth = clip_above(block, seabed.get("block")), clip_above(seamoth, seabed.get("seamoth"))
    L, det, fl = {}, {}, {}
    for t in ("noon", "midnight"):
        for name, obj, other in (("block", block, seamoth), ("seamoth", seamoth, block)):
            sh = shots.get((t, name))
            if sh is None or obj is None:
                continue
            L[(t, name)], fl[(t, name)], det["%s.%s.px" % (t, name)] = object_luminance_floor(sh[0], sh[1], obj,
                                                                                               [other] if other else ())
    if any(L.get((t, n)) is None for t in ("noon", "midnight") for n in ("block", "seamoth")):
        return row_result("S3.C7", "MISSING", "luminance %s" % {"%s.%s" % k: v for k, v in L.items()},
                          "+-%d %%" % (C7_TOL * 100), "sun: noon/midnight luminance ratio block vs Seamoth", **det)
    # noise floor: a midnight mean within 2 half-levels of black cannot carry a ratio
    floors = {k: fl.get(k) or lum_half_level(v) for k, v in L.items()}
    weak = ["%s.%s" % k for k, v in L.items() if v <= 2.0 * floors[k]]
    if weak:
        return row_result("S3.C7", "MISSING", "luminance at the 8-bit floor: %s" % ",".join(weak),
                          "+-%d %%" % (C7_TOL * 100), "sun: noon/midnight luminance ratio block vs Seamoth",
                          luminance={"%s.%s" % k: v for k, v in L.items()}, **det)
    rb = L[("noon", "block")] / L[("midnight", "block")]
    rs = L[("noon", "seamoth")] / L[("midnight", "seamoth")]
    r = rb / rs
    rel_err = sum(floors[k] / L[k] for k in L)
    status = ratio_verdict(r, rel_err, C7_TOL)
    return row_result("S3.C7", status, "block %.2f / Seamoth %.2f (%+.0f %% +- %.0f %%)" % (
        rb, rs, 100 * (r - 1.0), 100 * rel_err), "+-%d %%" % (C7_TOL * 100),
        "sun: noon/midnight luminance ratio block vs Seamoth (MISSING when the 8-bit floor straddles the band)",
        blockRatio=rb, seamothRatio=rs, relErr=rel_err, luminance={"%s.%s" % k: v for k, v in L.items()}, **det)

def top_face_series(burst, obj, others=()):
    out = []
    for img, cam in burst:
        poly = projected_hull(cam, top_face(obj))
        if not poly:
            continue
        ex = [scale_poly(h, 1.1) for h in (projected_hull(cam, o["corners"]) for o in others) if h]
        pix = region_pixels(img, scale_poly(poly, 0.7), exclude=ex)
        if len(pix) >= MIN_REGION_PX:
            out.append(stats(img, pix)["meanLum"])
    return out

def _cv(xs):
    if len(xs) < 3:
        return None
    m = sum(xs) / len(xs)
    sd = math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))
    return sd / m if m > 0 else None

def c8_caustics(bursts: dict, block: dict, seamoth: dict, depth_m: float = None) -> dict:
    """bursts["block"|"seamoth"] = [(img, cam),...] (P3, top-down; the same list may serve both)."""
    what = "caustics: temporal variation of the top-face luminance, block vs Seamoth (P3, depth < 20 m)"
    if depth_m is not None and depth_m >= 20.0:
        return row_result("S3.C8", "MISSING", "depth %.1f m (needs < 20 m: the shallow spot)" % depth_m,
                          "> %d %% of Seamoth" % (C8_MIN_RATIO * 100), what)
    sb = top_face_series(bursts.get("block") or [], block, [seamoth] if seamoth else ())
    ss = top_face_series(bursts.get("seamoth") or [], seamoth, [block]) if seamoth else []
    cb, cs = _cv(sb), _cv(ss)
    if cb is None or cs is None or cs <= 0:
        return row_result("S3.C8", "MISSING", "frames %d / %d" % (len(sb), len(ss)), "> %d %% of Seamoth" % (
            C8_MIN_RATIO * 100), what, blockSeries=len(sb), seamothSeries=len(ss))
    ratio = cb / cs
    return row_result("S3.C8", "PASS" if ratio > C8_MIN_RATIO else "FAIL", "CV %.4f / %.4f = %.0f %% (%d frames)" % (
        cb, cs, 100 * ratio, len(sb)), "> %d %% of Seamoth" % (C8_MIN_RATIO * 100), what, blockCv=cb, seamothCv=cs,
        ratio=ratio, frames=len(sb))

def _patch(cam, img, centre, half, exclude):
    pts = [add(centre, (dx, 0.0, dz)) for dx in (-half, half) for dz in (-half, half)]
    poly = projected_hull(cam, pts)
    if not poly:
        return None
    pix = region_pixels(img, poly, exclude=exclude)
    if len(pix) < MIN_REGION_PX // 2:
        return None
    return stats(img, pix)["meanLum"]

def shadow_darkening(img: Img, cam: Camera, obj: dict, others, sun_dir, seabed_y: float):
    """(darkening, detail) of the seabed patch beyond the object on the anti-sun side vs a lit patch to the side."""
    s = unit(sun_dir)
    hz = unit((s[0], 0.0, s[2]))
    elev = math.asin(max(-1.0, min(1.0, s[1])))
    if norm(hz) < 1e-6 or elev <= math.radians(5):
        return None, {"why": "sun too high (no shadow beside the object) or below 5 deg", "elevDeg": math.degrees(elev)}
    cs = obj["corners"]
    c = obj.get("centre") or tuple(sum(p[k] for p in cs) / 8.0 for k in range(3))
    reach = max(dot(sub(p, c), mul(hz, -1.0)) for p in cs)            # footprint half-size along the anti-sun direction
    height = max(p[1] for p in cs) - seabed_y
    shadow_len = height / math.tan(elev) if height > 0 else 0.0
    if shadow_len < 0.3:
        return None, {"why": "shadow shorter than 0.3 m", "shadowLenM": shadow_len}
    d = reach + min(0.6, 0.45 * shadow_len)
    half = max(0.12, min(0.35, 0.25 * shadow_len))
    base = (c[0], seabed_y, c[2])
    p_sh = add(base, mul(hz, -d))
    side = unit(cross((0.0, 1.0, 0.0), hz))
    lateral = max(abs(dot(sub(p, c), side)) for p in cs)
    p_ref = add(base, mul(side, lateral + 2.0))
    ex = [scale_poly(h, 1.1) for h in (projected_hull(cam, o["corners"]) for o in [obj] + list(others)) if h]
    ls, lr = _patch(cam, img, p_sh, half, ex), _patch(cam, img, p_ref, half, ex)
    if ls is None or lr is None or lr <= 0:
        return None, {"why": "patch outside the picture", "shadowPatch": p_sh, "refPatch": p_ref}
    return 1.0 - ls / lr, {"shadowLum": ls, "refLum": lr, "shadowLenM": shadow_len, "elevDeg": math.degrees(elev)}

def c9_shadow(shots: dict, block: dict, seamoth: dict, sun_dir, seabed: dict) -> dict:
    """shots["block"|"seamoth"] = (img, cam) of P3; seabed["block"|"seamoth"] = seabed y under each object."""
    what = "shadow: anti-sun seabed patch darkening, block vs Seamoth (P3)"
    if sun_dir is None:
        return row_result("S3.C9", "MISSING", "no sun direction in the sidecar", ">= %d %% of Seamoth" % (
            C9_MIN_RATIO * 100), what)
    res = {}
    for name, obj, other in (("block", block, seamoth), ("seamoth", seamoth, block)):
        sh = shots.get(name)
        if sh is None or obj is None:
            continue
        res[name] = shadow_darkening(sh[0], sh[1], obj, [other] if other else [], sun_dir, seabed.get(name, 0.0))
    db, ds = (res.get("block") or (None, {}))[0], (res.get("seamoth") or (None, {}))[0]
    det = {k: v[1] for k, v in res.items()}
    if db is None or ds is None:
        return row_result("S3.C9", "MISSING", "darkening %s / %s" % (_fmt(db), _fmt(ds)), ">= %d %% of Seamoth" % (
            C9_MIN_RATIO * 100), what, **det)
    if ds < C9_MIN_REF_DARKENING:
        return row_result("S3.C9", "MISSING", "Seamoth darkening %.3f < %.2f (inconclusive)" % (ds, C9_MIN_REF_DARKENING),
                          ">= %d %% of Seamoth" % (C9_MIN_RATIO * 100), what, **det)
    ratio = db / ds
    return row_result("S3.C9", "PASS" if ratio >= C9_MIN_RATIO else "FAIL", "%.3f / %.3f = %.0f %%" % (db, ds, 100 * ratio),
                      ">= %d %% of Seamoth" % (C9_MIN_RATIO * 100), what, blockDarkening=db, seamothDarkening=ds, **det)

def c10_alive(img_a: Img, img_b: Img, cpu_seconds=None, sha=None) -> dict:
    what = "oracle alive: two P3 captures 1 s apart differ and the game used CPU in between"
    if img_a is None or img_b is None:
        return row_result("S3.C10", "MISSING", "captures missing", "differ, CPU > 0", what)
    if (img_a.w, img_a.h) != (img_b.w, img_b.h):
        return row_result("S3.C10", "FAIL", "sizes differ", "differ, CPU > 0", what)
    a, b = img_a.data, img_b.data
    n = len(a)
    step = max(1, n // 3 // 400000) * 3
    tot, big, cnt = 0, 0, 0
    for i in range(0, n - 2, step):
        d = abs(a[i] - b[i]) + abs(a[i + 1] - b[i + 1]) + abs(a[i + 2] - b[i + 2])
        tot += d
        big += 1 if d > 6 else 0
        cnt += 1
    mean_abs, frac = tot / (3.0 * cnt), big / float(cnt)
    differ = mean_abs >= C10_MIN_MEAN_ABS or frac >= C10_MIN_FRAC
    cpu_ok = None if cpu_seconds is None else cpu_seconds > 0
    status = "PASS" if differ and cpu_ok else ("FAIL" if not differ or cpu_ok is False else "MISSING")
    return row_result("S3.C10", status, "mean|d| %.3f, %.2f %% px, CPU %s s" % (mean_abs, 100 * frac, _fmt(cpu_seconds,
                      "%.2f")), "differ, CPU > 0", what, meanAbs=mean_abs, fracChanged=frac, cpuSeconds=cpu_seconds,
                      sameFile=bool(sha and sha[0] == sha[1]))

def face_world(block: dict, face: dict):
    """A face of the block (SE-local centre/normal/right/up in part space) -> SN centre, normal, right, up, half."""
    p, h, off = block["pose"], block["half"], block.get("offset", (0.0, 0.0, 0.0))
    c = add(tuple(face["centre"]), off)
    return {"centre": sn_apply(p["o"], p["q"], p["s"], c), "normal": sn_dir(p["q"], p["s"], face["normal"]),
            "right": sn_dir(p["q"], p["s"], face["right"]), "up": sn_dir(p["q"], p["s"], face["up"]), "half": h,
            "local": (c, tuple(face["right"]), tuple(face["up"]))}

def facing_face(cam: Camera, block: dict, faces):
    """The face whose outward normal points most towards the camera (and is in view)."""
    best, best_d = None, 0.0
    for f in faces:
        fw = face_world(block, f)
        d = dot(fw["normal"], unit(sub(cam.pos, fw["centre"])))
        if d > best_d and cam.project(fw["centre"]) is not None:
            best, best_d = (f, fw), d
    return best, best_d

def face_point(block: dict, fw: dict, a: float, b: float):
    """SN point at face-local (a * half along right, b * half along up) through the exact pose."""
    p = block["pose"]
    c, r, u = fw["local"]
    h = block["half"]
    loc = add(add(c, mul(r, a * h)), mul(u, b * h))
    return sn_apply(p["o"], p["q"], p["s"], loc)

def sample_disc(img: Img, cam: Camera, p, radius_px: float):
    pr = cam.project(p)
    if pr is None:
        return None
    x, y = pr[0], pr[1]
    if not (0 <= x < img.w and 0 <= y < img.h):
        return None
    r = max(1.0, radius_px)
    poly = [(x + r * math.cos(t), y + r * math.sin(t)) for t in [i * math.pi / 8 for i in range(16)]]
    pix = region_pixels(img, poly)
    if not pix:
        pix = [(int(x), int(y))]
    return stats(img, pix)

def n1_glyph(img: Img, cam: Camera, glyph_block: dict, faces, colours=None) -> dict:
    colours = colours or PLANNED_GLYPH
    what = "N1 glyph: quadrant colours in the expected corners of the most camera-facing glyph face"
    got, facing = facing_face(cam, glyph_block, faces)
    if got is None or facing < 0.3:
        return row_result("S3.N1g", "MISSING", "no glyph face towards the camera", "4/4 quadrants", what)
    f, fw = got
    c0 = cam.project(face_point(glyph_block, fw, -1, -1))
    c1 = cam.project(face_point(glyph_block, fw, 1, 1))
    if c0 is None or c1 is None:
        return row_result("S3.N1g", "MISSING", "face out of view", "4/4 quadrants", what)
    r_px = 0.12 * math.hypot(c1[0] - c0[0], c1[1] - c0[1]) / math.sqrt(2.0)
    want = {"tl": (-0.5, 0.5), "tr": (0.5, 0.5), "bl": (-0.5, -0.5), "br": (0.5, -0.5)}
    seen, ok = {}, 0
    for key, (a, b) in want.items():
        st = sample_disc(img, cam, face_point(glyph_block, fw, a, b), r_px)
        if st is None or st.get("n", 0) == 0:
            seen[key] = None
            continue
        ch = chroma(st["medianRgb"])
        best = min(colours, key=lambda k: sum((x - y) ** 2 for x, y in zip(ch, chroma(colours[k]))))
        seen[key] = best
        ok += 1 if best == key else 0
    return row_result("S3.N1g", "PASS" if ok == 4 else "FAIL", "%d/4 (%s)" % (ok, ",".join("%s=%s" % (k, v) for k, v in
                      seen.items())), "4/4 quadrants", what, face=f.get("face"), seen=seen, facingCos=facing)

# The N1 bump map (tests/standins/fake_se_producer.py glyph_dds("ng"), ): the face is an 8 x 8
# grid of cells (u right, v down); in each colour quadrant (4 x 4 cells) the top two cell rows tilt the normal up or
# down in a checker ((cx + cy) even: up), the bottom two rows tilt it right (+u) or left in a checker. "Up" is SE's
# convention: G > 0.5 with SE's y negation leans the normal towards -v (the top of a side face). A checker inside one
# colour quadrant keeps the albedo equal and cancels any linear brightness gradient across the face.
N1_GRID = 8
N1_LUM_FLOOR = 0.003           # mean linear luminance of the dimmer cells below which N1b cannot judge (8-bit floor)
N1X_MIN_ALONG = 0.15           # |right. toSun| below which the x-tilt test is not judged (information row)

def n1_cell_kind(gx: int, gy: int) -> str:
    """The tilt of glyph cell (gx, gy) (gx right, gy down, 0..7): "U", "D", "R" or "L"."""
    cx, cy = gx % 4, gy % 4
    even = (cx + cy) % 2 == 0
    if cy < 2:
        return "U" if even else "D"
    return "R" if even else "L"

def n1_cells(img: Img, cam: Camera, glyph_block: dict, fw: dict):
    """Mean luminance per (quadrant, kind) over the cell centres of the face (discs a quarter of a cell wide)."""
    c0 = cam.project(face_point(glyph_block, fw, -1, -1))
    c1 = cam.project(face_point(glyph_block, fw, 1, 1))
    if c0 is None or c1 is None:
        return None
    cell_px = math.hypot(c1[0] - c0[0], c1[1] - c0[1]) / math.sqrt(2.0) / N1_GRID
    r_px = max(1.0, 0.22 * cell_px)
    acc = {}
    for gx in range(N1_GRID):
        for gy in range(N1_GRID):
            u, v = (gx + 0.5) / N1_GRID, (gy + 0.5) / N1_GRID
            st = sample_disc(img, cam, face_point(glyph_block, fw, 2.0 * u - 1.0, 1.0 - 2.0 * v), r_px)
            if st is None or not st.get("n"):
                continue
            q = ("t" if gy < N1_GRID // 2 else "b") + ("l" if gx < N1_GRID // 2 else "r")
            acc.setdefault((q, n1_cell_kind(gx, gy)), []).append(st["meanLum"])
    return {k: sum(v) / len(v) for k, v in acc.items()}, cell_px

def n1_bump(img: Img, cam: Camera, glyph_block: dict, faces, sun_dir, tilt=0.45, colours=None) -> dict:
    """S3.N1b "bump faces the sun": on the camera-facing side face, in every colour
 quadrant, the cells whose normal map tilts the normal up are brighter than the down-tilted cells (geometric mean of
 the four quadrant ratios >= 1.03). Light from above (the sun at any azimuth while it is up, and the sky) favours the
 up-tilted cells, so the verdict needs no sun azimuth: it settles SE's green convention (G' = 1 - Y) and the tangent
 handedness w (either one wrong swaps up and down). It cannot see S3.M8 (an sRGB conversion target): the sRGB encode
 is monotonic, so the ordering survives; S3.N1d measures that."""
    what = "N1 bump faces the sun: up-tilted cells brighter than down-tilted ones in every colour quadrant"
    got, facing = facing_face(cam, glyph_block, faces)
    if got is None or facing < 0.3:
        return row_result("S3.N1b", "MISSING", "no glyph face towards the camera", ">= %.2f" % N1_BUMP_MIN_RATIO, what)
    f, fw = got
    if abs(fw["normal"][1]) > 0.3 or dot(unit(fw["up"]), (0.0, 1.0, 0.0)) < 0.9:
        return row_result("S3.N1b", "MISSING", "the facing face is not a level side face (up %s)" % (fw["up"],),
                          ">= %.2f" % N1_BUMP_MIN_RATIO, what)
    res = n1_cells(img, cam, glyph_block, fw)
    if res is None:
        return row_result("S3.N1b", "MISSING", "face out of view", ">= %.2f" % N1_BUMP_MIN_RATIO, what)
    cells, cell_px = res
    ratios, dims = {}, []
    for q in ("tl", "tr", "bl", "br"):
        up, dn = cells.get((q, "U")), cells.get((q, "D"))
        if up is None or dn is None or dn <= 0:
            return row_result("S3.N1b", "MISSING", "quadrant %s cells not seen" % q, ">= %.2f" % N1_BUMP_MIN_RATIO, what,
                              cellPx=cell_px)
        ratios[q] = up / dn
        dims.append(dn)
    if min(dims) < N1_LUM_FLOOR:
        return row_result("S3.N1b", "MISSING", "too dark to judge (dim cells %.4f < %.4f)" % (min(dims), N1_LUM_FLOOR),
                          ">= %.2f" % N1_BUMP_MIN_RATIO, what, ratios=ratios, cellPx=cell_px)
    gm = math.exp(sum(math.log(r) for r in ratios.values()) / len(ratios))
    worst = min(ratios.values())
    ok = gm >= N1_BUMP_MIN_RATIO and worst > 1.0
    return row_result("S3.N1b", "PASS" if ok else "FAIL", "up/down %.3f (worst quadrant %.3f)" % (gm, worst),
                      ">= %.2f" % N1_BUMP_MIN_RATIO, what, ratios=ratios, face=f.get("face"), cellPx=cell_px,
                      sun=list(sun_dir) if sun_dir else None)

def n1_xtilt(img: Img, cam: Camera, glyph_block: dict, faces, sun_dir) -> dict:
    """S3.N1x (information): the right/left-tilted cells, brighter on the sun's side along the face's right vector
 (+u). Judged only when the sun has a horizontal component along the face (|right. toSun| >= 0.15)."""
    what = "N1 x tilt: cells tilted towards the sun along +u are brighter (information; needs a sun along the face)"
    if sun_dir is None:
        return row_result("S3.N1x", "MISSING", "no sun direction", ">= %.2f" % N1_BUMP_MIN_RATIO, what, required=False)
    got, facing = facing_face(cam, glyph_block, faces)
    if got is None or facing < 0.3:
        return row_result("S3.N1x", "MISSING", "no glyph face towards the camera", ">= %.2f" % N1_BUMP_MIN_RATIO, what,
                          required=False)
    _f, fw = got
    along = dot(unit(fw["right"]), unit(sun_dir))
    if abs(along) < N1X_MIN_ALONG:
        return row_result("S3.N1x", "MISSING", "sun along the face %.2f (needs |.| >= %.2f)" % (along, N1X_MIN_ALONG),
                          ">= %.2f" % N1_BUMP_MIN_RATIO, what, required=False)
    res = n1_cells(img, cam, glyph_block, fw)
    if res is None:
        return row_result("S3.N1x", "MISSING", "face out of view", ">= %.2f" % N1_BUMP_MIN_RATIO, what, required=False)
    cells = res[0]
    bright, dim = ("R", "L") if along > 0 else ("L", "R")
    ratios = {}
    for q in ("tl", "tr", "bl", "br"):
        b_, d_ = cells.get((q, bright)), cells.get((q, dim))
        if b_ is None or not d_:
            return row_result("S3.N1x", "MISSING", "quadrant %s cells not seen" % q, ">= %.2f" % N1_BUMP_MIN_RATIO, what,
                              required=False)
        ratios[q] = b_ / d_
    gm = math.exp(sum(math.log(r) for r in ratios.values()) / len(ratios))
    return row_result("S3.N1x", "PASS" if gm >= N1_BUMP_MIN_RATIO else "FAIL", "%s/%s %.3f" % (bright, dim, gm),
                      ">= %.2f" % N1_BUMP_MIN_RATIO, what, required=False, ratios=ratios, along=along)

def c13_paint(img: Img, cam: Camera, face_corners, export: Img, uv_rect) -> dict:
    """face_corners: 4 SN points of the painted face; uv_rect (u0, v0, u1, v1) of that face in SE's exported texture."""
    what = "paint: painted region at P1 vs SE's exported colour at the same UV (neutral light)"
    if export is None:
        return row_result("S3.C13", "MISSING", "no SE colour export", "dE < %.0f" % C13_MAX_DELTA_E, what)
    poly = projected_hull(cam, face_corners)
    pix = region_pixels(img, scale_poly(poly, 0.6)) if poly else []
    if len(pix) < MIN_REGION_PX:
        return row_result("S3.C13", "MISSING", "region %d px" % len(pix), "dE < %.0f" % C13_MAX_DELTA_E, what)
    u0, v0, u1, v1 = uv_rect
    xs = [int(min(export.w - 1, max(0, (u0 + (u1 - u0) * (0.2 + 0.6 * i / 15.0)) * export.w))) for i in range(16)]
    ys = [int(min(export.h - 1, max(0, (v0 + (v1 - v0) * (0.2 + 0.6 * j / 15.0)) * export.h))) for j in range(16)]
    ref = stats(export, [(x, y) for x in xs for y in ys])
    got = stats(img, pix)
    de = delta_e(got["meanLab"], ref["meanLab"])
    return row_result("S3.C13", "PASS" if de < C13_MAX_DELTA_E else "FAIL", "dE %.2f" % de, "dE < %.0f" %
                      C13_MAX_DELTA_E, what, captureLab=got["meanLab"], exportLab=ref["meanLab"])

def c14_seams(img: Img, cam: Camera, seams, inblock) -> dict:
    """seams / inblock: lists of SN segments (a, b). The gradient is taken across each projected segment (luma
 difference of the two pixels 1.5 px either side), sampled every 3 px along it."""
    what = "pattern: mean |gradient| across plate seams vs in-block"

    def grads(segs):
        out = []
        for a, b in segs:
            pa, pb = cam.project(a), cam.project(b)
            if pa is None or pb is None:
                continue
            L = math.hypot(pb[0] - pa[0], pb[1] - pa[1])
            if L < 4:
                continue
            nx, ny = -(pb[1] - pa[1]) / L, (pb[0] - pa[0]) / L
            for i in range(1, int(L / 3)):
                t = i * 3.0 / L
                x, y = pa[0] + (pb[0] - pa[0]) * t, pa[1] + (pb[1] - pa[1]) * t
                x1, y1, x2, y2 = int(x + 1.5 * nx), int(y + 1.5 * ny), int(x - 1.5 * nx), int(y - 1.5 * ny)
                if 0 <= x1 < img.w and 0 <= x2 < img.w and 0 <= y1 < img.h and 0 <= y2 < img.h:
                    out.append(abs(luma8(img.px(x1, y1)) - luma8(img.px(x2, y2))))
        return out
    gs, gi = grads(seams), grads(inblock)
    if len(gs) < 10 or len(gi) < 10:
        return row_result("S3.C14", "MISSING", "samples %d / %d" % (len(gs), len(gi)), "< %.1fx" % C14_MAX_RATIO, what)
    ms, mi = sum(gs) / len(gs), sum(gi) / len(gi)
    ratio = ms / mi if mi > 0 else (0.0 if ms == 0 else float("inf"))
    return row_result("S3.C14", "PASS" if ratio < C14_MAX_RATIO else "FAIL", "%.2f / %.2f = %.2fx" % (ms, mi, ratio),
                      "< %.1fx" % C14_MAX_RATIO, what, seamMean=ms, inBlockMean=mi, samples=[len(gs), len(gi)])

# ----------------------------------------------------------------------------------------------------------------
# Manifest runner (analyze_s3.py writes the manifest; this keeps the image side runnable on its own)
# ----------------------------------------------------------------------------------------------------------------

class Shot:
    """One capture: the PNG, the S1 capture-service sidecar (<png>.json: the Unity camera of the frame) and, for the
 Subnautica lab runs, the S3 sidecar (<png>.s3.json: block pose and corners, buried-corner rays, Seamoth bounds, the
 sun, the time of day)."""

    def __init__(self, png, sidecar=None):
        self.png = png
        self.sidecar = sidecar if isinstance(sidecar, dict) else load_sidecar(png)
        self.s3 = load_s3_sidecar(png)
        if self.sidecar is None and self.s3 is not None:
            self.sidecar = s3_camera_sidecar(self.s3)
        self._img = None
        self._cam = None

    @property
    def img(self):
        if self._img is None and self.png and os.path.exists(self.png):
            self._img = read_png(self.png)
        return self._img

    @property
    def cam(self):
        if self._cam is None and self.sidecar is not None:
            self._cam = Camera.from_sidecar(self.sidecar, self.img)
        return self._cam

    def pair(self):
        return (self.img, self.cam) if self.img is not None and self.cam is not None else None

def load_sidecar(png):
    for p in (png + ".json", os.path.splitext(png)[0] + ".json"):
        if png and os.path.exists(p):
            try:
                with open(p, encoding="utf-8") as f:
                    return json.load(f)
            except (OSError, ValueError):
                return None
    return None

def load_s3_sidecar(png):
    p = (png or "") + ".s3.json"
    if png and os.path.exists(p):
        try:
            with open(p, encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return None
    return None

def s3_camera_sidecar(s3: dict):
    """The S1 sidecar keys from the S3 sidecar's "camera" object (pos, rot, fov, pixelWidth/Height)."""
    c = (s3 or {}).get("camera") or {}
    pos, rot = c.get("pos"), c.get("rot")
    if not (isinstance(pos, list) and len(pos) == 3 and isinstance(rot, list) and len(rot) == 4):
        return None
    return {"unityCamX": pos[0], "unityCamY": pos[1], "unityCamZ": pos[2], "unityQx": rot[0], "unityQy": rot[1],
            "unityQz": rot[2], "unityQw": rot[3], "unityFov": c.get("fov"), "screenW": c.get("pixelWidth"),
            "screenH": c.get("pixelHeight")}

def sun_of(sidecar, s3=None):
    """Unit vector towards the sun (SN world): "sunDir"/"toSun" keys, else minus the forward of the S3 sidecar's sun
 light (the direction its light travels)."""
    for d in (s3, sidecar):
        sun = (d or {}).get("sun") if isinstance(d, dict) else None
        if isinstance(sun, dict) and isinstance(sun.get("forward"), list) and len(sun["forward"]) == 3:
            f = sun["forward"]
            return unit((-float(f[0]), -float(f[1]), -float(f[2])))
    if not isinstance(sidecar, dict):
        return None
    for k in ("sunDir", "sunDirSn", "toSun"):
        v = sidecar.get(k) or (sidecar.get("s3") or {}).get(k)
        if isinstance(v, (list, tuple)) and len(v) == 3:
            return tuple(float(x) for x in v)
    return None

def run_manifest(m: dict) -> list:
    """The manifest (analyze_s3.py): {"scene": {"block": {o,q,s,half,offset}, "glyph": {...} | null, "faces": [...],
 "seamoth": {"lo", "hi"} | null, "seabedY": {...}, "depthM": {...}}, "captures": [{"name", "target", "png",
 "time", "spot", "burst"}], "cpuSeconds": {...}, "paint": {...}, "seams": {...}} -> rows."""
    sc = m.get("scene") or {}
    rows = []

    def objs(spot):
        s = (sc.get("spots") or {}).get(spot) or sc
        b = s.get("block")
        block = block_object(b, b.get("half", 1.25), tuple(b.get("offset", (0, 0, 0)))) if b else None
        sm = s.get("seamoth")
        seamoth = aabb_object(sm["lo"], sm["hi"]) if sm and sm.get("lo") and sm.get("hi") else None
        g = s.get("glyph")
        glyph = block_object(b, b.get("half", 1.25), tuple(g["offset"]), "glyph") if b and g else None
        return block, seamoth, glyph, s
    shots = {}
    for c in m.get("captures") or ():
        if c.get("png") and os.path.exists(c["png"]):
            shots.setdefault((c.get("spot", "deep"), c.get("name"), c.get("target", "both")), []).append(Shot(c["png"]))

    def shot_sun(spot, name, target):
        """The sun of the capture's own sidecars, else of any capture at that spot (the sun is global and moves about
 0.3 deg/s: the lab run of the same spot a few seconds earlier is close enough), else the scene's."""
        for t in (target, "both"):
            for sh in shots.get((spot, name, t)) or ():
                v = sun_of(sh.sidecar, sh.s3)
                if v:
                    return v
        for (sp, _n, _t), lst in shots.items():
            if sp == spot:
                for sh in lst:
                    v = sun_of(sh.sidecar, sh.s3)
                    if v:
                        return v
        v = ((sc.get("spots") or {}).get(spot) or {}).get("sun")
        return tuple(v) if isinstance(v, (list, tuple)) and len(v) == 3 else None

    def one(spot, name, target):
        for t in (target, "both"):
            lst = shots.get((spot, name, t))
            if lst:
                return lst[0].pair()
        return None

    def burst(spot, name, target):
        out = []
        for t in (target, "both"):
            for s in shots.get((spot, name, t)) or ():
                p = s.pair()
                if p:
                    out.append(p)
            if out:
                return out
        return out
    # C5 (deep spot, P2)
    block, seamoth, glyph, s = objs("deep")
    p2 = one("deep", "P2", "block")
    rows.append(c5_occlusion(p2[0], p2[1], block, (s.get("seabedY") or {}).get("block", 0.0)) if p2 and block else
                row_result("S3.C5", "MISSING", "no P2 capture or no block", ">= %.0f dE" % C5_MIN_DELTA_E,
                           "P2 buried half hidden (image region)"))
    rows.append(c6_fog({(p, t): one("deep", p, t) for p in ("P1", "P4") for t in ("block", "seamoth")
                        if one("deep", p, t)}, block, seamoth, s.get("seabedY")) if block else
                row_result("S3.C6", "MISSING", "no block", "+-20 %", "fog"))
    rows.append(c7_sun({(t, tg): one("deep", "P1-" + t, tg) for t in ("noon", "midnight") for tg in ("block", "seamoth")
                        if one("deep", "P1-" + t, tg)}, block, seamoth, s.get("seabedY")) if block else
                row_result("S3.C7", "MISSING", "no block", "+-25 %", "sun"))
    # C8, C9, C10 (shallow spot, P3)
    block_s, seamoth_s, _g, ss = objs("shallow")
    if block_s:
        rows.append(c8_caustics({"block": burst("shallow", "P3-burst", "block"),
                                 "seamoth": burst("shallow", "P3-burst", "seamoth")}, block_s, seamoth_s,
                                (ss.get("depthM") or {}).get("block")))
        p3b, p3s = one("shallow", "P3", "block"), one("shallow", "P3", "seamoth")
        sun = shot_sun("shallow", "P3", "block") if p3b else None
        rows.append(c9_shadow({k: v for k, v in (("block", p3b), ("seamoth", p3s)) if v}, block_s, seamoth_s, sun,
                              ss.get("seabedY") or {}))
        a = shots.get(("shallow", "P3-a", "block")) or shots.get(("shallow", "P3-a", "both"))
        b = shots.get(("shallow", "P3-b", "block")) or shots.get(("shallow", "P3-b", "both"))
        cpu = (m.get("cpuSeconds") or {}).get("c10")
        sha = None
        if a and b:
            sha = [_sha(a[0].png), _sha(b[0].png)]
        rows.append(c10_alive(a[0].img if a else None, b[0].img if b else None, cpu, sha))
    else:
        for cid in ("S3.C8", "S3.C9", "S3.C10"):
            rows.append(row_result(cid, "MISSING", "no shallow placement", "-", "shallow-spot check"))
    # N1 (glyph block; its own placement, spot "n1", fully above the seabed)
    n1_spot = next((k[0] for k in shots if k[1] == "N1"), "n1")
    _b1, _s1, glyph, sn1 = objs(n1_spot)
    faces = (sn1 or {}).get("faces") or sc.get("faces")
    g1 = one(n1_spot, "N1", "glyph")
    if glyph and faces and g1:
        rows.append(n1_glyph(g1[0], g1[1], glyph, faces))
        rows.append(n1_bump(g1[0], g1[1], glyph, faces, shot_sun(n1_spot, "N1", "glyph")))
        rows.append(n1_xtilt(g1[0], g1[1], glyph, faces, shot_sun(n1_spot, "N1", "glyph")))
    else:
        rows.append(row_result("S3.N1g", "MISSING", "no glyph block or capture", "4/4 quadrants", "N1 glyph"))
        rows.append(row_result("S3.N1b", "MISSING", "no glyph block or capture", ">= %.2f" % N1_BUMP_MIN_RATIO,
                               "N1 bump faces the sun"))
    # C13, C14 (S3b plate, spot "plate")
    plate_spot = next((k[0] for k in shots if k[1] in ("P1-plate", "P1-paint")), "plate")
    _bp, _sp, _gp, spl = objs(plate_spot)
    plate = (spl or {}).get("plate")
    pose = (spl or {}).get("block")
    p1p = one(plate_spot, "P1-paint", "block")
    exp_png = (plate or {}).get("exportPng")
    if plate and pose and p1p and exp_png and os.path.exists(exp_png):
        face, _segs = plate_face(p1p[1], pose, plate)
        rows.append(c13_paint(p1p[0], p1p[1], face, read_png(exp_png), plate.get("faceUv") or (0.0, 0.0, 1.0, 1.0)))
    else:
        rows.append(row_result("S3.C13", "MISSING", "no plate paint capture / SE export", "dE < 5", "paint (S3b)"))
    p1s = one(plate_spot, "P1-plate", "block")
    if plate and pose and p1s:
        _face, segs = plate_face(p1s[1], pose, plate)
        rows.append(c14_seams(p1s[0], p1s[1], segs[0], segs[1]))
    else:
        rows.append(row_result("S3.C14", "MISSING", "no plate capture", "< 1.5x", "pattern seams (S3b)"))
    return rows

def plate_face(cam: Camera, pose: dict, plate: dict):
    """For a planar plate of grid cells (integer [i, j, k], cell size 2 * half, grid-local SE space): the 4 SN corners of
 the centre cell's camera-facing face (C13), and the seam / in-block segments on that face (C14): seams on the shared
 edges between neighbouring cells, in-block lines at each cell's quarter and middle lines, all parallel to the seam
 they compare with."""
    o, q, s = pose["o"], pose["q"], pose["s"]
    h = float(plate.get("half", 1.25))
    cells = [tuple(int(v) for v in c) for c in plate["cells"]]
    const = [k for k in range(3) if len(set(c[k] for c in cells)) == 1]
    k0 = const[0] if const else 1
    best = None
    for sgn in (-1, 1):
        n = [0.0, 0.0, 0.0]
        n[k0] = float(sgn)
        c0 = cells[len(cells) // 2]
        fc = tuple(2 * h * c0[i] + (h * sgn if i == k0 else 0.0) for i in range(3))
        w = sn_apply(o, q, s, fc)
        d = dot(sn_dir(q, s, tuple(n)), unit(sub(cam.pos, w)))
        if best is None or d > best[0]:
            best = (d, sgn)
    sgn = best[1]
    a_ax, b_ax = [k for k in range(3) if k != k0]

    def P(cell, da, db):
        loc = [0.0, 0.0, 0.0]
        loc[k0] = 2 * h * cell[k0] + h * sgn
        loc[a_ax] = 2 * h * cell[a_ax] + da * h
        loc[b_ax] = 2 * h * cell[b_ax] + db * h
        return sn_apply(o, q, s, tuple(loc))
    c0 = cells[len(cells) // 2]
    face = [P(c0, da, db) for da in (-1, 1) for db in (-1, 1)]
    cellset = set(cells)
    seams, inblock = [], []
    for c in cells:
        for ax, (lo_l, hi_l) in ((a_ax, (-0.9, 0.9)), (b_ax, (-0.9, 0.9))):
            nb = list(c)
            nb[ax] += 1
            if tuple(nb) in cellset:
                if ax == a_ax:
                    seams.append((P(c, 1.0, lo_l), P(c, 1.0, hi_l)))
                    inblock += [(P(c, f, lo_l), P(c, f, hi_l)) for f in (-0.5, 0.0, 0.5)]
                else:
                    seams.append((P(c, lo_l, 1.0), P(c, hi_l, 1.0)))
                    inblock += [(P(c, lo_l, f), P(c, hi_l, f)) for f in (-0.5, 0.0, 0.5)]
    return face, (seams, inblock)

def _sha(path):
    import hashlib
    try:
        with open(path, "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()
    except OSError:
        return None

# ----------------------------------------------------------------------------------------------------------------
# Self-test: a tiny ray tracer renders synthetic captures of a lab scene; every check must pass on the faithful
# rendering and fail on its seeded failure
# ----------------------------------------------------------------------------------------------------------------

class _Box:
    def __init__(self, name, centre, axes, half, albedo, paint=None):
        self.name, self.centre, self.axes, self.half, self.albedo, self.paint = name, centre, axes, half, albedo, paint

    def hit(self, o, d):
        """Slab test in box space: (t, normal) or None."""
        lo_t, hi_t, n_lo = -1e30, 1e30, None
        rel = sub(o, self.centre)
        for k in range(3):
            a = self.axes[k]
            e, f = dot(a, rel), dot(a, d)
            if abs(f) < 1e-12:
                if abs(e) > self.half[k]:
                    return None
                continue
            t1, t2 = (-self.half[k] - e) / f, (self.half[k] - e) / f
            s = -1.0
            if t1 > t2:
                t1, t2, s = t2, t1, 1.0
            if t1 > lo_t:
                lo_t, n_lo = t1, mul(a, s)
            hi_t = min(hi_t, t2)
            if lo_t > hi_t:
                return None
        if hi_t < 1e-6:
            return None
        return (lo_t, n_lo) if lo_t > 1e-6 else None

class _World:
    """Seabed plane y = seabed, boxes, a sun, exponential fog, animated caustics on up-facing surfaces."""

    def __init__(self, seabed, sun, boxes, fog=0.03, caustic_amp=0.25, t=0.0):
        self.seabed, self.sun, self.boxes, self.fog, self.caustic_amp, self.t = seabed, unit(sun), boxes, fog, \
            caustic_amp, t
        self.no_occlusion = set()          # box names drawn on top of the terrain (seeded C5 failure)
        self.no_fog = set()
        self.no_sun = set()
        self.no_caustics = set()
        self.no_shadow = set()
        self.glyph = None                  # (box name, face info, colours, flip_v, bump_tilt,...)
        self.night = False
        self.night_level = 0.1
        self.grain = True

    def trace(self, o, d):
        best = None
        if d[1] < -1e-9:
            t = (self.seabed - o[1]) / d[1]
            if t > 1e-6:
                best = (t, (0.0, 1.0, 0.0), None)
        for b in self.boxes:
            h = b.hit(o, d)
            if h is None:
                continue
            if best is None or h[0] < best[0] or (b.name in self.no_occlusion and best[2] is None):
                best = (h[0], h[1], b)
        return best

    def shade(self, o, d):
        h = self.trace(o, d)
        water = (20, 60, 90)
        if h is None:
            return tuple(_enc(_LIN[c] * self.night_level) for c in water) if self.night else water
        t, n, b = h
        p = add(o, mul(d, t))
        albedo = b.albedo if b else (150, 140, 110)
        name = b.name if b else "seabed"
        if b and self.glyph and self.glyph[0] == b.name:
            albedo, n = self._glyph(b, p, n)
        sun_on = name not in self.no_sun
        ndl = max(0.0, dot(n, self.sun)) if sun_on else 0.6
        lit = 0.25 + 0.75 * ndl
        if self.night and sun_on:
            lit *= self.night_level
        if name not in self.no_shadow and not self.night:
            sp = add(p, mul(n, 1e-4))
            for ob in self.boxes:
                if ob is not b and ob.name not in self.no_shadow and ob.hit(sp, self.sun):
                    lit = 0.25 + 0.75 * ndl * 0.25
                    break
        if n[1] > 0.7 and name not in self.no_caustics:
            lit *= 1.0 + self.caustic_amp * math.sin(3.0 * p[0] + 2.0 * self.t) * math.sin(3.0 * p[2] - 1.7 * self.t)
        # shading and fog in linear light (as Subnautica renders), encoded to sRGB at the end
        col = [_LIN[int(c)] * lit for c in albedo]
        if name not in self.no_fog:
            f = math.exp(-self.fog * t)
            col = [c * f + _LIN[w] * (self.night_level if self.night else 1.0) * (1.0 - f) for c, w in zip(col, water)]
        return tuple(_enc(c) for c in col)

    def _glyph(self, b, p, n):
        """The N1 glyph face: colour quadrants and the 8 x 8 bump cells (n1_cell_kind); glyph = (box name, faces,
 colours, flip_v, tilt, y_sign, x_sign, y_bias): y_sign -1 is a wrong green convention or tangent handedness,
 y_bias adds the same tilt to every cell (an sRGB conversion target re-encodes 0.5 to about +0.47)."""
        g = self.glyph
        _name, faces, colours, flip_v, tilt = g[:5]
        y_sign = g[5] if len(g) > 5 else 1.0
        x_sign = g[6] if len(g) > 6 else 1.0
        y_bias = g[7] if len(g) > 7 else None
        rel = sub(p, b.centre)
        for f in faces:
            if dot(n, f["normal"]) > 0.99:
                a = dot(rel, f["right"]) / b.half[0]
                c = dot(rel, f["up"]) / b.half[0]
                if flip_v:
                    c = -c
                key = ("t" if c > 0 else "b") + ("l" if a < 0 else "r")
                u, v = (a + 1.0) / 2.0, (1.0 - c) / 2.0
                gx, gy = min(N1_GRID - 1, max(0, int(u * N1_GRID))), min(N1_GRID - 1, max(0, int(v * N1_GRID)))
                kind = n1_cell_kind(gx, gy)
                ty = {"U": tilt, "D": -tilt}.get(kind, 0.0)
                tx = {"R": tilt, "L": -tilt}.get(kind, 0.0)
                if y_bias is not None:      # the monotonic re-encode of S3.M8: both up/down cells lean the same way
                    ty, tx = y_bias(ty), y_bias(tx)
                nn = unit(add(add(f["normal"], mul(f["up"], ty * y_sign)), mul(f["right"], tx * x_sign)))
                return colours[key], nn
        return b.albedo, n

    def render(self, cam: Camera) -> Img:
        out = bytearray()
        for y in range(cam.h):
            for x in range(cam.w):
                c = self.shade(cam.pos, cam.ray(x, y))
                if self.grain:
                    # deterministic sensor-like grain (+-1.5 levels, the same for every frame of a pixel), so regions are
 # not flat at one 8-bit level (real captures never are; image_checks' floors depend on it)
                    h = ((x * 73856093) ^ (y * 19349663)) & 0xFFFF
                    g = (h % 4) - 1.5
                    c = tuple(max(0, min(255, int(round(v + g)))) for v in c)
                out += bytes(c)
        return Img(cam.w, cam.h, out, "synthetic")

def _enc_unit(lin: float) -> float:
    """Linear [0, 1] -> sRGB-encoded [0, 1] (what an sRGB render target stores)."""
    lin = max(0.0, min(1.0, lin))
    return lin * 12.92 if lin <= 0.0031308 else 1.055 * lin ** (1.0 / 2.4) - 0.055

def _enc(lin: float) -> int:
    lin = max(0.0, min(1.0, lin))
    v = lin * 12.92 if lin <= 0.0031308 else 1.055 * lin ** (1.0 / 2.4) - 0.055
    return int(round(255.0 * v))

def _cam_look(pos, target, w=160, h=90, fov=60.0, up=(0.0, 1.0, 0.0)):
    return Camera(pos, look_rotation(sub(target, pos), up), fov, w, h)

def selftest() -> int:
    import tempfile
    results = []

    def expect(row, status, label):
        ok = row["status"] == status
        results.append(ok)
        print("  %s %-44s %-8s %s" % ("ok  " if ok else "BAD ", label, row["status"], row["measured"]))

    print("S3 image_checks self-test (decoder: %s)" % decoder_name())
    # ---- PNG round trip, both decoders ----
    tmp = tempfile.mkdtemp(prefix="de_s3_img_")
    img = Img(7, 5, bytes((x * 30 + y * 7) % 256 for y in range(5) for x in range(7) for _c in range(3)))
    for ft in (0, 1):
        p = os.path.join(tmp, "rt%d.png" % ft)
        write_png(p, img, ft)
        a = read_png(p, force_stdlib=True)
        b = read_png(p)
        results.append(a.data == img.data == b.data)
        print("  %s PNG round trip filter %d: stdlib %s, default (%s) %s" % ("ok  " if results[-1] else "BAD ", ft,
              a.data == img.data, b.decoder, b.data == img.data))
    # ---- the lab scene: block (2.5 m, yaw 30 deg) half buried at y = -10, Seamoth stand-in box 6 m east ----
    seabed = -10.0
    yaw = math.radians(30.0)
    q = (0.0, math.sin(yaw / 2.0), 0.0, math.cos(yaw / 2.0))
    pose = {"o": (0.0, seabed, 0.0), "q": q, "s": 1.0}
    axes = (qrot(q, (1.0, 0.0, 0.0)), qrot(q, (0.0, 1.0, 0.0)), qrot(q, (0.0, 0.0, 1.0)))
    blk = _Box("block", (0.0, seabed, 0.0), axes, (1.25, 1.25, 1.25), (170, 170, 175))
    smc = (6.0, seabed + 0.8, 0.0)
    sm = _Box("seamoth", smc, ((1, 0, 0), (0, 1, 0), (0, 0, 1)), (1.1, 0.8, 2.0), (215, 215, 220))
    block = block_object(pose, 1.25)
    seamoth = aabb_object(sub(smc, (1.1, 0.8, 2.0)), add(smc, (1.1, 0.8, 2.0)))
    sun = unit((0.55, 0.75, 0.35))

    def world(**kw):
        w = _World(seabed, sun, [blk, sm], **kw)
        return w

    W = world()
    # C5: P2 from the side, 0.3 m above the seabed
    cam_p2 = _cam_look((-6.0, seabed + 0.3, 0.5), (0.0, seabed, 0.0))
    expect(c5_occlusion(W.render(cam_p2), cam_p2, block, seabed), "PASS", "C5 terrain occludes")
    W2 = world()
    W2.no_occlusion.add("block")
    expect(c5_occlusion(W2.render(cam_p2), cam_p2, block, seabed), "FAIL", "C5 seeded: block drawn over terrain")
    # C6: P1 (6 m) and P4 (40 m) per target
    def pair(w, target, dist):
        c = block["centre"] if target == "block" else seamoth["centre"]
        if dist > 10:
            cam = _cam_look((c[0], seabed + 0.8, c[2] - dist), (c[0], c[1], c[2]), w=240, h=135, fov=20.0)
        else:
            cam = _cam_look((c[0], seabed + 0.8, c[2] - dist), (c[0], c[1], c[2]))
        return w.render(cam), cam
    shots = {(p, t): pair(W, t, d) for p, d in (("P1", 6.0), ("P4", 40.0)) for t in ("block", "seamoth")}
    sb_ = {"block": seabed, "seamoth": seabed}
    expect(c6_fog(shots, block, seamoth, sb_), "PASS", "C6 fog like native")
    Wn = world()
    Wn.no_fog.add("block")
    shots_n = dict(shots)
    for p, d in (("P1", 6.0), ("P4", 40.0)):
        shots_n[(p, "block")] = pair(Wn, "block", d)
    expect(c6_fog(shots_n, block, seamoth, sb_), "FAIL", "C6 seeded: block not fogged")
    # C7: noon/midnight
    Wd = world()
    Wm = world()
    Wm.night = True
    sh7 = {("noon", t): pair(Wd, t, 6.0) for t in ("block", "seamoth")}
    sh7.update({("midnight", t): pair(Wm, t, 6.0) for t in ("block", "seamoth")})
    expect(c7_sun(sh7, block, seamoth, sb_), "PASS", "C7 sun response")
    Wd2, Wm2 = world(), world()
    Wm2.night = True
    for w in (Wd2, Wm2):
        w.no_sun.add("block")
    sh7b = dict(sh7)
    sh7b[("noon", "block")] = pair(Wd2, "block", 6.0)
    sh7b[("midnight", "block")] = pair(Wm2, "block", 6.0)
    expect(c7_sun(sh7b, block, seamoth, sb_), "FAIL", "C7 seeded: block ignores the sun")
    # the 8-bit noise floor: a near-black midnight without grain cannot carry the ratio
    Wm3 = world()
    Wm3.night, Wm3.night_level, Wm3.grain = True, 0.0015, False
    sh7c = dict(sh7)
    sh7c.update({("midnight", t): pair(Wm3, t, 6.0) for t in ("block", "seamoth")})
    expect(c7_sun(sh7c, block, seamoth, sb_), "MISSING", "C7 midnight at the 8-bit floor: inconclusive")
    # C8: P3 burst, top-down 10 m above each object

    def top(target, w):
        c = block["centre"] if target == "block" else seamoth["centre"]
        cam = _cam_look((c[0], c[1] + 10.0, c[2]), c, up=(0.0, 0.0, 1.0))
        return w.render(cam), cam
    burst_b, burst_s, burst_b_bad = [], [], []
    for k in range(8):
        wk = world(t=k * 0.7)
        burst_b.append(top("block", wk))
        burst_s.append(top("seamoth", wk))
        wk2 = world(t=k * 0.7)
        wk2.no_caustics.add("block")
        burst_b_bad.append(top("block", wk2))
    expect(c8_caustics({"block": burst_b, "seamoth": burst_s}, block, seamoth, 12.0), "PASS", "C8 caustics")
    expect(c8_caustics({"block": burst_b_bad, "seamoth": burst_s}, block, seamoth, 12.0), "FAIL",
           "C8 seeded: no caustics on the block")
    expect(c8_caustics({"block": burst_b, "seamoth": burst_s}, block, seamoth, 150.0), "MISSING",
           "C8 at -150 m: not applicable (MISSING)")
    # C9: shadows (top-down, low sun)
    sun_low = unit((0.8, 0.45, 0.2))
    Wl = _World(seabed, sun_low, [blk, sm])
    sh9 = {"block": top("block", Wl), "seamoth": top("seamoth", Wl)}
    expect(c9_shadow(sh9, block, seamoth, sun_low, {"block": seabed, "seamoth": seabed}), "PASS", "C9 shadow")
    Wl2 = _World(seabed, sun_low, [blk, sm])
    Wl2.no_shadow.add("block")
    sh9b = {"block": top("block", Wl2), "seamoth": sh9["seamoth"]}
    expect(c9_shadow(sh9b, block, seamoth, sun_low, {"block": seabed, "seamoth": seabed}), "FAIL",
           "C9 seeded: the block casts no shadow")
    # C10: alive / the reused file (S3.M5)
    a, b = burst_b[0][0], burst_b[3][0]
    expect(c10_alive(a, b, 0.8), "PASS", "C10 two captures differ, CPU used")
    expect(c10_alive(a, a, 0.8, ["x", "x"]), "FAIL", "C10 seeded S3.M5: capture file reused")
    # N1 glyph and bump on the camera-facing face of a second (glyph) block
    faces = []
    for n_, r_, u_ in (((0, 0, 1), (1, 0, 0), (0, 1, 0)), ((0, 0, -1), (-1, 0, 0), (0, 1, 0)),
                       ((1, 0, 0), (0, 0, -1), (0, 1, 0)), ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
                       ((0, 1, 0), (1, 0, 0), (0, 0, -1)), ((0, -1, 0), (1, 0, 0), (0, 0, 1))):
        faces.append({"face": len(faces), "centre": mul(n_, 1.25), "normal": n_, "right": r_, "up": u_})
    goff = (0.0, 2.5, 0.0)              # the glyph block sits on top of the block (grid cell above), above the seabed
    gpose_c = sn_apply(pose["o"], q, 1.0, goff)
    gbox = _Box("glyph", gpose_c, axes, (1.25, 1.25, 1.25), (128, 128, 128))
    gblock = block_object(pose, 1.25, goff, "glyph")
    world_faces = [{"normal": sn_dir(q, 1.0, f["normal"]), "right": sn_dir(q, 1.0, f["right"]),
                    "up": sn_dir(q, 1.0, f["up"])} for f in faces]
    sun_g = unit((0.6, 0.5, 0.6))
    # camera in front of the face whose SE-local normal is -Z (Unity +Z after the mirror)
    cam_g = _cam_look(add(gpose_c, mul(sn_dir(q, 1.0, (0, 0, -1)), 4.5)), gpose_c, w=320, h=192)
    Wg = _World(seabed, sun_g, [blk, sm, gbox])
    Wg.glyph = ("glyph", world_faces, PLANNED_GLYPH, False, 0.45)
    ig = Wg.render(cam_g)
    expect(n1_glyph(ig, cam_g, gblock, faces), "PASS", "N1 glyph reads correctly")
    expect(n1_bump(ig, cam_g, gblock, faces, sun_g), "PASS", "N1 bump faces the sun (up-tilted cells)")
    n1x = n1_xtilt(ig, cam_g, gblock, faces, sun_g)
    expect(n1x, "PASS" if n1x["status"] != "MISSING" else "MISSING", "N1x x tilt (information)")
    Wg2 = _World(seabed, sun_g, [blk, sm, gbox])
    Wg2.glyph = ("glyph", world_faces, PLANNED_GLYPH, True, 0.45)
    expect(n1_glyph(Wg2.render(cam_g), cam_g, gblock, faces), "FAIL", "N1 seeded: V flipped (DDS row order)")
    Wg3 = _World(seabed, sun_g, [blk, sm, gbox])
    Wg3.glyph = ("glyph", world_faces, PLANNED_GLYPH, False, 0.45, -1.0)
    expect(n1_bump(Wg3.render(cam_g), cam_g, gblock, faces, sun_g), "FAIL", "N1 seeded: G' = Y kept (or w wrong)")
    # noon (the sun overhead): the up/down test still judges (the old left/right test was MISSING there)
    sun_noon = unit((0.02, 1.0, 0.01))
    Wg4 = _World(seabed, sun_noon, [blk, sm, gbox])
    Wg4.glyph = ("glyph", world_faces, PLANNED_GLYPH, False, 0.45)
    expect(n1_bump(Wg4.render(cam_g), cam_g, gblock, faces, sun_noon), "PASS", "N1 bump at noon (vertical sun)")
    # an sRGB conversion target (S3.M8) keeps the ordering: N1b cannot see it (S3.N1d does, in the plugin)
    enc = lambda t: 2.0 * (_enc_unit(0.5 + 0.5 * t)) - 1.0     # noqa: E731
    Wg5 = _World(seabed, sun_g, [blk, sm, gbox])
    Wg5.glyph = ("glyph", world_faces, PLANNED_GLYPH, False, 0.45, 1.0, 1.0, enc)
    expect(n1_bump(Wg5.render(cam_g), cam_g, gblock, faces, sun_g), "PASS",
           "N1b under an sRGB target: still PASS (monotonic)")
    # C13 paint: the export = flat colour; the capture under "neutral light" renders the block albedo
    export = Img(8, 8, bytes(c for _ in range(64) for c in (170, 170, 175)))
    face_c = [add(block["centre"], add(mul(axes[0], sx * 1.25), add(mul(axes[1], sy * 1.25), mul(axes[2], -1.25))))
              for sx in (-1, 1) for sy in (0.2, 1)]
    Wneutral = world(fog=0.0)
    Wneutral.no_sun.add("block")
    cam_p1 = _cam_look(add(block["centre"], mul(axes[2], -6.0)), block["centre"])
    i_neu = Img(cam_p1.w, cam_p1.h, bytes(c for _ in range(cam_p1.w * cam_p1.h) for c in (170, 170, 175)))
    expect(c13_paint(i_neu, cam_p1, face_c, export, (0.0, 0.0, 0.5, 1.0)), "PASS", "C13 paint dE")
    i_bad = Img(cam_p1.w, cam_p1.h, bytes(c for _ in range(cam_p1.w * cam_p1.h) for c in (170, 120, 100)))
    expect(c13_paint(i_bad, cam_p1, face_c, export, (0.0, 0.0, 0.5, 1.0)), "FAIL", "C13 seeded: wrong paint colour")
    # C14 seams: a 3-block plate face as a texture with a smooth gradient (continuous) vs a jump at the seams
    cam14 = Camera((0.0, 0.0, -6.0), (0.0, 0.0, 0.0, 1.0), 60.0, 240, 120)

    def plate(jump):
        out = bytearray()
        for y in range(cam14.h):
            for x in range(cam14.w):
                d = cam14.ray(x, y)
                t = 6.0 / d[2]
                px = d[0] * t
                base = 100 + 12 * px + 3 * math.sin(y * 0.7)       # constant gradient across the seams
                if jump:
                    base += 25 * (math.floor((px + 3.75) / 2.5) % 2)
                v = int(max(0, min(255, base)))
                out += bytes((v, v, v))
        return Img(cam14.w, cam14.h, out)
    seams = [((-1.25, -1.0, 0.0), (-1.25, 1.0, 0.0)), ((1.25, -1.0, 0.0), (1.25, 1.0, 0.0))]
    inblk = [((x, -1.0, 0.0), (x, 1.0, 0.0)) for x in (-2.5, -0.6, 0.6, 2.5)]
    expect(c14_seams(plate(False), cam14, seams, inblk), "PASS", "C14 continuous pattern")
    expect(c14_seams(plate(True), cam14, seams, inblk), "FAIL", "C14 seeded: pattern jumps at the seams")
    # projection convention: the sidecar camera (S1 keys) and a known point
    sc = {"unityCamX": 0.0, "unityCamY": 0.0, "unityCamZ": 0.0, "unityQx": 0.0, "unityQy": 0.0, "unityQz": 0.0,
          "unityQw": 1.0, "unityFov": 90.0, "screenW": 200, "screenH": 100}
    cam = Camera.from_sidecar(sc)
    p = cam.project((1.0, 0.5, 1.0))
    ok = cam is not None and abs(p[0] - 150.0) < 1e-9 and abs(p[1] - 25.0) < 1e-9
    results.append(ok)
    print("  %s Unity projection from a sidecar: (1, 0.5, 1) at fov 90, 200x100 -> %s" % ("ok  " if ok else "BAD ",
                                                                                         [round(v, 6) for v in p[:2]]))
    n_bad = sum(1 for r in results if not r)
    print("IMAGE CHECKS SELFTEST %s (%d cases, %d wrong)" % ("PASS" if not n_bad else "FAIL", len(results), n_bad))
    return 0 if not n_bad else 1

def main(argv) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="S3 image oracles (C5-C10, C13, C14, N1)")
    ap.add_argument("--selftest", action="store_true")
    sub_ = ap.add_subparsers(dest="cmd")
    p = sub_.add_parser("info")
    p.add_argument("png")
    p = sub_.add_parser("run")
    p.add_argument("manifest")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.cmd == "info":
        img = read_png(a.png)
        sc = load_sidecar(a.png)
        cam = Camera.from_sidecar(sc, img) if sc else None
        print(json.dumps({"png": a.png, "size": [img.w, img.h], "decoder": img.decoder, "sidecar": bool(sc),
                          "camera": None if cam is None else {"pos": cam.pos, "rot": cam.rot, "fov": cam.fov,
                                                              "screen": [cam.w, cam.h]}, "sun": sun_of(sc)}, indent=1))
        return 0
    if a.cmd == "run":
        with open(a.manifest, encoding="utf-8") as f:
            m = json.load(f)
        rows = run_manifest(m)
        for r in rows:
            print("%-8s %-8s %-40s %s" % (r["id"], r["status"], str(r["measured"])[:40], r["what"]))
        return 0 if not any(r["status"] == "FAIL" for r in rows) else 1
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
