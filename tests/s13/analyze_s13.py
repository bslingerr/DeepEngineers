"""S13 oracle table (ROADMAP §14 "### S13 Character and animation v2"; docs/S13-BRIEF.md; CONTRACT §5.4 "S13 conventions").

 python -I tests/s13/analyze_s13.py <folder with se-*.jsonl / sn-*.jsonl> [--lenient] [--json]
 python -I tests/s13/analyze_s13.py --se-log <se-*.jsonl> --sn-log <sn-*.jsonl> [--lenient] [--json]
 python -I tests/s13/analyze_s13.py --selftest

py run`, S13 on by default; `launcher.py s13 --notes`); this table judges
its logs with the Python twin of the S13 rules below (never the plugins' own verdicts):

 S13.PIN the twin's canonical text hashes to the pinned value, and the C# source pins the same number
 S13.A1 point 1, hands: no frame with Subnautica's own diver view model drawn under fpbody=se (s13.char leakFrames, counted
 at onPreCull from the view model's enabled renderers, 0), no s7p.char row with snBodyLeak, over >= 5 first-person
 surface crossings (splashesFirstPerson) and >= 600 forced-off frames
 S13.A2 point 2, swim near a surface: >= 95 % of the frames submerged with ground contact keep a swim source (Retarget or
 Blend; >= 30 such frames)
 S13.A3 point 3, tool colour, judged where it is drawn: every Subnautica s13.tooln row has a received colour that
 is not SE's red key (hue 0 with saturation offset >= 0) and no slot left "pending" (a paintable, converted slot in bake
 mode not painted) 10 s after the part arrived; and every SE s13.tool row sends the entity's own colour
 S13.A3T point 3, equip: no frame drew a character child part before its first C2 row (s13.char toolShownNoRow 0)
 S13.A4N point 4, shadows: every s13.light row (the suit lamp casting) has the 1.0 m near plane; INFO the measured surface
 distance and the shadow a 0.1 m caster at the near plane throws on it (the "shadow length vs distance" row)
 S13.A4F point 4, falloff: the lamp's range and intensity factors are the twin's fit for the row's falloff (when fitOn)
 S13.A5 point 5, motion: in third-person s13.swim rows faster than 0.5 m/s, the sign of moveForward matches
 vForward and the sign of moveStrafe matches vRight (look frame) on >= 90 % of >= 5 judged axes
 S13.A5A point 5, animation: >= 90 % of the in-water samples agree (cos >= 0.7) between the animator's direction and the
 body-relative velocity, and the look-frame extension drove >= 95 % of the animated frames (>= 60 samples)
 S13.A6 point 6, held items: >= 95 % of the frames swimming with a held tool, gun or block use the Blend source (>= 30)
 S13.A7 point 7, ghost: >= 80 % of the s13.ghost rows with renderers draw the textured material; measured names the
 shader the render probe kept
 S13.A7R point 7, rotation: the drawn ghost's rotation is SE's (s7a.ghost poseErrDeg p95 <= 1 deg; INFO when absent)
 S13.SND INFO: the dive/surface crossings and the sounds played (WaterAmbience)
 S13.Mx.red a seeded failure turns exactly its rows red (--selftest)

"""
from __future__ import annotations

import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))

PINNED_HASH = 0xdbc2fc8fe4aa4ce7
PINNED_LINES, PINNED_BYTES = 43, 2468
CS_SOURCE = os.path.join(ROOT, "src", "DeepEngineers.Wire", "S13Conventions.cs")

MUTANT_ROWS = {"S13.M1": ("S13.A1",), "S13.M2": ("S13.A2",), "S13.M3": ("S13.A3",), "S13.M4": ("S13.A4N", "S13.A4F"),
               "S13.M5": ("S13.A5",), "S13.M6": ("S13.A6",), "S13.M7": ("S13.A7",), "S13.M8": ("S13.A5A",)}

# ------------------------------------------------------------------------------------------------------------- the twin

C1 = {"Offset": 0xC0, "Size": 8, "Version": 1}
C1_FIELDS = [("version", "0xC0", "1", "u8"), ("flags", "0xC1", "1", "u8"), ("vRightCm", "0xC2", "2", "i16"),
             ("vUpCm", "0xC4", "2", "i16"), ("vForwardCm", "0xC6", "2", "i16")]
C1_FLAGS = [("Valid", 1), ("BodyBasis", 2), ("Swimming", 4)]
LIGHT = [("NearPlaneCharacterSpotMm", 1000), ("NearPlaneBlockSpotMm", 500), ("UnityAttenuationK", 25),
         ("UnityFadeStartPermille", 800), ("FitSamples", 301), ("FitSpanPermille", 1500), ("FitKMinPermille", 1000),
         ("FitKMaxPermille", 4000), ("FitKStepPermille", 10), ("EnergySpanPermille", 1000)]
BLEND_ROOTS = ["SE_RigLThigh", "SE_RigRThigh"]
FORMAT_LINES = [
    "c1.frame look = GetHeadMatrix(includeY false, includeX true): body yaw, head pitch",
    "c1.velocity cm/s round half away from zero, clamp -32767..32767",
    "c1.idle version 1 flags 0 velocity 0 when no character",
    "swim.basis firstPerson camera; thirdPerson look frame",
    "anim.velocity underwater move_speed_xyz = look-frame velocity (ArmsController.GetRelativeVelocity)",
    "source.grounded grounded cancels the swim source only at the surface (eye >= 0)",
    "source.held swimming with a held tool, gun or block = Blend (legs Retarget, the rest SeBones)",
    "light.nearPlane characterSpot 1000 mm, other spots 500 mm, points unchanged",
    "light.fit characterSpot range x k, intensity x energy factor; other kinds unchanged",
    "tool.colour PartAdd colorMaskHSV = the tool entity's Render.ColorMaskHsv",
    "tool.row a character child part is hidden until its first C2 row",
    "texture.root a rooted .mwm resolves relative textures in its own content root first (MyMwmUtils.GetFullMwmContentPath)",
    "ghost.material Standard Fade, the slot's _BumpMap, tint green/red",
    "ghost.alpha 450 permille, 120 permille with the camera inside the bounds grown by 300 mm",
    "arms.splash the dive window never shows the diver view model under fpbody=se",
    "source.ladder a ladder (C1 loco state or flag Ladder) is never a swim source",
    "ghost.probe Standard Fade, else the glass clone, kept when its Blit over (0,0,0,0) is translucent (alpha < 0.9, glass also tinted); else the flat S7a material",
    "arms.leak counted at onPreCull: a view model renderer enabled and active under ForceOff",
    "sounds.dive WaterAmbience diveStart or diveStartSplash (-vUp > 5.5 m/s) when the eye goes under, PlayReachSurfaceSound when it comes out; dives 1 s apart",
]

def canonical_text() -> str:
    out = ["ext s13 char 2"]
    for k in ("Offset", "Size", "Version"):
        out.append("const C1 %s %d" % (k, C1[k]))
    for f in C1_FIELDS:
        out.append("field s13 %s %s %s %s" % f)
    for n, v in C1_FLAGS:
        out.append("const C1Flags %s %d" % (n, v))
    for n, v in LIGHT:
        out.append("const Light %s %d" % (n, v))
    for r in BLEND_ROOTS:
        out.append("blend root " + r)
    for f in sorted(FORMAT_LINES):   # ordinal (ASCII) order, as C#'s string.CompareOrdinal
        out.append("format " + f)
    return "".join(line + "\n" for line in out)

def fnv1a64(data: bytes) -> int:
    h = 0xcbf29ce484222325
    for b in data:
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def unity_att(x: float) -> float:
    """Unity's built-in attenuation at x = d / range (HYPOTHESIS model, the C# S13LightFit.Unity)."""
    if not (x >= 0):
        return 1.0 if x < 0 else 0.0
    v = x * x
    if v >= 1.0:
        return 0.0
    a = 1.0 / (1.0 + 25 * v)
    fade = 800 / 1000.0
    if v >= fade:
        a *= 1.0 - (v - fade) / (1.0 - fade)
    return a

def se_att(x: float, falloff: float) -> float:
    """SE's spot attenuation (LightingModel.hlsli GetAttenuation): (1 - x^4)^3 / (1 + falloff x^2)."""
    if not (x >= 0):
        return 1.0
    if x >= 1.0:
        return 0.0
    dq = x * x
    h = 1.0 - dq * dq
    return h * h * h / (1.0 + max(0.0, falloff) * dq)

def fit_error(k: float, falloff: float) -> float:
    n, span = 301, 1.5
    s = 0.0
    for j in range(n):
        x = span * j / (n - 1)
        d = unity_att(x / k) - se_att(x, falloff)
        s += d * d
    return s / n

def range_factor(falloff: float) -> float:
    if falloff != falloff or falloff < 0:
        falloff = 0.0
    best, best_e = 1000, float("inf")
    for km in range(1000, 4001, 10):
        e = fit_error(km / 1000.0, falloff)
        if e < best_e - 1e-15:
            best_e, best = e, km
    return best / 1000.0

def energy_factor(k: float) -> float:
    if not (k > 0):
        return 1.0
    n, span = 301, 1.0
    a = b = 0.0
    for j in range(n):
        x = span * j / (n - 1)
        a += unity_att(x)
        b += unity_att(x / k)
    return a / b if b > 0 else 1.0

def cs_pinned_hash():
    try:
        with open(CS_SOURCE, encoding="utf-8") as f:
            m = re.search(r"PinnedHash\s*=\s*0x([0-9a-fA-F]+)UL", f.read())
        return int(m.group(1), 16) if m else None
    except OSError:
        return None

# ------------------------------------------------------------------------------------------------------------- rows

BAD = ("FAIL", "MISSING")

def row(rows, cid, status, measured, threshold, what, required=True, **detail):
    r = {"id": cid, "status": status, "measured": measured, "threshold": threshold, "what": what, "required": required}
    if detail:
        r["detail"] = detail
    rows.append(r)
    return r

def verdict(rows, strict=True) -> bool:
    bad = BAD if strict else ("FAIL",)
    return not any(r.get("required", True) and r["status"] in bad for r in rows)

def table(rows) -> str:
    out = ["%-9s %-8s %-40s %-30s %s" % ("check", "status", "measured", "threshold", "what"), "-" * 150]
    for r in rows:
        out.append("%-9s %-8s %-40s %-30s %s" % (r["id"], r["status"] + ("" if r.get("required", True) else "*"),
                                                 str(r["measured"])[:40], str(r["threshold"])[:30], r["what"]))
    return "\n".join(out)

def read_jsonl(paths):
    """Every JSON line of the files as {ev,...fields} (Subnautica's rows nest their fields under "data")."""
    out = []
    for p in paths:
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.strip()
                    if not line.startswith("{"):
                        continue
                    try:
                        rec = json.loads(line)
                    except ValueError:
                        continue
                    ev = rec.get("ev") or rec.get("event")
                    if not isinstance(ev, str):
                        continue
                    d = dict(rec.get("data")) if isinstance(rec.get("data"), dict) else dict(rec)
                    d["_ev"] = ev
                    out.append(d)
        except OSError:
            continue
    return out

def by(evs, name):
    return [e for e in evs if e.get("_ev") == name]

TOOL_PENDING_GRACE_S = 10.0

def red_key(h, s) -> bool:
    """SE's shader key is red when the hue is 0 and the saturation offset >= 0 (+ ColorizeHSV 0.8): the twin of S13ToolSlot.Red."""
    try:
        h, s = float(h), float(s)
    except (TypeError, ValueError):
        return False
    return (abs(h) < 1e-4 or abs(h - 1.0) < 1e-4) and s >= 0.0

def sign_checks(swims):
    """Review 2: per third-person row faster than 0.5 m/s, each pressed axis (|move| >= 32 of 127) whose look-frame velocity
 is above 0.5 m/s must have the same sign. Returns (judged, agreeing)."""
    judged = agree = 0
    for r in swims:
        try:
            vf, vr, vu = float(r.get("vForward", 0)), float(r.get("vRight", 0)), float(r.get("vUp", 0))
            mf, ms = float(r.get("moveForward", 0)), float(r.get("moveStrafe", 0))
        except (TypeError, ValueError):
            continue
        if (vf * vf + vr * vr + vu * vu) ** 0.5 <= 0.5:
            continue
        for m, v in ((mf, vf), (ms, vr)):
            if abs(m) < 32 or abs(v) <= 0.5:
                continue
            judged += 1
            if (m > 0) == (v > 0):
                agree += 1
    return judged, agree

def evaluate_events(se, sn):
    rows = []
    # PIN
    text = canonical_text()
    h = fnv1a64(text.encode("ascii"))
    cs = cs_pinned_hash()
    ok = h == PINNED_HASH and text.count("\n") == PINNED_LINES and len(text.encode("ascii")) == PINNED_BYTES and (cs is None or cs == PINNED_HASH)
    row(rows, "S13.PIN", "PASS" if ok else "FAIL", "0x%016x (C# %s)" % (h, "0x%016x" % cs if cs is not None else "n/a"),
        "0x%016x" % PINNED_HASH, "the twin's canonical text = the pinned S13 conventions")

    chars = by(sn, "s13.char")
    last = chars[-1] if chars else None

    # A1 hands
    leaks7p = [r for r in by(sn, "s7p.char") if r.get("snBodyLeak") is True]
    if last is None:
        row(rows, "S13.A1", "FAIL" if leaks7p else "MISSING", "no s13.char row, s7p leaks %d" % len(leaks7p), "leak 0 over >= 600 frames",
            "point 1: Subnautica's hands never drawn under fpbody=se")
    else:
        lf, fo = int(last.get("leakFrames", 0)), int(last.get("forceOffFrames", 0))
        fpx = int(last.get("splashesFirstPerson", 0))
        st = "FAIL" if (lf > 0 or leaks7p) else ("PASS" if fo >= 600 and fpx >= 5 else "MISSING")
        row(rows, "S13.A1", st, "leak %d/%d frames, 1st-person crossings %d, s7p leaks %d" % (lf, fo, fpx, len(leaks7p)),
            "leak 0, >= 5 crossings", "point 1: Subnautica's hands never drawn under fpbody=se")

    # A2 swim held submerged
    if last is None or int(last.get("submergedGrounded", 0)) < 30:
        row(rows, "S13.A2", "MISSING", "%s submerged-grounded frames" % (last.get("submergedGrounded", 0) if last else "no"),
            ">= 95 % of >= 30", "point 2: the swim holds under water near the seabed or a hull")
    else:
        n, s = int(last["submergedGrounded"]), int(last.get("submergedGroundedSourceSwim", 0))
        row(rows, "S13.A2", "PASS" if s >= 0.95 * n else "FAIL", "%d/%d swim source" % (s, n), ">= 95 % of >= 30",
            "point 2: the swim holds under water near the seabed or a hull")

    # A3 tool colour
    tools = by(se, "s13.tool")
    snt = by(sn, "s13.tooln")
    se_bad = 0
    for t in tools:
        e, s = t.get("entityHsv") or [], t.get("sentHsv") or []
        if len(e) != 3 or len(s) != 3 or any(abs(float(a) - float(b)) > 1e-3 for a, b in zip(e, s)):
            se_bad += 1
    if not snt:
        row(rows, "S13.A3", "FAIL" if se_bad else "MISSING", "no s13.tooln row; SE %d/%d differ" % (se_bad, len(tools)),
            "not red, painted, SE = entity", "point 3: the held tool keeps SE's own colour (never red)")
    else:
        red = sum(1 for r in snt if red_key(r.get("h"), r.get("s")))
        late = 0
        for r in snt:
            if float(r.get("ageS", 0)) < TOOL_PENDING_GRACE_S:
                continue
            if any(isinstance(sl, dict) and sl.get("state") == "pending" for sl in (r.get("slots") or [])):
                late += 1
        ok = red == 0 and late == 0 and se_bad == 0
        row(rows, "S13.A3", "PASS" if ok else "FAIL", "red %d, unpainted %d of %d SN rows; SE %d/%d differ" % (red, late, len(snt), se_bad, len(tools)),
            "not red, painted, SE = entity", "point 3: the held tool keeps SE's own colour (never red)")
    # A3T tool row gate
    if last is None or int(last.get("toolNoRow", 0)) + int(last.get("toolWithRow", 0)) == 0:
        row(rows, "S13.A3T", "MISSING", "no tool frames", "0 shown without a row", "point 3: an equipped tool never drawn at the feet")
    else:
        sh = int(last.get("toolShownNoRow", 0))
        row(rows, "S13.A3T", "PASS" if sh == 0 else "FAIL", "%d shown before the row (%s waited)" % (sh, last.get("toolNoRow", 0)),
            "0 shown without a row", "point 3: an equipped tool never drawn at the feet")

    # A4 lights
    lights = [r for r in by(sn, "s13.light") if r.get("shadows") is not False]
    if not lights:
        row(rows, "S13.A4N", "MISSING", "no s13.light row", "near plane >= 1.0 m", "point 4: the lamp's own body never casts")
        row(rows, "S13.A4F", "MISSING", "no s13.light row", "the twin's fit", "point 4: SE's falloff shape")
    else:
        near_bad = [r for r in lights if float(r.get("nearPlane", 0)) < 0.999]
        surf = sorted(float(r["surfaceM"]) for r in lights if float(r.get("surfaceM", -1)) > 0)
        shad = sorted(float(r["shadowLen10cm"]) for r in lights if float(r.get("shadowLen10cm", -1)) > 0)
        med = surf[len(surf) // 2] if surf else None
        row(rows, "S13.A4N", "PASS" if not near_bad else "FAIL",
            "%d/%d rows < 1 m; surface p50 %s m, 10 cm -> %s m" % (len(near_bad), len(lights), "%.1f" % med if med else "-", "%.2f" % shad[-1] if shad else "-"),
            "near plane >= 1.0 m", "point 4: the lamp's own body never casts (shadow length vs distance in the detail)",
            detail_shadow=[{"surfaceM": r.get("surfaceM"), "shadowLen10cm": r.get("shadowLen10cm"), "nearPlane": r.get("nearPlane")} for r in lights[:20]])
        fit_bad = 0
        fit_rows = [r for r in lights if r.get("fitOn") is not False]
        for r in fit_rows:
            k = range_factor(float(r.get("falloff", 1.0)))
            ki = energy_factor(k)
            if abs(float(r.get("rangeFactor", 1)) - k) > 0.011 or abs(float(r.get("intensityFactor", 1)) - ki) > 0.01:
                fit_bad += 1
        if not fit_rows:
            row(rows, "S13.A4F", "MISSING", "lightFit off", "the twin's fit", "point 4: SE's falloff shape")
        else:
            row(rows, "S13.A4F", "PASS" if fit_bad == 0 else "FAIL", "%d/%d rows off the fit" % (fit_bad, len(fit_rows)),
                "the twin's fit", "point 4: SE's falloff shape (Unity range x k, intensity x energy)")

    # A5 basis
    swims = [r for r in by(se, "s13.swim") if r.get("firstPerson") is False]
    judged, agree = sign_checks(swims)
    cam = sum(1 for r in swims if r.get("basis") != "body")
    if judged < 5:
        row(rows, "S13.A5", "MISSING", "%d judged axes (%d third-person rows)" % (judged, len(swims)), ">= 90 % of >= 5 axes",
            "point 5: third person moves the way the keys say, along the body")
    else:
        row(rows, "S13.A5", "PASS" if agree >= 0.9 * judged else "FAIL", "%d/%d axes agree; %d rows not on the body basis" % (agree, judged, cam),
            ">= 90 % of >= 5 axes", "point 5: third person moves the way the keys say, along the body")
    # A5A animation
    if last is None or int(last.get("animAgreeSamples", 0)) < 60:
        row(rows, "S13.A5A", "MISSING", "%s samples" % (last.get("animAgreeSamples", 0) if last else "no"), ">= 90 % agree, ext >= 95 %",
            "point 5: the swim animation and the body's motion agree")
    else:
        n, b = int(last["animAgreeSamples"]), int(last.get("animAgreeBad", 0))
        fr, ext = int(last.get("animFrames", 0)), int(last.get("animFromExt", 0))
        ok = b <= 0.10 * n and fr > 0 and ext >= 0.95 * fr
        row(rows, "S13.A5A", "PASS" if ok else "FAIL", "%d/%d disagree, ext %d/%d" % (b, n, ext, fr), ">= 90 % agree, ext >= 95 %",
            "point 5: the swim animation and the body's motion agree")

    # A6 held items
    if last is None or int(last.get("heldSwim", 0)) < 30:
        row(rows, "S13.A6", "MISSING", "%s held-swim frames" % (last.get("heldSwim", 0) if last else "no"), "Blend >= 95 % of >= 30",
            "point 6: swim legs while holding a tool, gun or block")
    else:
        n, bl = int(last["heldSwim"]), int(last.get("heldSwimBlend", 0))
        row(rows, "S13.A6", "PASS" if bl >= 0.95 * n else "FAIL", "%d/%d Blend" % (bl, n), "Blend >= 95 % of >= 30",
            "point 6: swim legs while holding a tool, gun or block")

    # A7 ghost
    ghosts = [r for r in by(sn, "s13.ghost") if int(r.get("textured", 0)) + int(r.get("flat", 0)) > 0]
    if not ghosts:
        row(rows, "S13.A7", "MISSING", "no s13.ghost row", "textured >= 80 % of rows", "point 7: the real block's look, green/red")
    else:
        tex = [r for r in ghosts if int(r.get("textured", 0)) >= int(r.get("flat", 0)) and int(r.get("textured", 0)) > 0]
        shader = ghosts[-1].get("shader") or "?"
        source = ghosts[-1].get("source") or "?"
        row(rows, "S13.A7", "PASS" if len(tex) >= 0.8 * len(ghosts) else "FAIL", "%d/%d textured, %s (%s)" % (len(tex), len(ghosts), source, shader),
            "textured >= 80 % of rows", "point 7: the real block's look, green/red", probe=ghosts[-1].get("probe"))
    errs = sorted(float(r["poseErrDeg"]) for r in by(sn, "s7a.ghost") if isinstance(r.get("poseErrDeg"), (int, float)))
    if not errs:
        row(rows, "S13.A7R", "MISSING", "no s7a.ghost pose", "p95 <= 1 deg", "point 7: the ghost turns with SE's preview", required=False)
    else:
        p95 = errs[min(len(errs) - 1, int(0.95 * len(errs)))]
        row(rows, "S13.A7R", "PASS" if p95 <= 1.0 else "FAIL", "p95 %.3f deg (%d)" % (p95, len(errs)), "p95 <= 1 deg",
            "point 7: the ghost turns with SE's preview")
    # SND (info)
    if last is not None:
        row(rows, "S13.SND", "INFO", "%s crossings, %s sounds" % (last.get("splashes", 0), last.get("splashSounds", 0)), "-",
            "point 1: the dive/surface sounds (WaterAmbience)", required=False)
    return rows

def evaluate(folder=None, se_logs=None, sn_logs=None):
    se_logs = list(se_logs or [])
    sn_logs = list(sn_logs or [])
    if folder:
        se_logs += sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        sn_logs += sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))
    return evaluate_events(read_jsonl(se_logs), read_jsonl(sn_logs))

# ------------------------------------------------------------------------------------------------------------- self-test

def _clean():
    se = [{"_ev": "s13.tool", "entityHsv": [0, -1, 0], "sentHsv": [0, -1, 0]}]
    se += [{"_ev": "s13.swim", "firstPerson": False, "basis": "body", "bodyOn": True, "moveForward": 127 if k % 2 else -127, "moveStrafe": 0,
            "vForward": 3.0 if k % 2 else -2.5, "vRight": 0.1, "vUp": 0.0} for k in range(6)]
    se += [{"_ev": "s13.swim", "firstPerson": True, "basis": "camera", "bodyOn": True}]
    char = {"_ev": "s13.char", "frames": 5000, "forceOffFrames": 4000, "leakFrames": 0, "submergedGrounded": 120,
            "submergedGroundedSourceSwim": 120, "heldSwim": 90, "heldSwimBlend": 90, "toolNoRow": 3, "toolShownNoRow": 0,
            "toolWithRow": 400, "animFrames": 3000, "animFromExt": 3000, "animAgreeSamples": 900, "animAgreeBad": 10,
            "splashes": 8, "splashSounds": 8, "splashesFirstPerson": 6}
    k = range_factor(1.0)
    sn = [dict(char), {"_ev": "s7p.char", "snBodyLeak": False}]
    sn += [{"_ev": "s13.light", "shadows": True, "nearPlane": 1.0, "falloff": 1.0, "rangeFactor": k, "intensityFactor": energy_factor(k),
            "fitOn": True, "surfaceM": 6.0, "shadowLen10cm": 0.6} for _ in range(5)]
    sn += [{"_ev": "s13.ghost", "textured": 7, "flat": 0, "source": "standard", "shader": "Standard"} for _ in range(5)]
    sn += [{"_ev": "s13.tooln", "h": 0.0, "s": -1.0, "v": 0.0, "ageS": a, "slots": [{"state": "applied"}, {"state": "none"}]} for a in (0, 5, 15)]
    sn += [{"_ev": "s13.tooln", "h": 0.0, "s": -1.0, "v": 0.0, "ageS": 2, "slots": [{"state": "pending"}]}]   # young: not late
    sn += [{"_ev": "s7a.ghost", "poseErrDeg": 0.01} for _ in range(20)]
    return se, sn

def _mutate(name, se, sn):
    se = [dict(r) for r in se]
    sn = [dict(r) for r in sn]
    for r in sn:
        if r["_ev"] == "s13.char":
            if name == "S13.M1":
                r["leakFrames"] = 78           # the 1.3 s dive window at 60 fps
            elif name == "S13.M2":
                r["submergedGroundedSourceSwim"] = 0
            elif name == "S13.M6":
                r["heldSwimBlend"] = 0
            elif name == "S13.M8":
                r["animFromExt"] = 0
                r["animAgreeBad"] = 300        # a coasting turn: the input axes disagree with the motion
        if r["_ev"] == "s13.light" and name == "S13.M4":
            r["nearPlane"], r["rangeFactor"], r["intensityFactor"], r["shadowLen10cm"] = 0.2, 1.0, 1.0, 3.0
        if r["_ev"] == "s13.ghost" and name == "S13.M7":
            r["textured"], r["flat"] = 0, 7
    for r in sn:
        if r["_ev"] == "s13.tooln" and name == "S13.M3":
            r["s"] = 0.0                       # Subnautica received (0, 0, 0): the red key
    for r in se:
        if r["_ev"] == "s13.tool" and name == "S13.M3":
            r["sentHsv"] = [0, 0, 0]
        if r["_ev"] == "s13.swim" and name == "S13.M5" and r.get("firstPerson") is False:
            r["basis"] = "camera"
            r["vForward"] = -float(r["vForward"])   # the camera faces the body: the keys and the body's motion disagree
    return se, sn

def selftest() -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S13 analyzer self-test (tests/s13/analyze_s13.py --selftest)")
    text = canonical_text()
    check(fnv1a64(text.encode("ascii")) == PINNED_HASH, "twin hash 0x%016x = pinned" % fnv1a64(text.encode("ascii")))
    check(text.count("\n") == PINNED_LINES and len(text) == PINNED_BYTES, "%d lines, %d bytes" % (text.count("\n"), len(text)))
    cs = cs_pinned_hash()
    check(cs == PINNED_HASH, "the C# source pins the same hash (%s)" % ("0x%016x" % cs if cs is not None else "unreadable"))
    check(abs(range_factor(1.0) - 2.42) < 1e-9 and abs(range_factor(0.0) - 2.82) < 1e-9 and abs(range_factor(2.0) - 2.16) < 1e-9,
          "fit k(1) = 2.42, k(0) = 2.82, k(2) = 2.16 (Wire S13ConventionsTests)")
    check(abs(energy_factor(2.42) - 0.504) < 0.001 and abs(energy_factor(1.0) - 1.0) < 1e-12, "energy factor(2.42) = 0.504")
    check(fit_error(2.42, 1.0) * 4 < fit_error(1.0, 1.0), "the fit cuts the curve error at least 4x")
    se, sn = _clean()
    rows = evaluate_events(se, sn)
    st = {r["id"]: r["status"] for r in rows}
    check(all(s in ("PASS", "INFO") for s in st.values()), "clean session: every row PASS (%s)" % ", ".join("%s=%s" % kv for kv in st.items() if kv[1] != "PASS"))
    for name, red in sorted(MUTANT_ROWS.items()):
        se2, sn2 = _mutate(name, se, sn)
        st2 = {r["id"]: r["status"] for r in evaluate_events(se2, sn2)}
        reds = sorted(k for k, v in st2.items() if v == "FAIL")
        check(reds == sorted(red), "%s red exactly on %s (got %s)" % (name, ",".join(red), ",".join(reds) or "none"))
    # an empty session is MISSING, not PASS
    st3 = {r["id"]: r["status"] for r in evaluate_events([], [])}
    check(st3.get("S13.A1") == "MISSING" and st3.get("S13.A6") == "MISSING", "an empty session is MISSING")
    # the s7p leak row alone turns A1 red
    se4, sn4 = _clean()
    sn4.append({"_ev": "s7p.char", "snBodyLeak": True})
    check({r["id"]: r["status"] for r in evaluate_events(se4, sn4)}.get("S13.A1") == "FAIL", "an s7p.char snBodyLeak row turns A1 red")
    #  forced-off frames without first-person crossings are MISSING, not PASS
    se6, sn6 = _clean()
    for r in sn6:
        if r["_ev"] == "s13.char":
            r["splashesFirstPerson"] = 2
    check({r["id"]: r["status"] for r in evaluate_events(se6, sn6)}.get("S13.A1") == "MISSING", "A1 without 5 first-person crossings is MISSING")
    #  a slot still unpainted 10 s after the tool arrived, or a red key received, turns A3 red (SE's rows alone pass)
    se7, sn7 = _clean()
    sn7.append({"_ev": "s13.tooln", "h": 0.0, "s": -1.0, "v": 0.0, "ageS": 11, "slots": [{"state": "pending"}]})
    check({r["id"]: r["status"] for r in evaluate_events(se7, sn7)}.get("S13.A3") == "FAIL", "an unpainted tool slot after 10 s turns A3 red")
    se8, sn8 = _clean()
    sn8 = [r for r in sn8 if r["_ev"] != "s13.tooln"] + [{"_ev": "s13.tooln", "h": 0.0, "s": 0.2, "v": 0.0, "ageS": 1, "slots": []}]
    check({r["id"]: r["status"] for r in evaluate_events(se8, sn8)}.get("S13.A3") == "FAIL", "a red key received turns A3 red although SE's rows agree")
    check(red_key(0, 0) and red_key(1.0, 0.5) and not red_key(0, -1) and not red_key(0.3, 0.5), "red_key, the twin of S13ToolSlot.Red")
    #  slow rows are not judged
    se9 = [{"_ev": "s13.swim", "firstPerson": False, "basis": "body", "moveForward": 127, "moveStrafe": 0, "vForward": 0.3, "vRight": 0, "vUp": 0}] * 8
    check(sign_checks(se9) == (0, 0), "rows at <= 0.5 m/s are not judged")
    # the reader takes Subnautica's nested rows and SE's flat ones
    import tempfile
    d = tempfile.mkdtemp(prefix="s13-selftest-")
    try:
        with open(os.path.join(d, "sn-x.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"t": "x", "event": "s13.char", "ev": "s13.char", "data": {"leakFrames": 0, "forceOffFrames": 700, "splashesFirstPerson": 5}}) + "\n")
            f.write(json.dumps({"t": "x", "event": "s13.tooln", "ev": "s13.tooln", "data": {"h": 0, "s": -1, "v": 0, "ageS": 12, "slots": [{"state": "applied"}]}}) + "\n")
        with open(os.path.join(d, "se-x.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"t": "x", "ev": "s13.tool", "entityHsv": [0, -1, 0], "sentHsv": [0, -1, 0]}) + "\n")
        st5 = {r["id"]: r["status"] for r in evaluate(d)}
        check(st5.get("S13.A1") == "PASS" and st5.get("S13.A3") == "PASS", "folder reader (nested sn rows, flat se rows)")
    finally:
        for p in glob.glob(os.path.join(d, "*")):
            os.remove(p)
        os.rmdir(d)
    ok = all(results)
    print("S13 ANALYZE SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv):
    if argv and argv[0] == "--selftest":
        return selftest()
    folder, se_logs, sn_logs = None, [], []
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--se-log" and i + 1 < len(argv):
            se_logs.append(argv[i + 1]); i += 2; continue
        if a == "--sn-log" and i + 1 < len(argv):
            sn_logs.append(argv[i + 1]); i += 2; continue
        if not a.startswith("--") and folder is None:
            folder = a
        i += 1
    if folder is None and not se_logs and not sn_logs:
        print(__doc__)
        return 2
    rows = evaluate(folder, se_logs, sn_logs)
    ok = verdict(rows, strict="--lenient" not in argv)
    if "--json" in argv:
        print(json.dumps(rows, indent=1, default=str))
    else:
        print(table(rows))
    print("S13 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
