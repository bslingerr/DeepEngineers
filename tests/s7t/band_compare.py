"""S7t T2 oracle, compare.py's 'bands' mode (ROADMAP "### S7t Seamless transition" T2, and T5's sky/water rows): the mean

pair: SE at ~+500 m, Subnautica at ~+380 m, same heading and time), judged within a tolerance
Images are read by colour_patch.py's loader (Pillow when importable, else its stdlib PNG reader). Run with python -I.

 python -I tests/s7t/compare.py bands SE.png SN.png [--horizon-se F] [--horizon-sn F] [--band NAME=RECT[/RECT]]...
 [--tol 24] [--ltol 16] [--json]
 python -I tests/s7t/compare.py bands SN.png --targets sn # one screenshot against the measured table (se|sn)
 python -I tests/s7t/band_compare.py --selftest

BANDS (per image; the horizon is found per image, so SE's curved horizon and Subnautica's flat one line up):
 sky_top y 0.02..0.10 of the height, x 0.30..0.70
 sky_horizon y top-0.03..top, x 0.55..0.
 far_water y bottom..bottom+0.06, x 0.55..0.80
 near_water y 0.62..0.74, x 0.58..0.80 (above the HUD)
The horizon: the steepest drop of the mean luma (Rec. 709 on the sRGB bytes, the ROADMAP's L) over the columns
0.55..0.80 with a 4 %-of-the-height window, searched in rows 0.08..0.60; top = the window's upper edge (the last sky),
bottom = its lower edge (the first sea); printed with the result. --horizon-se / --horizon-sn F (a fraction of the
height: sky above F, sea below) set top = bottom = F. --band NAME=RECT[/RECT] adds or replaces a band with colour_patch.py's rectangles (X,Y,W,H in
pixels, or four fractions; before '/' the first image's, after it the second's).
VERDICT per band: PASS when every channel differs by at most --tol (default 24) and the luma by at most --ltol (default
16); FAIL otherwise. Exit 0 when every band passes, 1 otherwise, 2 on bad input.

"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)  # python -I does not add the script's folder

import colour_patch as cp  # noqa: E402 (the shared image loader and rectangles; S7t-colour-mask's module)

TOL_DEFAULT = 24.0
LTOL_DEFAULT = 16.0
BAND_ORDER = ["sky_top", "sky_horizon", "far_water", "near_water"]
PROFILE_X = (0.55, 0.80)
WINDOW = 0.04
SEARCH = (0.08, 0.60)

# Python twin of the one source
# src/DeepEngineers.Wire/S7tBandTargets.cs (Wire.Tests S7tBandTargetsTests reads this table: keep both equal).
TARGETS = {
    "se": {"sky_top": (135, 221, 235), "sky_horizon": (153, 234, 246), "far_water": (38, 72, 96), "near_water": (27, 68, 104)},
    "sn": {"sky_top": (123, 176, 234), "sky_horizon": (197, 241, 255), "far_water": (129, 155, 187), "near_water": (65, 97, 122)},
}

def luma(r, g, b):
    return 0.2126 * r + 0.7152 * g + 0.0722 * b

def row_profile(img, x0f=PROFILE_X[0], x1f=PROFILE_X[1]):
    """Mean luma per row over the columns x0f..x1f (fractions)."""
    x0, x1 = int(img.width * x0f), max(int(img.width * x1f), int(img.width * x0f) + 1)
    n = float(x1 - x0)
    prof = []
    for y in range(img.height):
        o = (y * img.width + x0) * 3
        row = img.rgb[o:o + (x1 - x0) * 3]
        prof.append(luma(sum(row[0::3]) / n, sum(row[1::3]) / n, sum(row[2::3]) / n))
    return prof

def find_horizon(img):
    """(top, bottom, drop): the steepest drop of the mean luma (sky above, sea below) as the window's upper and lower
 edges (fractions of the height) and the luma drop across it."""
    prof = row_profile(img)
    h = img.height
    d = max(1, int(round(h * WINDOW)))
    pre = [0.0]
    for v in prof:
        pre.append(pre[-1] + v)

    def mean(a, b):
        a, b = max(0, a), min(h, b)
        return (pre[b] - pre[a]) / float(max(1, b - a))

    best, best_y = None, int(h * 0.3)
    for y in range(max(d, int(h * SEARCH[0])), min(h - d, int(h * SEARCH[1]))):
        drop = mean(y - d, y) - mean(y, y + d)
        if best is None or drop > best:
            best, best_y = drop, y
    return (best_y - d) / float(h), (best_y + d) / float(h), (best if best is not None else 0.0)

def default_bands(top, bottom):
    """The four bands as fraction rectangles ((x, y, w, h), True) for a horizon between top (sky) and bottom (sea)."""
    def r(x0, y0, x1, y1):
        y0, y1 = max(0.0, min(1.0, y0)), max(0.0, min(1.0, y1))
        if y1 - y0 < 0.002:
            y1 = min(1.0, y0 + 0.002)
        return (x0, y0, x1 - x0, y1 - y0), True
    return {
        "sky_top": r(0.30, 0.02, 0.70, 0.10),
        "sky_horizon": r(0.55, top - 0.03, 0.80, top),
        "far_water": r(0.55, bottom, 0.80, bottom + 0.06),
        "near_water": r(0.58, 0.62, 0.80, 0.74),
    }

def measure_bands(img, hz, extra):
    """{band: measure dict} with the defaults for the horizon hz = (top, bottom) and the user's --band rectangles."""
    bands = default_bands(hz[0], hz[1])
    bands.update(extra)
    out = {}
    for name, rect in bands.items():
        m = cp.measure(img, rect)
        out[name] = {"rect": m["rect"], "rgb": m["rgb"], "luma": m["luma"]}
    return out

def judge(a_rgb, b_rgb, tol, ltol):
    d = [abs(a_rgb[i] - b_rgb[i]) for i in range(3)]
    dl = abs(luma(*a_rgb) - luma(*b_rgb))
    ok = max(d) <= tol and dl <= ltol
    return ("PASS" if ok else "FAIL"), round(max(d), 1), round(dl, 1)

def parse_band(text):
    """'name=RECT[/RECT]' -> (name, rect_first, rect_second)."""
    if "=" not in text:
        raise ValueError("--band '%s': NAME=X,Y,W,H[/X,Y,W,H]" % text)
    label, a, b = cp.parse_patch(text)
    return label, a, b

def _horizon(img, given):
    if given is None:
        return find_horizon(img)
    return given, given, None

def run_pair(img_a, img_b, hz_a=None, hz_b=None, bands=(), tol=TOL_DEFAULT, ltol=LTOL_DEFAULT):
    found_a = _horizon(img_a, hz_a)
    found_b = _horizon(img_b, hz_b)
    ea = dict((n, a) for n, a, _b in bands)
    eb = dict((n, b) for n, _a, b in bands)
    ma = measure_bands(img_a, found_a, ea)
    mb = measure_bands(img_b, found_b, eb)
    rows = []
    for name in _order(ma):
        v, dmax, dl = judge(ma[name]["rgb"], mb[name]["rgb"], tol, ltol)
        rows.append({"band": name, "a": ma[name], "b": mb[name], "dMax": dmax, "dL": dl, "verdict": v})
    return {"horizonA": [round(found_a[0], 4), round(found_a[1], 4)], "horizonB": [round(found_b[0], 4), round(found_b[1], 4)], "rows": rows}

def run_targets(img, side, hz=None, bands=(), tol=TOL_DEFAULT, ltol=LTOL_DEFAULT):
    found = _horizon(img, hz)
    m = measure_bands(img, found, dict((n, a) for n, a, _b in bands))
    rows = []
    for name in _order(m):
        t = TARGETS[side].get(name)
        if t is None:
            rows.append({"band": name, "a": m[name], "b": None, "dMax": None, "dL": None, "verdict": "INFO"})
            continue
        tgt = {"rect": None, "rgb": [float(c) for c in t], "luma": round(luma(*t), 1)}
        v, dmax, dl = judge(m[name]["rgb"], tgt["rgb"], tol, ltol)
        rows.append({"band": name, "a": m[name], "b": tgt, "dMax": dmax, "dL": dl, "verdict": v})
    return {"horizonA": [round(found[0], 4), round(found[1], 4)], "horizonB": None, "rows": rows}

def _order(m):
    return [n for n in BAND_ORDER if n in m] + sorted(n for n in m if n not in BAND_ORDER)

def describe(res, label_a, label_b):
    def hz(h):
        return "(table)" if h is None else "sky to %.3f, sea from %.3f" % (h[0], h[1])
    out = ["horizon: %s %s; %s %s" % (label_a, hz(res["horizonA"]), label_b, hz(res["horizonB"]))]
    out.append("%-12s %-22s %-6s %-22s %-6s %6s %6s  %s" % ("band", label_a + " rgb", "L", label_b + " rgb", "L", "dMax", "dL", "verdict"))
    for r in res["rows"]:
        a, b = r["a"], r["b"]
        out.append("%-12s %-22s %-6s %-22s %-6s %6s %6s  %s" % (
            r["band"], tuple(int(round(c)) for c in a["rgb"]), int(round(a["luma"])),
            tuple(int(round(c)) for c in b["rgb"]) if b else "-", int(round(b["luma"])) if b else "-",
            r["dMax"] if r["dMax"] is not None else "-", r["dL"] if r["dL"] is not None else "-", r["verdict"]))
    return "\n".join(out)

# ----------------------------------------------------------------------------------------------------------- self-test

def _scene(w, h, hz, sky_top, sky_hz, water, near):
    """A synthetic screenshot: a sky gradient down to a bright horizon row at hz, the sea below (far, then near)."""
    data = bytearray(w * h * 3)
    yh = int(hz * h)
    for y in range(h):
        if y < yh:
            t = y / float(max(1, yh - 1))
            c = tuple(int(round(sky_top[i] + (sky_hz[i] - sky_top[i]) * t)) for i in range(3))
        elif y < int(0.55 * h):
            c = water
        else:
            c = near
        row = bytes(c) * w
        data[y * w * 3:(y + 1) * w * 3] = row
    return cp.Image(w, h, data, "synthetic")

def selftest():
    fails = []

    def expect(cond, what):
        if not cond:
            fails.append(what)

    se = _scene(320, 200, 0.21, (135, 221, 235), (153, 234, 246), (38, 72, 96), (27, 68, 104))
    sn = _scene(320, 200, 0.26, (123, 176, 234), (197, 241, 255), (129, 155, 187), (65, 97, 122))
    top, bottom, drop = find_horizon(se)
    expect(top <= 0.21 <= bottom and bottom - top <= 0.09, "SE horizon window holds 0.21 (got %.3f..%.3f)" % (top, bottom))
    expect(drop > 50, "a clear sky-to-sea drop")
    top2, bottom2, _ = find_horizon(sn)
    expect(top2 <= 0.26 <= bottom2, "SN horizon window holds 0.26 (got %.3f..%.3f)" % (top2, bottom2))

    same = run_pair(se, se)
    expect(all(r["verdict"] == "PASS" for r in same["rows"]), "a screenshot against itself passes")
    expect([r["band"] for r in same["rows"]] == BAND_ORDER, "the four bands in order")
    pair = run_pair(se, sn)
    v = dict((r["band"], r["verdict"]) for r in pair["rows"])
    expect(v["far_water"] == "FAIL", "SE's dark far water vs Subnautica's hazy far water fails (the measured gap)")
    expect(v["sky_top"] == "FAIL", "pale cyan vs deep blue fails")
    far = [r for r in pair["rows"] if r["band"] == "far_water"][0]
    expect(far["a"]["rgb"] == [38.0, 72.0, 96.0] and far["b"]["rgb"] == [129.0, 155.0, 187.0], "band means exact on flat colours")

    t = run_targets(sn, "sn")
    tv = dict((r["band"], r["verdict"]) for r in t["rows"])
    expect(tv["far_water"] == "PASS" and tv["near_water"] == "PASS" and tv["sky_top"] == "PASS", "the SN scene matches the SN table: %s" % tv)
    t2 = run_targets(sn, "se")
    expect(any(r["verdict"] == "FAIL" for r in t2["rows"]), "the SN scene does not match the SE table")

    near = _scene(320, 200, 0.21, (135, 221, 235), (153, 234, 246), (50, 84, 108), (27, 68, 104))
    nv = dict((r["band"], r["verdict"]) for r in run_pair(se, near)["rows"])
    expect(nv["far_water"] == "PASS", "a 12-level difference is within the default tolerance")
    nv = dict((r["band"], r["verdict"]) for r in run_pair(se, near, tol=8)["rows"])
    expect(nv["far_water"] == "FAIL", "and outside --tol 8")

    extra = [parse_band("hull=0.10,0.80,0.10,0.10/0.10,0.80,0.10,0.10")]
    rows = run_pair(se, sn, bands=extra)["rows"]
    expect(rows[-1]["band"] == "hull" and rows[-1]["a"]["rgb"] == [27.0, 68.0, 104.0], "a --band is measured after the defaults")
    fixed = run_pair(se, sn, hz_a=0.30, hz_b=0.30)
    expect(fixed["horizonA"] == [0.30, 0.30], "--horizon overrides the search")

    png = cp.encode_png(se)
    back = cp.decode_png(png, "roundtrip")
    expect(back.width == se.width and back.rgb == se.rgb, "stdlib PNG round trip")
    expect("dMax" in describe(pair, "SE", "SN"), "the table prints")
    try:
        parse_band("nolabel")
        fails.append("a band without NAME= is refused")
    except ValueError:
        pass

    for f in fails:
        print("SELFTEST FAIL " + f)
    print("band_compare selftest " + ("PASS" if not fails else "FAIL (%d)" % len(fails)))
    return 0 if not fails else 1

# ----------------------------------------------------------------------------------------------------------- main

def main(argv=None):
    ap = argparse.ArgumentParser(prog="compare.py bands", description="S7t T2: sky / horizon / water bands, SE vs Subnautica at the band")
    ap.add_argument("a", nargs="?", help="SE screenshot (or the one screenshot with --targets)")
    ap.add_argument("b", nargs="?", help="Subnautica screenshot")
    ap.add_argument("--targets", choices=sorted(TARGETS), help="compare ONE screenshot with the measured table of that side")
    ap.add_argument("--horizon-se", type=float, default=None, help="horizon row of the first image (fraction of the height)")
    ap.add_argument("--horizon-sn", type=float, default=None, help="horizon row of the second image (fraction of the height)")
    ap.add_argument("--band", action="append", default=[], help="NAME=X,Y,W,H[/X,Y,W,H] (pixels or fractions; first/second image)")
    ap.add_argument("--tol", type=float, default=TOL_DEFAULT, help="largest channel difference (default 24)")
    ap.add_argument("--ltol", type=float, default=LTOL_DEFAULT, help="largest luma difference (default 16)")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.a or (not a.b and not a.targets):
        ap.print_usage()
        print("need SE.png SN.png, or one screenshot with --targets se|sn")
        return 2
    for hzv in (a.horizon_se, a.horizon_sn):
        if hzv is not None and not 0.0 < hzv < 1.0:
            print("compare.py bands: --horizon-* is a fraction of the height (0..1)")
            return 2
    try:
        bands = [parse_band(t) for t in a.band]
        img_a = cp.load_image(a.a)
        if a.targets:
            res = run_targets(img_a, a.targets, a.horizon_se, bands, a.tol, a.ltol)
            la, lb = os.path.basename(a.a), "table:" + a.targets
        else:
            img_b = cp.load_image(a.b)
            res = run_pair(img_a, img_b, a.horizon_se, a.horizon_sn, bands, a.tol, a.ltol)
            la, lb = os.path.basename(a.a), os.path.basename(a.b)
    except (ValueError, OSError) as e:
        print("compare.py bands: " + str(e))
        return 2
    if a.json:
        print(json.dumps({"mode": "bands", "a": os.path.abspath(a.a), "b": os.path.abspath(a.b) if a.b else None,
                          "targets": a.targets, "tol": a.tol, "ltol": a.ltol, "result": res}, indent=1))
    else:
        print(describe(res, la, lb))
    return 0 if all(r["verdict"] in ("PASS", "INFO") for r in res["rows"]) else 1

if __name__ == "__main__":
    sys.exit(main())
