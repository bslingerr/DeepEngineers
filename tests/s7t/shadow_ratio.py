"""S7t T5 oracle, compare.py's 'shadow' mode (ROADMAP "### S7t Seamless transition" T5
really tell you are switching games, especially because of the shadows and the skybox"): the shadow contrast of the same
thing in an SE screenshot and in a Subnautica screenshot at the band (a hull face in its own shadow against a face in the
sun, e.g. a ship's side and its top), and whether the two games' shadows are equally deep. CONTRACT §3.4 (the look
match). Pillow when importable (PNG, JPEG), else colour_patch's stdlib PNG reader. Run with python -I.

 python -I tests/s7t/compare.py shadow SE.png SN.png --shadow hull=X,Y,W,H/X,Y,W,H --lit hull=X,Y,W,H/X,Y,W,H
 [--shadow deck=... --lit deck=...] [--tol 0.25] [--json]
 python -I tests/s7t/shadow_ratio.py --selftest

Rectangles as in the 'patch' mode: X,Y,W,H in pixels or four fractions 0..1 (with a decimal point); the part before '/'
is SE's, after it Subnautica's (one rectangle for both without '/'); a --shadow and a --lit with the same label make one
face pair. Per rectangle: mean sRGB, luma (Rec. 709 on the sRGB bytes, the ROADMAP's L) and the mean relative luminance
of the linear pixels (Y). Per image and pair: the shadow contrast C = Y_lit / Y_shadow and the shadow depth
1 − Y_shadow / Y_lit. Verdict per pair: PASS when SE's contrast lies within the tolerance of Subnautica's
(|ln(C_se / C_sn)| ≤ ln(1 + tol), default tol 0.25), FAIL otherwise (the message says which game's shadows are darker),
MISSING when a shadowed region is too dark to measure (Y below --floor, default 0.002: the ratio is noise there) or a
lit region is not brighter than its shadow. Exit 0 when every pair passes, 1 otherwise, 2 on bad input.
"""
import argparse
import json
import math
import os
import sys

TOL_DEFAULT = 0.25
FLOOR_DEFAULT = 0.002

def _cp():
    here = os.path.dirname(os.path.abspath(__file__))
    if here not in sys.path:
        sys.path.insert(0, here)  # python -I does not add the script's folder
    import colour_patch
    return colour_patch

_LUT = [((c / 255.0) / 12.92 if c / 255.0 <= 0.04045 else (((c / 255.0) + 0.055) / 1.055) ** 2.4) for c in range(256)]

def region(img, rect):
    """Mean sRGB, luma of the mean bytes, and the mean linear relative luminance of the pixels in the rectangle."""
    cp = _cp()
    x0, y0, x1, y1 = cp.pixel_rect(img, rect)
    sr = sg = sb = 0
    sy = 0.0
    n = 0
    lut = _LUT
    for yy in range(y0, y1):
        o = (yy * img.width + x0) * 3
        row = img.rgb[o:o + (x1 - x0) * 3]
        rs, gs, bs = row[0::3], row[1::3], row[2::3]
        sr += sum(rs)
        sg += sum(gs)
        sb += sum(bs)
        for r, g, b in zip(rs, gs, bs):
            sy += 0.2126 * lut[r] + 0.7152 * lut[g] + 0.0722 * lut[b]
        n += x1 - x0
    r, g, b = sr / float(n), sg / float(n), sb / float(n)
    return {
        "rect": [x0, y0, x1 - x0, y1 - y0], "pixels": n,
        "rgb": [round(r, 1), round(g, 1), round(b, 1)],
        "luma": round(0.2126 * r + 0.7152 * g + 0.0722 * b, 1),
        "Y": sy / n,
    }

def contrast(shadow, lit, floor=FLOOR_DEFAULT):
    """(C, depth, why): C = Y_lit / Y_shadow; why is None when measurable."""
    ys, yl = shadow["Y"], lit["Y"]
    if ys < floor:
        return None, None, "shadow region too dark to measure (Y %.5f < %.5f)" % (ys, floor)
    if yl <= ys:
        return None, None, "the 'lit' region is not brighter than the shadow (Y %.5f <= %.5f)" % (yl, ys)
    return yl / ys, 1.0 - ys / yl, None

def judge_pair(se, sn, tol=TOL_DEFAULT):
    """se/sn: (C, depth, why). Returns (verdict, message)."""
    (cse, dse, wse), (csn, dsn, wsn) = se, sn
    if wse or wsn:
        parts = []
        if wse:
            parts.append("SE: " + wse)
        if wsn:
            parts.append("Subnautica: " + wsn)
        return "MISSING", "; ".join(parts)
    q = math.log(cse / csn)
    ok = abs(q) <= math.log(1.0 + tol)
    darker = "SE's shadows are deeper" if q > 0 else "Subnautica's shadows are deeper"
    msg = "contrast SE %.2f vs Subnautica %.2f (depth %.0f %% vs %.0f %%; ratio %.2f, tolerance x%.2f)%s" % (
        cse, csn, 100 * dse, 100 * dsn, cse / csn, 1.0 + tol, "" if ok else ": " + darker)
    return ("PASS" if ok else "FAIL"), msg

def parse_labelled(items, what):
    cp = _cp()
    out = {}
    for text in items or []:
        label, se, sn = cp.parse_patch(text)
        if label in out:
            raise ValueError("--%s '%s' given twice" % (what, label))
        out[label] = (se, sn)
    return out

def run(se_img, sn_img, shadows, lits, tol=TOL_DEFAULT, floor=FLOOR_DEFAULT):
    if set(shadows) != set(lits):
        raise ValueError("every --shadow needs a --lit with the same label (shadow %s, lit %s)" % (sorted(shadows), sorted(lits)))
    rows = []
    for label in sorted(shadows):
        s_se, s_sn = shadows[label]
        l_se, l_sn = lits[label]
        m = {"label": label,
             "se": {"shadow": region(se_img, s_se), "lit": region(se_img, l_se)},
             "sn": {"shadow": region(sn_img, s_sn), "lit": region(sn_img, l_sn)}}
        cse = contrast(m["se"]["shadow"], m["se"]["lit"], floor)
        csn = contrast(m["sn"]["shadow"], m["sn"]["lit"], floor)
        m["se"]["contrast"], m["se"]["depth"] = cse[0], cse[1]
        m["sn"]["contrast"], m["sn"]["depth"] = csn[0], csn[1]
        m["verdict"], m["message"] = judge_pair(cse, csn, tol)
        rows.append(m)
    return rows

def describe(rows):
    lines = []
    for m in rows:
        lines.append("CHECK S7t.T5.shadow.%s %s %s; SE shadow L%.0f lit L%.0f, Subnautica shadow L%.0f lit L%.0f" % (
            m["label"], m["verdict"], m["message"], m["se"]["shadow"]["luma"], m["se"]["lit"]["luma"],
            m["sn"]["shadow"]["luma"], m["sn"]["lit"]["luma"]))
    return "\n".join(lines)

# ----------------------------------------------------------------------------------------------------------- self-test

def _scene(w, h, lit_rgb, shadow_rgb, sky=(123, 176, 234)):
    """A synthetic screenshot: sky on top, a lit face (left) and a shadowed face (right) below."""
    cp = _cp()
    img = cp.Image(w, h, bytearray(w * h * 3), "synthetic")
    for y in range(h):
        for x in range(w):
            if y < h // 2:
                c = sky
            elif x < w // 2:
                c = lit_rgb
            else:
                c = shadow_rgb
            o = (y * w + x) * 3
            img.rgb[o], img.rgb[o + 1], img.rgb[o + 2] = c
    return img

def selftest():
    cp = _cp()
    failures = []

    def expect(cond, what):
        print(("ok   " if cond else "FAIL ") + what)
        if not cond:
            failures.append(what)

    shadows = parse_labelled(["hull=0.6,0.6,0.3,0.3"], "shadow")
    lits = parse_labelled(["hull=0.1,0.6,0.3,0.3"], "lit")
    # SE's hard shadow (sun-lit 200 grey, shadow 40) against Subnautica's soft one (200 / 120): FAIL, SE deeper
    se = _scene(64, 48, (200, 200, 200), (40, 40, 40))
    sn = _scene(80, 60, (200, 200, 200), (120, 120, 120))
    rows = run(se, sn, shadows, lits)
    expect(rows[0]["verdict"] == "FAIL" and "SE's shadows are deeper" in rows[0]["message"], "hard SE shadow vs soft Subnautica: FAIL (%s)" % rows[0]["message"])
    expect(abs(rows[0]["se"]["contrast"] - _LUT[200] / _LUT[40]) < 1e-9, "contrast = linear Y ratio")
    # the look match softened SE's shadow: 200 / 125 against 200 / 120 (contrast within 25 %): PASS
    se2 = _scene(64, 48, (200, 200, 200), (125, 125, 125))
    rows = run(se2, sn, shadows, lits)
    expect(rows[0]["verdict"] == "PASS", "matched shadows: PASS (%s)" % rows[0]["message"])
    # colour does not matter, luminance does: a blue-tinted shadow with the same Y passes too
    se3 = _scene(64, 48, (200, 200, 200), (110, 122, 150))
    rows = run(se3, sn, shadows, lits)
    expect(rows[0]["verdict"] == "PASS", "a tinted shadow of the same luminance: PASS (%s)" % rows[0]["message"])
    # Subnautica's shadows deeper than SE's: FAIL with the other message
    rows = run(sn, se, shadows, lits)
    expect(rows[0]["verdict"] == "FAIL" and "Subnautica's shadows are deeper" in rows[0]["message"], "the reverse names Subnautica")
    # a black shadow cannot be measured: MISSING, never PASS
    rows = run(_scene(64, 48, (200, 200, 200), (0, 0, 0)), sn, shadows, lits)
    expect(rows[0]["verdict"] == "MISSING", "black shadow region: MISSING")
    # regions swapped by mistake (the 'lit' darker than the 'shadow'): MISSING
    rows = run(se, sn, lits, shadows)
    expect(rows[0]["verdict"] == "MISSING", "lit and shadow swapped: MISSING")
    # the tolerance: 40 % apart fails at 0.25 and passes at 0.5
    a = _scene(32, 32, (200, 200, 200), (100, 100, 100))
    b = _scene(32, 32, (200, 200, 200), (125, 125, 125))
    expect(run(a, b, shadows, lits, tol=0.25)[0]["verdict"] == "FAIL", "tolerance 0.25")
    expect(run(a, b, shadows, lits, tol=0.7)[0]["verdict"] == "PASS", "tolerance 0.7 (the pair is 1.61 apart)")
    # labels must pair up
    try:
        run(se, sn, shadows, parse_labelled(["deck=0,0,1,1"], "lit"))
        expect(False, "unpaired labels refused")
    except ValueError:
        expect(True, "unpaired labels refused")
    # pixel rectangles and separate SE / Subnautica rectangles; PNG round trip through the stdlib reader
    data = cp.encode_png(se)
    img = cp.decode_png(data, "roundtrip")
    m = region(img, cp.parse_rect("40,30,20,10"))
    expect(m["rgb"] == [40.0, 40.0, 40.0] and m["pixels"] == 200, "pixel rectangle on a decoded PNG")
    pair = parse_labelled(["hull=40,30,20,10/0.7,0.7,0.2,0.2"], "shadow")
    expect(pair["hull"][0][1] is False and pair["hull"][1][1] is True, "SE pixels / Subnautica fractions")
    print("SELFTEST %s (%d failures)" % ("PASS" if not failures else "FAIL", len(failures)))
    return 0 if not failures else 1

def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv and argv[0] == "--selftest":
        return selftest()
    ap = argparse.ArgumentParser(prog="compare.py shadow", description="S7t T5: shadow contrast (shadowed vs lit face) SE vs Subnautica")
    ap.add_argument("se")
    ap.add_argument("sn")
    ap.add_argument("--shadow", action="append", required=True, help="label=SE_RECT/SN_RECT of a face in shadow")
    ap.add_argument("--lit", action="append", required=True, help="label=SE_RECT/SN_RECT of a sun-lit face (same label)")
    ap.add_argument("--tol", type=float, default=TOL_DEFAULT, help="relative tolerance on the contrast ratio (default 0.25)")
    ap.add_argument("--floor", type=float, default=FLOOR_DEFAULT, help="minimum shadow luminance Y (default 0.002)")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    cp = _cp()
    try:
        shadows = parse_labelled(a.shadow, "shadow")
        lits = parse_labelled(a.lit, "lit")
        rows = run(cp.load_image(a.se), cp.load_image(a.sn), shadows, lits, a.tol, a.floor)
    except (ValueError, OSError) as e:
        print("compare.py shadow: %s" % e)
        return 2
    print(json.dumps(rows, indent=1) if a.json else describe(rows))
    return 0 if all(m["verdict"] == "PASS" for m in rows) else 1

if __name__ == "__main__":
    sys.exit(main())
