"""S7t T6 oracle, compare.py's 'patch' mode (ROADMAP "### S7t Seamless transition" T6
suit is white in Subnautica, the white/yellow ship beige/tan"): the mean colour of a user-given rectangle in an SE
screenshot and in a Subnautica screenshot of the same thing (the suit, a painted hull face), and whether Subnautica shows
SE's paint with the same hue. Lighting differs between the two games, so brightness is reported, not judged; the verdict
is about the paint: the same hue (both chromatic), or both neutral (white, grey, black). Pillow when importable (PNG,
JPEG: Steam screenshots), else a stdlib PNG reader (8-bit RGB/RGBA, not interlaced). Run with python -I.

 python -I tests/s7t/compare.py patch SE.png SN.png --patch suit=X,Y,W,H/X,Y,W,H [--patch hull=...] [--json]
 python -I tests/s7t/colour_patch.py SE.png SN.png --patch... # the same, directly
 python -I tests/s7t/colour_patch.py --selftest

A rectangle is X,Y,W,H in pixels (integers) or in fractions of the image (all four in 0..1 with a decimal point, e.g.
0.45,0.60,0.05,0.08): the two games' screenshots may differ in size. The part before '/' is the SE rectangle, the part
after it Subnautica's (one rectangle for both when there is no '/'). Per image: mean sRGB, luma (Rec. 709 on the sRGB
bytes, the ROADMAP's L), HSV of the mean, CIELAB (D65); per patch: dHue (degrees, circular), dE76, chroma ratio.
Verdict per patch: PASS when both means are chromatic (HSV saturation >= --chroma, default 0.08), dHue <= --hue-tol
(default 15 deg) and their saturations within --sat-ratio (default 0.5: a tan is a washed-out yellow), or both are neutral; FAIL when one is chromatic and the other neutral (the bug: SE pink, Subnautica
white) or the hues differ more; --de-max N also gates dE76. Exit 0 when every patch passes, 1 otherwise, 2 on bad input.
"""
import argparse
import json
import math
import os
import struct
import sys
import zlib

HUE_TOL_DEFAULT = 15.0
CHROMA_DEFAULT = 0.08
SAT_RATIO_DEFAULT = 0.5

# ----------------------------------------------------------------------------------------------------------- images

class Image(object):
    """RGB pixels as a flat bytearray (3 bytes per pixel), row-major from the top."""

    def __init__(self, width, height, rgb, source=""):
        self.width = width
        self.height = height
        self.rgb = rgb
        self.source = source

    def pixel(self, x, y):
        o = (y * self.width + x) * 3
        return self.rgb[o], self.rgb[o + 1], self.rgb[o + 2]

def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c

def decode_png(data, source=""):
    """Stdlib PNG reader: 8-bit RGB (colour type 2) or RGBA (6), not interlaced; alpha dropped."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG file")
    pos, idat, w, h, ctype = 8, [], 0, 0, -1
    while pos + 8 <= len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            w, h, depth, ctype, _comp, _filt, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8 or ctype not in (2, 6) or interlace != 0:
                raise ValueError("PNG reader: only 8-bit RGB/RGBA, not interlaced (install Pillow for others)")
        elif kind == b"IDAT":
            idat.append(body)
        elif kind == b"IEND":
            break
    if w <= 0 or h <= 0:
        raise ValueError("PNG without IHDR")
    bpp = 3 if ctype == 2 else 4
    raw = zlib.decompress(b"".join(idat))
    stride = w * bpp
    out = bytearray(w * h * 3)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        f = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + ((a + b) >> 1)) & 255
            elif f == 4:
                line[i] = (line[i] + _paeth(a, b, c)) & 255
            elif f != 0:
                raise ValueError("PNG filter %d" % f)
        for x in range(w):
            s = x * bpp
            d = (y * w + x) * 3
            out[d:d + 3] = line[s:s + 3]
        prev = line
    return Image(w, h, out, source)

def encode_png(img):
    """Minimal PNG writer (RGB, filter 0) for the self-test."""
    raw = bytearray()
    for y in range(img.height):
        raw.append(0)
        raw += img.rgb[y * img.width * 3:(y + 1) * img.width * 3]

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xffffffff)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", img.width, img.height, 8, 2, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(bytes(raw))) + chunk(b"IEND", b""))

def load_image(path):
    try:
        from PIL import Image as PilImage  # Pillow 12.3.10 (S3 image_checks note)
    except Exception:
        PilImage = None
    if PilImage is not None:
        with PilImage.open(path) as im:
            im = im.convert("RGB")
            return Image(im.width, im.height, bytearray(im.tobytes()), path)
    with open(path, "rb") as f:
        return decode_png(f.read(), path)

# ----------------------------------------------------------------------------------------------------------- colour

def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

def rgb_to_hsv(r, g, b):
    """r, g, b in 0..1: hue in degrees, saturation, value (the textbook HSV)."""
    mx, mn = max(r, g, b), min(r, g, b)
    d = mx - mn
    if d <= 0:
        return 0.0, 0.0, mx
    if mx == r:
        h = ((g - b) / d) % 6.0
    elif mx == g:
        h = (b - r) / d + 2.0
    else:
        h = (r - g) / d + 4.0
    return h * 60.0, (d / mx if mx > 0 else 0.0), mx

def rgb_to_lab(r, g, b):
    """sRGB 0..1 → CIELAB (D65)."""
    lr, lg, lb = srgb_to_linear(r), srgb_to_linear(g), srgb_to_linear(b)
    x = (0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / 0.95047
    y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb
    z = (0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / 1.08883

    def f(t):
        return t ** (1.0 / 3.0) if t > 216.0 / 24389.0 else (24389.0 / 27.0 * t + 16.0) / 116.0

    fx, fy, fz = f(x), f(y), f(z)
    return 116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz)

def hue_difference(a, b):
    d = abs(a - b) % 360.0
    return 360.0 - d if d > 180.0 else d

# ----------------------------------------------------------------------------------------------------------- patches

def parse_rect(text):
    """'X,Y,W,H' → (values, fractional): integers are pixels; four numbers in 0..1 with a decimal point are fractions."""
    parts = [p.strip() for p in text.split(",")]
    if len(parts) != 4:
        raise ValueError("rectangle '%s': X,Y,W,H" % text)
    vals = [float(p) for p in parts]
    frac = all("." in p for p in parts) and all(0.0 <= v <= 1.0 for v in vals)
    if not frac and any(v != int(v) for v in vals):
        raise ValueError("rectangle '%s': integers (pixels) or four fractions 0..1" % text)
    if vals[2] <= 0 or vals[3] <= 0:
        raise ValueError("rectangle '%s': width and height > 0" % text)
    return vals, frac

def parse_patch(text):
    """'label=SE_RECT/SN_RECT' (or 'label=RECT' for both, or no label)."""
    label = "patch"
    if "=" in text:
        label, text = text.split("=", 1)
    se, sn = (text.split("/", 1) + [None])[:2] if "/" in text else (text, text)
    return label.strip() or "patch", parse_rect(se), parse_rect(sn)

def pixel_rect(img, rect):
    (x, y, w, h), frac = rect
    if frac:
        x, y, w, h = x * img.width, y * img.height, w * img.width, h * img.height
    x0, y0 = int(round(x)), int(round(y))
    x1, y1 = int(round(x + w)), int(round(y + h))
    x0, y0 = max(0, min(img.width, x0)), max(0, min(img.height, y0))
    x1, y1 = max(0, min(img.width, x1)), max(0, min(img.height, y1))
    if x1 <= x0 or y1 <= y0:
        raise ValueError("rectangle outside the %dx%d image %s" % (img.width, img.height, img.source))
    return x0, y0, x1, y1

def measure(img, rect):
    x0, y0, x1, y1 = pixel_rect(img, rect)
    sr = sg = sb = 0
    n = 0
    for yy in range(y0, y1):
        o = (yy * img.width + x0) * 3
        row = img.rgb[o:o + (x1 - x0) * 3]
        sr += sum(row[0::3])
        sg += sum(row[1::3])
        sb += sum(row[2::3])
        n += x1 - x0
    r, g, b = sr / float(n), sg / float(n), sb / float(n)
    hue, sat, val = rgb_to_hsv(r / 255.0, g / 255.0, b / 255.0)
    lab = rgb_to_lab(r / 255.0, g / 255.0, b / 255.0)
    return {
        "rect": [x0, y0, x1 - x0, y1 - y0], "pixels": n,
        "rgb": [round(r, 1), round(g, 1), round(b, 1)],
        "luma": round(0.2126 * r + 0.7152 * g + 0.0722 * b, 1),
        "hsv": [round(hue, 1), round(sat, 3), round(val, 3)],
        "lab": [round(lab[0], 2), round(lab[1], 2), round(lab[2], 2)],
    }

def judge(se, sn, hue_tol=HUE_TOL_DEFAULT, chroma=CHROMA_DEFAULT, de_max=None, sat_ratio=SAT_RATIO_DEFAULT):
    se_chromatic, sn_chromatic = se["hsv"][1] >= chroma, sn["hsv"][1] >= chroma
    dhue = hue_difference(se["hsv"][0], sn["hsv"][0])
    de = math.sqrt(sum((a - b) ** 2 for a, b in zip(se["lab"], sn["lab"])))
    c_se = math.hypot(se["lab"][1], se["lab"][2])
    c_sn = math.hypot(sn["lab"][1], sn["lab"][2])
    if se_chromatic and sn_chromatic:
        ratio = min(se["hsv"][1], sn["hsv"][1]) / max(se["hsv"][1], sn["hsv"][1])
        ok = dhue <= hue_tol and ratio >= sat_ratio
        why = "both coloured, dHue %.1f deg (tolerance %.0f), saturation %.3f / %.3f (ratio %.2f, at least %.2f)" % (
            dhue, hue_tol, se["hsv"][1], sn["hsv"][1], ratio, sat_ratio)
    elif not se_chromatic and not sn_chromatic:
        ok = True
        why = "both neutral (saturation %.3f / %.3f < %.2f)" % (se["hsv"][1], sn["hsv"][1], chroma)
    else:
        ok = False
        why = ("SE coloured (S %.3f, hue %.0f) but Subnautica neutral (S %.3f)" % (se["hsv"][1], se["hsv"][0], sn["hsv"][1]) if se_chromatic
               else "SE neutral (S %.3f) but Subnautica coloured (S %.3f, hue %.0f)" % (se["hsv"][1], sn["hsv"][1], sn["hsv"][0]))
    if de_max is not None and de > de_max:
        ok = False
        why += "; dE76 %.1f > %.1f" % (de, de_max)
    return {"verdict": "PASS" if ok else "FAIL", "why": why, "dHue": round(dhue, 1), "dE76": round(de, 2),
            "chromaRatio": round(c_sn / c_se, 3) if c_se > 1e-6 else None,
            "lumaRatio": round(sn["luma"] / se["luma"], 3) if se["luma"] > 1e-6 else None}

def run(se_img, sn_img, patches, hue_tol=HUE_TOL_DEFAULT, chroma=CHROMA_DEFAULT, de_max=None, sat_ratio=SAT_RATIO_DEFAULT):
    out = []
    for label, se_rect, sn_rect in patches:
        se = measure(se_img, se_rect)
        sn = measure(sn_img, sn_rect)
        row = {"id": "S7t.T6." + label, "se": se, "sn": sn}
        row.update(judge(se, sn, hue_tol, chroma, de_max, sat_ratio))
        out.append(row)
    return out

def describe(rows):
    lines = []
    for r in rows:
        lines.append("%-22s %-4s %s | SE rgb %s luma %s hsv %s | SN rgb %s luma %s hsv %s | dE76 %s chroma x%s luma x%s" % (
            r["id"], r["verdict"], r["why"], r["se"]["rgb"], r["se"]["luma"], r["se"]["hsv"], r["sn"]["rgb"], r["sn"]["luma"],
            r["sn"]["hsv"], r["dE76"], r["chromaRatio"], r["lumaRatio"]))
    return "\n".join(lines)

# ----------------------------------------------------------------------------------------------------------- self-test

def _solid(w, h, rgb, rect=None, fill=(0, 0, 0)):
    data = bytearray(bytes(fill) * (w * h))
    x0, y0, x1, y1 = rect if rect else (0, 0, w, h)
    for y in range(y0, y1):
        for x in range(x0, x1):
            o = (y * w + x) * 3
            data[o:o + 3] = bytes(rgb)
    return Image(w, h, data, "synthetic")

def selftest():
    fails = []

    def expect(cond, what):
        if not cond:
            fails.append(what)

    # rectangles: pixels and fractions; the patch grammar
    expect(parse_rect("10,20,30,40") == ([10.0, 20.0, 30.0, 40.0], False), "pixel rect")
    expect(parse_rect("0.5,0.25,0.1,0.1")[1] is True, "fraction rect")
    for bad in ("1,2,3", "0,0,0,5", "1.5,2,3,4"):
        try:
            parse_rect(bad)
            fails.append("bad rect accepted: " + bad)
        except ValueError:
            pass
    label, a, b = parse_patch("suit=1,2,3,4/0.1,0.2,0.3,0.4")
    expect(label == "suit" and a[1] is False and b[1] is True, "patch grammar")
    label, a, b = parse_patch("5,5,2,2")
    expect(label == "patch" and a == b, "one rect for both")
    # SE's pink suit (sRGB from the shader on SE's 144 grey: key H 0.92, S 0.6) against the same pink darker in
 # Subnautica's light: PASS; against the grey the old formula drew: FAIL; white vs light grey: PASS (both neutral)
    se = _solid(40, 30, (233, 93, 160), (10, 10, 20, 20), (20, 40, 60))
    sn_ok = _solid(64, 48, (150, 62, 104), (16, 16, 32, 32), (90, 120, 150))
    sn_bad = _solid(64, 48, (205, 205, 205), (16, 16, 32, 32), (90, 120, 150))
    rows = run(se, sn_ok, [parse_patch("suit=10,10,10,10/0.25,0.3333,0.25,0.3333")])
    expect(rows[0]["verdict"] == "PASS", "pink vs darker pink: " + describe(rows))
    expect(abs(rows[0]["se"]["rgb"][0] - 233) < 1e-9 and rows[0]["se"]["pixels"] == 100, "mean of the rect only")
    rows = run(se, sn_bad, [parse_patch("suit=10,10,10,10/16,16,16,16")])
    expect(rows[0]["verdict"] == "FAIL" and "neutral" in rows[0]["why"], "pink vs grey: " + describe(rows))
    white_se = _solid(8, 8, (232, 232, 232))
    beige_sn = _solid(8, 8, (205, 190, 160))
    grey_sn = _solid(8, 8, (190, 192, 195))
    expect(run(white_se, grey_sn, [parse_patch("hull=0,0,8,8")])[0]["verdict"] == "PASS", "white vs grey: both neutral")
    expect(run(white_se, beige_sn, [parse_patch("hull=0,0,8,8")])[0]["verdict"] == "FAIL", "white vs beige (the owner's ship): FAIL")
    yellow_se = _solid(8, 8, (185, 140, 56))
    yellow_sn = _solid(8, 8, (120, 92, 40))
    tan_sn = _solid(8, 8, (160, 150, 135))
    expect(run(yellow_se, yellow_sn, [parse_patch("y=0,0,8,8")])[0]["verdict"] == "PASS", "yellow vs darker yellow")
    expect(run(yellow_se, tan_sn, [parse_patch("y=0,0,8,8")])[0]["verdict"] == "FAIL", "yellow vs tan")
    expect(run(yellow_se, yellow_sn, [parse_patch("y=0,0,8,8")], de_max=5.0)[0]["verdict"] == "FAIL", "dE gate")
    # hue wraps around 0/360; Lab of white
    expect(abs(hue_difference(355.0, 5.0) - 10.0) < 1e-9, "hue wrap")
    L, A, B = rgb_to_lab(1.0, 1.0, 1.0)
    expect(abs(L - 100.0) < 0.01 and abs(A) < 0.01 and abs(B) < 0.01, "Lab white")
    # the stdlib PNG reader round-trips the writer, and agrees with Pillow when Pillow is there
    img = _solid(7, 5, (12, 200, 99), (2, 1, 5, 4), (250, 3, 140))
    back = decode_png(encode_png(img))
    expect(back.width == 7 and back.height == 5 and back.rgb == img.rgb, "PNG round trip")
    try:
        import io
        from PIL import Image as PilImage
        with PilImage.open(io.BytesIO(encode_png(img))) as im:
            expect(bytearray(im.convert("RGB").tobytes()) == img.rgb, "Pillow agrees")
    except ImportError:
        pass
    # rectangles outside the image are refused
    try:
        measure(img, (([20.0, 20.0, 2.0, 2.0]), False))
        fails.append("rect outside accepted")
    except ValueError:
        pass
    if fails:
        print("S7t T6 colour_patch selftest FAIL:\n  " + "\n  ".join(fails))
        return 1
    print("S7t T6 colour_patch selftest PASS (rect grammar, means, verdicts: pink/pink PASS, pink/grey FAIL, white/grey PASS, "
          "white/beige FAIL, yellow/tan FAIL, dE gate, PNG reader)")
    return 0

# ----------------------------------------------------------------------------------------------------------- CLI

def main(argv=None):
    ap = argparse.ArgumentParser(prog="compare.py patch", description="S7t T6: SE's paint in Subnautica, one rectangle per screenshot")
    ap.add_argument("se", nargs="?", help="SE screenshot (PNG/JPEG)")
    ap.add_argument("sn", nargs="?", help="Subnautica screenshot of the same thing")
    ap.add_argument("--patch", action="append", default=[], help="label=X,Y,W,H/X,Y,W,H (SE/Subnautica; pixels or fractions)")
    ap.add_argument("--hue-tol", type=float, default=HUE_TOL_DEFAULT, help="largest hue difference in degrees (default 15)")
    ap.add_argument("--chroma", type=float, default=CHROMA_DEFAULT, help="HSV saturation from which a mean counts as coloured (default 0.08)")
    ap.add_argument("--sat-ratio", type=float, default=SAT_RATIO_DEFAULT, help="smallest saturation ratio between the two means (default 0.5: tan is not yellow)")
    ap.add_argument("--de-max", type=float, default=None, help="also gate the CIELAB dE76")
    ap.add_argument("--json", action="store_true", help="JSON rows instead of text")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.se or not a.sn or not a.patch:
        ap.print_usage()
        print("need SE.png SN.png and at least one --patch")
        return 2
    try:
        patches = [parse_patch(p) for p in a.patch]
        se_img, sn_img = load_image(a.se), load_image(a.sn)
        rows = run(se_img, sn_img, patches, a.hue_tol, a.chroma, a.de_max, a.sat_ratio)
    except (ValueError, OSError) as e:
        print("compare.py patch: " + str(e))
        return 2
    if a.json:
        print(json.dumps({"mode": "patch", "se": os.path.abspath(a.se), "sn": os.path.abspath(a.sn), "rows": rows}, indent=1))
    else:
        print(describe(rows))
    return 0 if all(r["verdict"] == "PASS" for r in rows) else 1

if __name__ == "__main__":
    sys.exit(main())
