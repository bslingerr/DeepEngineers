#!/usr/bin/env python3
"""S7 image checks of the interiors and lights build pass (ROADMAP S7.N1 "fog", S7.N3 "ratio"; stdlib only).

S7.N1.fog: Subnautica captures of the sealed room with the IsInsideWalkable patch on and off (``de.interior {patch:
on|off}``), probed at (a) the inner wall 1.5 m ahead and (b) a point 20 m beyond the window (pixel positions from
``DE.SnPixel``): the interior is unfogged when the patch is on, so probe (a) differs between on and off by >= 0.05 (mean
absolute RGB, 0..1), while the water seen through the window stays fogged: probe (b) differs by <= 0.02. S7.M1 (the
postfix off) makes both captures equal: (a) < 0.05, red.

S7.N3.ratio: the wall luminance under an SE Interior Light and under the Seamoth cabin light at the same distance
(relative luminance of linearised sRGB, Rec. 709): within +-25 % (Wire ``LightCalibration``; the suggested
``LightIntensityScale`` = current x seamoth / se).

Library (called by name by tests/s7/analyze_s7.py through the ``s7_images`` shim): ``read_png``, ``probe_rgb``,
``mad``, ``fog_check``, ``luminance_srgb``, ``light_ratio``, ``suggest_scale``.

CLI:
 python -I tests/s7/s7_interior_check.py fog --on ON.png --off OFF.png --a X,Y --b X,Y [--radius 4] [--json]
 python -I tests/s7/s7_interior_check.py ratio --se SE.png --se-at X,Y --seamoth SM.png --seamoth-at X,Y [--scale 1.0]
 python -I tests/s7/s7_interior_check.py --selftest
Exit code 0 = PASS (or selftest passed), 1 = FAIL, 2 = usage or unreadable input.
"""

import argparse
import json
import os
import struct
import sys
import tempfile
import zlib

FOG_A_MIN = 0.05      # probe (a), the inner wall: on vs off differ by at least this (mean |RGB|, 0..1)
FOG_B_MAX = 0.02      # probe (b), 20 m beyond the window: on vs off differ by at most this
RATIO_TOL = 0.25      # S7.N3: |ratio - 1| <= 25 %
MIN_SCALE, MAX_SCALE = 0.05, 50.0
DEFAULT_RADIUS = 4    # a 9 x 9 patch, as the analyzer's fallback

# ----------------------------------------------------------------------------------------------------------------
# PNG (8-bit greyscale, RGB, grey+alpha or RGBA; non-interlaced: Unity's ScreenCapture writes RGB/RGBA)
# ----------------------------------------------------------------------------------------------------------------

_CHANNELS = {0: 1, 2: 3, 4: 2, 6: 4}

def read_png(path):
    """(width, height, channels, rows of bytes) of an 8-bit non-interlaced PNG."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG: %s" % path)
    pos, idat, w, h, ch = 8, [], 0, 0, 0
    while pos + 8 <= len(data):
        n = struct.unpack(">I", data[pos:pos + 4])[0]
        kind = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + n]
        pos += 12 + n
        if kind == b"IHDR":
            w, h, depth, ctype, _comp, _filt, inter = struct.unpack(">IIBBBBB", body)
            if depth != 8 or ctype not in _CHANNELS or inter != 0:
                raise ValueError("PNG depth %d colour type %d interlace %d not read" % (depth, ctype, inter))
            ch = _CHANNELS[ctype]
        elif kind == b"IDAT":
            idat.append(body)
        elif kind == b"IEND":
            break
    if w <= 0 or h <= 0:
        raise ValueError("PNG without IHDR: %s" % path)
    raw = zlib.decompress(b"".join(idat))
    stride = w * ch
    rows, prev, i = [], bytearray(stride), 0
    for _y in range(h):
        ft = raw[i]
        line = bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        if ft == 1:
            for x in range(ch, stride):
                line[x] = (line[x] + line[x - ch]) & 255
        elif ft == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 255
        elif ft == 3:
            for x in range(stride):
                a = line[x - ch] if x >= ch else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif ft == 4:
            for x in range(stride):
                a = line[x - ch] if x >= ch else 0
                b = prev[x]
                c = prev[x - ch] if x >= ch else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else (b if pb <= pc else c))) & 255
        elif ft != 0:
            raise ValueError("PNG filter %d unknown" % ft)
        rows.append(bytes(line))
        prev = line
    return w, h, ch, rows

def write_png(path, w, h, pixel, filters=(0,)):
    """A test PNG (RGB): pixel(x, y) -> (r, g, b); row y uses filters[y % len(filters)] (0 none, 1 sub, 2 up, 3 avg, 4 paeth)."""
    ch = 3
    raw_rows = [bytes(b for x in range(w) for b in pixel(x, y)) for y in range(h)]
    out, prev = [], bytes(w * ch)
    for y, line in enumerate(raw_rows):
        ft = filters[y % len(filters)]
        enc = bytearray(len(line))
        for x in range(len(line)):
            a = line[x - ch] if x >= ch else 0
            b = prev[x]
            c = prev[x - ch] if x >= ch else 0
            if ft == 0:
                p = 0
            elif ft == 1:
                p = a
            elif ft == 2:
                p = b
            elif ft == 3:
                p = (a + b) >> 1
            else:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                p = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
            enc[x] = (line[x] - p) & 255
        out.append(bytes([ft]) + bytes(enc))
        prev = line

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) +
                chunk(b"IDAT", zlib.compress(b"".join(out))) + chunk(b"IEND", b""))

def probe_rgb(path, x, y, radius=DEFAULT_RADIUS):
    """[r, g, b] in 0..1: the mean of the (2·radius+1)² patch around pixel (x, y) (y from the top, as DE.SnPixel), clipped
 to the image; None when the patch lies outside it."""
    w, h, ch, rows = read_png(path)
    xi, yi = int(round(x)), int(round(y))
    acc, n = [0.0, 0.0, 0.0], 0
    for yy in range(max(0, yi - radius), min(h, yi + radius + 1)):
        line = rows[yy]
        for xx in range(max(0, xi - radius), min(w, xi + radius + 1)):
            if ch >= 3:
                for k in range(3):
                    acc[k] += line[xx * ch + k]
            else:
                for k in range(3):
                    acc[k] += line[xx * ch]
            n += 1
    if n == 0:
        return None
    return [a / (255.0 * n) for a in acc]

def mad(a, b):
    """Mean absolute difference of two RGB triples (0..1)."""
    return sum(abs(a[i] - b[i]) for i in range(3)) / 3.0

def fog_check(a_on, a_off, b_on, b_off):
    """S7.N1.fog: (status, da, db): PASS when da >= 0.05 and db <= 0.02 (MISSING when a probe is None)."""
    if any(v is None for v in (a_on, a_off, b_on, b_off)):
        return "MISSING", None, None
    da, db = mad(a_on, a_off), mad(b_on, b_off)
    return ("PASS" if da >= FOG_A_MIN and db <= FOG_B_MAX else "FAIL"), da, db

def _linear(c):
    if c <= 0:
        return 0.0
    if c >= 1:
        return 1.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

def luminance_srgb(rgb):
    """Relative luminance (Rec. 709) of an sRGB triple 0..1 (the Wire LightCalibration twin)."""
    return 0.2126 * _linear(rgb[0]) + 0.7152 * _linear(rgb[1]) + 0.0722 * _linear(rgb[2])

def light_ratio(se_rgb, seamoth_rgb):
    """S7.N3.ratio: (status, ratio): the SE light's wall luminance over the Seamoth cabin light's, PASS within +-25 %."""
    if se_rgb is None or seamoth_rgb is None:
        return "MISSING", None
    ls, lm = luminance_srgb(se_rgb), luminance_srgb(seamoth_rgb)
    if lm <= 0:
        return "MISSING", None
    r = ls / lm
    return ("PASS" if abs(r - 1.0) <= RATIO_TOL else "FAIL"), r

def suggest_scale(current, se_lum, seamoth_lum):
    """The LightIntensityScale that equalises the two probes (Wire LightCalibration.SuggestScale)."""
    if not (se_lum > 0 and seamoth_lum > 0 and current > 0):
        return current if current > 0 else 1.0
    return max(MIN_SCALE, min(MAX_SCALE, current * seamoth_lum / se_lum))

# ----------------------------------------------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------------------------------------------

def _xy(text):
    try:
        x, y = text.split(",")
        return float(x), float(y)
    except ValueError:
        raise argparse.ArgumentTypeError("expected X,Y pixels, got %r" % text)

def _emit(result, as_json):
    if as_json:
        print(json.dumps(result, sort_keys=True))
    else:
        print("%s %s %s" % (result["id"], result["status"], result.get("measured", "")))

def cmd_fog(args):
    try:
        a_on = probe_rgb(args.on, args.a[0], args.a[1], args.radius)
        a_off = probe_rgb(args.off, args.a[0], args.a[1], args.radius)
        b_on = probe_rgb(args.on, args.b[0], args.b[1], args.radius)
        b_off = probe_rgb(args.off, args.b[0], args.b[1], args.radius)
    except (OSError, ValueError, zlib.error) as e:
        print("S7.N1.fog MISSING image unreadable: %s" % e)
        return 2
    status, da, db = fog_check(a_on, a_off, b_on, b_off)
    measured = "a %.3f, b %.3f" % (da, db) if da is not None else "probe outside the image"
    _emit({"id": "S7.N1.fog", "status": status, "measured": measured, "threshold": ">= %.2f / <= %.2f" % (FOG_A_MIN, FOG_B_MAX),
           "a": [a_on, a_off], "b": [b_on, b_off]}, args.json)
    return 0 if status == "PASS" else 1

def cmd_ratio(args):
    try:
        se = probe_rgb(args.se, args.se_at[0], args.se_at[1], args.radius)
        sm = probe_rgb(args.seamoth, args.seamoth_at[0], args.seamoth_at[1], args.radius)
    except (OSError, ValueError, zlib.error) as e:
        print("S7.N3.ratio MISSING image unreadable: %s" % e)
        return 2
    status, r = light_ratio(se, sm)
    sug = suggest_scale(args.scale, luminance_srgb(se), luminance_srgb(sm)) if r is not None else None
    _emit({"id": "S7.N3.ratio", "status": status, "measured": ("%.3f" % r) if r is not None else "n/a", "threshold": "1 +- %.2f" % RATIO_TOL,
           "se": se, "seamoth": sm, "scale": args.scale, "suggestedScale": sug}, args.json)
    return 0 if status == "PASS" else 1

def selftest():
    """Synthetic captures: a fogged scene (patch off) vs an unfogged interior (patch on) with the window region equal;
 the M1 case (both fogged) is red; every PNG filter decodes; the ratio and scale rules."""
    fails = []

    def check(cond, what):
        if not cond:
            fails.append(what)
    tmp = tempfile.mkdtemp(prefix="s7_interior_check_")
    try:
        w, h = 64, 48
        fog = (40, 90, 110)          # Subnautica's blue-green murk
        wall = (180, 170, 160)       # the dry wall, unfogged
 # probe (a) at (16, 24) on the wall, probe (b) at (48, 24) through the window
        def scene(unfogged):
            def px(x, y):
                if x >= 40:
                    return fog                    # the window: water beyond, fogged in both
                return wall if unfogged else tuple((a + b) // 2 for a, b in zip(wall, fog))
            return px
        on, off, m1 = (os.path.join(tmp, n) for n in ("on.png", "off.png", "m1.png"))
        write_png(on, w, h, scene(True), filters=(0, 1, 2, 3, 4))
        write_png(off, w, h, scene(False), filters=(4, 3, 2, 1, 0))
        write_png(m1, w, h, scene(False), filters=(2,))
        a = (16, 24)
        b = (52, 24)
        s, da, db = fog_check(probe_rgb(on, *a), probe_rgb(off, *a), probe_rgb(on, *b), probe_rgb(off, *b))
        check(s == "PASS", "fog pass: %s %.3f %.3f" % (s, da, db))
        s, da, db = fog_check(probe_rgb(m1, *a), probe_rgb(off, *a), probe_rgb(m1, *b), probe_rgb(off, *b))
        check(s == "FAIL" and da < FOG_A_MIN, "S7.M1 (no difference) is red: %s %.3f" % (s, da))
        # a fog that also changed the window (the shader spared everything): b over the limit
        def all_clear(x, y):
            return wall if x < 40 else (90, 140, 160)
        clr = os.path.join(tmp, "clear.png")
        write_png(clr, w, h, all_clear)
        s, da, db = fog_check(probe_rgb(clr, *a), probe_rgb(off, *a), probe_rgb(clr, *b), probe_rgb(off, *b))
        check(s == "FAIL" and db > FOG_B_MAX, "window unfogged too is red: %s %.3f" % (s, db))
        check(fog_check(None, [0, 0, 0], [0, 0, 0], [0, 0, 0])[0] == "MISSING", "missing probe")
        # decoding: every filter gives the same pixels
        grad = os.path.join(tmp, "grad.png")
        write_png(grad, 17, 9, lambda x, y: ((x * 13) & 255, (y * 29) & 255, (x * y * 7) & 255), filters=(0, 1, 2, 3, 4))
        _w, _h, ch, rows = read_png(grad)
        ok = all(rows[y][x * 3:x * 3 + 3] == bytes(((x * 13) & 255, (y * 29) & 255, (x * y * 7) & 255)) for y in range(9) for x in range(17))
        check(ok and ch == 3, "PNG filters 0-4 decode exactly")
        check(probe_rgb(grad, -100, -100, 2) is None, "a probe outside the image is None")
        p = probe_rgb(grad, 0, 0, 0)
        check(p == [0.0, 0.0, 0.0], "single pixel probe")
        # ratio and scale (the Wire LightCalibration twin)
        check(abs(luminance_srgb([0.5, 0.5, 0.5]) - 0.2140) < 1e-3, "sRGB 0.5 is 21.4 % linear")
        check(light_ratio([0.5, 0.5, 0.5], [0.5, 0.5, 0.5]) == ("PASS", 1.0), "equal probes pass")
        st, r = light_ratio([0.8, 0.8, 0.8], [0.5, 0.5, 0.5])
        check(st == "FAIL" and r > 1.25, "too bright is red (%.3f)" % r)
        check(abs(suggest_scale(1.0, 0.4, 0.2) - 0.5) < 1e-12, "half the scale")
        check(suggest_scale(1.0, 1e-9, 1.0) == MAX_SCALE, "clamped")
        check(suggest_scale(2.0, 0.0, 1.0) == 2.0, "a black probe changes nothing")
    finally:
        for n in os.listdir(tmp):
            try:
                os.remove(os.path.join(tmp, n))
            except OSError:
                pass
        try:
            os.rmdir(tmp)
        except OSError:
            pass
    for f in fails:
        print("SELFTEST FAIL: " + f)
    print("s7_interior_check selftest " + ("PASS" if not fails else "FAIL (%d)" % len(fails)))
    return 0 if not fails else 1

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--selftest", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    f = sub.add_parser("fog", help="S7.N1.fog from a capture pair (patch on / off)")
    f.add_argument("--on", required=True)
    f.add_argument("--off", required=True)
    f.add_argument("--a", type=_xy, required=True, help="probe (a), the inner wall: X,Y pixels from the top-left")
    f.add_argument("--b", type=_xy, required=True, help="probe (b), 20 m beyond the window: X,Y")
    f.add_argument("--radius", type=int, default=DEFAULT_RADIUS)
    f.add_argument("--json", action="store_true")
    r = sub.add_parser("ratio", help="S7.N3.ratio from two captures (SE light / Seamoth cabin light)")
    r.add_argument("--se", required=True)
    r.add_argument("--se-at", type=_xy, required=True)
    r.add_argument("--seamoth", required=True)
    r.add_argument("--seamoth-at", type=_xy, required=True)
    r.add_argument("--scale", type=float, default=1.0, help="the session's LightIntensityScale")
    r.add_argument("--radius", type=int, default=DEFAULT_RADIUS)
    r.add_argument("--json", action="store_true")
    args = ap.parse_args(argv)
    if args.selftest:
        return selftest()
    if args.cmd == "fog":
        return cmd_fog(args)
    if args.cmd == "ratio":
        return cmd_ratio(args)
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main())
