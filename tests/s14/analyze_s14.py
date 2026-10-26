"""S14 oracle table (ROADMAP §14 "### S14 Audio v2"; docs/S14-BRIEF.md; CONTRACT §5.4 "S14 conventions").

 python -I tests/s14/analyze_s14.py <folder with se-*.jsonl / sn-*.jsonl> [--lenient] [--json]
 python -I tests/s14/analyze_s14.py --se-log <se-*.jsonl> --sn-log <sn-*.jsonl> [--lenient] [--json]
 python -I tests/s14/analyze_s14.py --selftest

py run`, S14 on by default; `launcher.py s14 --notes`); this table judges its
logs with the Python twin of the S14 rules below (never the plugins' own verdicts):

 S14.PIN the twin's canonical text hashes to the pinned value, and the C# source pins the same number
 S14.N1 point 1, the jetpack loop: in every periodic s14.jetpack row (>= 1 s after a change) with the swim latch holding
 the character and the jetpack not running, SE plays neither the jetpack idle loop (emitter 4) nor its run loop
 (emitter 0), read back from SE's own emitters (>= 3 such rows)
 S14.N2 point 2, every SE sound under water: in the periodic s14.muffle rows under water (seMixActive, not in a dry room,
 the polish on), SE applied the effect to >= 90 % of the modifiable sounds that started (>= 10), and the voice
 filter read back from XAudio2 is <= 1,000 Hz (median); above water no sound got it and the readback is dry
 (>= DryCutoffMinHz) when sampled
 S14.
 camera y, not the view's mix: in the s14.eye counts, every update with the camera (an independent MainCamera read)
 more than DeadbandMm from the surface has the decision on its side, at most 2 frames per crossing (the frame of lag),
 with >= 1 crossing and >= 60 judged frames; under water the N2 rows are those with the eye under (eyeUnder)
 S14.N2C round 4 (too muffled in a Realistic world), effect.chain: of the modifiable SE sounds that started under water
 (>= 10) none carries two low-passes (the effect over a Real* recording) and >= 90 % carry exactly one, in either sound
 mode (the row names the world's mode: Arcade or Realistic)
 S14.N2G point 2, the building sounds: >= 90 % of the place/delete/rotate/colour sounds under water muffled (INFO without any)
 S14.N2R INFO: SE's reverb under water (the player's EnableReverb option decides whether SE has one)
 S14.N3E point 3,  in the last s14.weapons row with weapons under water near the camera every one is on (a ship
 saved under water reloads with the guns the Water Mod switched off; the plugin switches them on once per grid)
 S14.N3 point 3, weapons: the Water Mod's in-memory configs of its weapon blocks are all FunctionalUnderwater (the world mod's
 override loaded: 20 of 20); INFO the weapons under water near the camera, enabled and working
 S14.N4L point 4a, the listener: in >= 95 % of the s14.listener rows (>= 5) FMOD's listener read back is the camera (<= 5 cm,
 <= 2 deg); INFO S14.N4C names the scene's StudioListener and its offset from the camera (the left-ear cause). Review 3:
 the readback is taken right after the override, so when the scene's listener already sat on the camera (median
 <= 5 cm, <= 2 deg) N4L is INCONCLUSIVE (the left-ear cause is elsewhere), not PASS
 S14.N4P round 4 (the left ear), pan.row: in the s14.pan rows with an SE voice judged in 3D or the own ship's sound playing in
 third person (>= 3 rows), no 3D voice roughly in front of or behind the camera (|geometric pan| < Centred) is heard
 in one ear only, beyond SignMin every voice is on the geometry's side (<= 5 % flips), SE's listener is the camera
 (front and top <= 2 deg) and the controlled ship's sound plays 2D (pan.own: ownShip3D 0); INFO S14.N4Q: what SE's own
 matrix of the 2-channel 3D voices was before pan.stereo (the cause read from the session)
 S14.N4E point 4b, the envelope on whichever source plays (SE's own thruster sound by default, Subnautica's loop with
 `engine subnautica`): every measured attack takes 150-300 ms and every release 200-400 ms (>= 3 attacks, both logs)
 S14.N4V point 4b, the level: the volume read back (SE: the thruster voice's multiplier; Subnautica: FMOD's getVolume)
 equals the twin's level x gain(rpm) within 0.02 in >= 95 % of the periodic s14.engine rows (>= 5): small corrections
 quieter; INFO S14.N4T: which source played each thruster type
 S14.N4S point 4b with `engine subnautica`: in >= 90 % of the periodic s14.thrusters rows with SE thrusters whose type
 Subnautica's loop plays (>= 3) every unsilenced block's voice is faded out and no ship-level thruster emitter of those
 types (SE's ship sound system) plays above 0.05 (no double sound); MISSING (not required) in the default mode
 S14.N5 point 5, the jets above water: in >= 90 % of the s14.jets rows (>= 3) every above-water thruster flame shows its jet
 S14.N5O round 4, jets.old: while the jets are on, scene-above's kit drew no thruster puff (oldFlames 0 in every s14.jets row,
 >= 3 rows)
 S14. the rows had a jet on every flame and
 up to 850 live particles while nothing showed): judged on Subnautica's picture, not on particle counts. The scripted
 capture pair of a folder (launcher.py s15 --parts jets: captures/jets-on.png and jets-control.png, the session's
 s14_jets_pose): three injected above-water flames (hydrogen, ion, atmospheric) with the jets on against the same pose
 with the jets off (scene-above's kit off in both, so only the jets differ): per flame at least Jets.VisibleMinPx
 pixels brighter by Jets.VisibleLumaMin inside its projected box (format jets.visible); MISSING without the pair;
 INFO S14.N5P: the rows' live particles and the jets a camera rendered
 S14.N5S INFO: every sampled jet's written length (speed x S7p's lifetime) against the twin's JetLengthM (S7p's flame length at
 the jet's strength)
 S14.Mx.red a seeded failure turns exactly its rows red (--selftest)

"""
from __future__ import annotations

import glob
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
if os.path.join(ROOT, "tests", "s7t") not in sys.path:
    sys.path.insert(0, os.path.join(ROOT, "tests", "s7t"))
import colour_patch as CP  # noqa: E402 (the pure-Python PNG loader the S15 analyzer uses)

PINNED_HASH = 0x1261b20244b0b097
PINNED_LINES, PINNED_BYTES = 70, 7180
CS_SOURCE = os.path.join(ROOT, "src", "DeepEngineers.Wire", "S14Conventions.cs")

MUTANT_ROWS = {"S14.M1": ("S14.N1",), "S14.M2": ("S14.N2", "S14.N2C"), "S14.M3": ("S14.N3", "S14.N3E"), "S14.M4": ("S14.N4L",),
               "S14.M5": ("S14.N4E", "S14.N4V"), "S14.M6": ("S14.N5", "S14.N5O"), "S14.M7": ("S14.N4E", "S14.N4V", "S14.N4S"),
               "S14.M8": ("S14.N4P",), "S14.M9": ("S14.N2C",), "S14.M10": ("S14.N2E",)}

# ------------------------------------------------------------------------------------------------------------- the twin

EFFECT = "LowPassHelmet"
CONSTS = [
    ("Effect", "UnderwaterCutoffHz", 1800), ("Effect", "DryCutoffMinHz", 5000), ("Effect", "SampleRateHz", 44100),
    ("Muffle", "EyeYMm", 0), ("Muffle", "DeadbandMm", 50),
    ("Envelope", "AttackMs", 200), ("Envelope", "ReleaseMs", 300), ("Envelope", "GainMinPermille", 250),
    ("Envelope", "GainExponentPermille", 1500), ("Envelope", "SeThrusterFadeMs", 300),
    ("Listener", "ToleranceMm", 50), ("Listener", "ToleranceCentiDeg", 200),
    ("Jetpack", "GraceMs", 500), ("Rows", "PeriodMs", 1000),
    ("Pan", "OneSidedPermille", 20), ("Pan", "CentredPermille", 350), ("Pan", "SignMinPermille", 300), ("Pan", "DownmixPermille", 500),
    ("Chain", "MaxLowPass", 1),
    ("Weapons", "Configs", 20),
    ("Engine", "ModeSe", 0), ("Engine", "ModeSubnautica", 1), ("Engine", "SeStartStrengthPermille", 100),
    ("C7", "Offset", 0xC4), ("C7", "Size", 4), ("C7", "Version", 1),
    ("C7Flags", "SnLoops", 1), ("C7Flags", "Hydrogen", 2), ("C7Flags", "Ion", 4),
    ("Jets", "IdleFloorPermille", 300), ("Jets", "ConeAngleDeg", 5), ("Jets", "ShapeRadiusPermille", 500), ("Jets", "TipSizePermille", 350),
    ("Jets", "FadeInPermille", 80), ("Jets", "MaxParticles", 400), ("Jets", "VisibleMinPx", 400), ("Jets", "VisibleLumaMin", 30), ("Jets", "VisibleRefRows", 1440),
    ("Jets", "S7pFlameLengthPermille", 9500), ("Jets", "S7pLifetimeMs", 140), ("Jets", "S7pDefaultLengthScalePermille", 1150),
]
C = {n: v for _g, n, v in CONSTS}
GUI = ["HudColorBlock", "HudDeleteBlock", "HudPlaceBlock", "HudPlaceItem", "HudRotateBlock"]
# SE's four filter effects (Content/Data/AudioEffects.sbc), Hz: the readback ceiling under water is the effect in use x 1.25
EFFECT_HZ = {"LowPassNoHelmetNoOxy": 100, "LowPassHelmet": 1800, "LowPassCockpit": 750, "LowPassCockpitNoOxy": 600}
FORMAT_LINES = [
    "effect.name LowPassHelmet (SE's own, 1800 Hz) for every modifiable SE sound while the camera is under the sea (S2's camera SN y below Muffle.EyeYMm / 1000: the eye's own submersion, whatever the view state, its waterline hysteresis, dwell or ramp; owner 2026-10-24) and not in an airtight room; supersedes the S7 audio.muffle effect name",
    "effect.chain under water one low-pass per modifiable SE voice in both sound modes: where SE picked a pair's Real* recording, SelectCue answers its Arcade one (m_realistic false); chain = the effect applied + a Real* recording playing = MaxLowPass",
    "pan.stereo MyXAudio2.Apply3D of a voice with 2 channels or not the count SE used: one 1-channel X3DAudioCalculate (same listener and emitter; Matrix, RedirectToLfe) gives g[d]; matrix[d x channels + s] = g[d] x Downmix for 2 channels x SE's distance factor (1 - distance / max when the inner radius is 0; 0 silent); a 1-channel voice SE counted right keeps SE's matrix",
    "pan.own the ship-level sounds (MyShipSoundComponent) of the grid whose ship controller the player controls play SE's 2D path in both views (m_shouldPlay2D true after UpdateShouldPlay2D, SE restarting its loops once); every other ship SE's own 3D at its sound source",
    "pan.row s14.pan: per sampled SE voice the geometric pan = dot(direction to the voice, camera right) and the matrix pan = (R - L) / (R + L) of the voice's own output matrix; a 3D voice with |geometric| < Centred is never one-sided (quieter / louder < OneSided) and with |geometric| >= SignMin its sign is the geometry's",
    "jets.old while the jets are on and their material is found, scene-above's kit draws no thruster puff (oldFlames 0); otherwise the kit draws as before",
    "jets.length the cone reaches SE's flame length at the jet's strength (IdleFloor + (1 - IdleFloor) x intensity): S7p's FlameLength = S7pFlameLength x the flame's length scale (S7pDefaultLengthScale without one) x radius x strength x jetScale, in S7pLifetimeMs (speed = length / lifetime); start size S7p's FlameDiameter(radius, jetScale), TipSize of it at the tip, fading in over FadeIn of the life; rate S7p's FlameRate at the strength; radius ShapeRadius x SE's flame radius",
    "jets.visible S14.N5V: a Subnautica capture of injected above-water thruster flames, one per kind (hydrogen, ion, atmospheric), against the same pose with the jets off (the kit off in both): per flame at least VisibleMinPx pixels brighter by VisibleLumaMin within its projected box (VisibleMinPx at VisibleRefRows rows; x (rows / VisibleRefRows) ^ 2 for another capture height)",
    "effect.arcade a world without RealisticSound gets MyEntity3DSoundEmitter.EmitterMethods[3] = the emitter's own SelectEffect, so PlaySoundInternal applies the S7 postfix's answer",
    "effect.gui MyGuiAudio.PlaySound(MyGuiSounds) of the gui names: MyAudio.ApplyEffect on the returned voice under the same gate",
    "effect.reverb MyAudio.ApplyReverb true under the same gate when the player's EnableReverb is on; the reverb detector's preset back after",
    "jetpack.silence MyCharacterSoundComponent.SelectSound answers its EmptySoundPair while the swim latch holds the character in Flying with the jetpack not running",
    "thruster.se under water (seMixActive, block SN y < 0) a block SE's ship sound system has not silenced plays SE's own cue of its definition (muffled by effect.name), its emitter's VolumeMultiplier = the envelope level x gain(strength), or fades to 0 over SeThrusterFadeMs when Subnautica plays its type (c7.s14); a silenced block is never written; above water nothing is touched (a value S14 wrote is given back once)",
    "thruster.ship a ship's grid-level thruster sound per type (MyShipSoundComponent.UpdateThrusterVolume) under water: its main emitter x the envelope of the type's strongest thruster, or main, idle and push emitters x a fade to 0 over SeThrusterFadeMs when Subnautica plays the type; above water untouched",
    "thruster.start under water SE's own sound starts on the tick the strength passes SeStartStrength (MyThrust.UpdateSoundState called by the plugin) instead of SE's 100-tick cadence",
    "thruster.sn the envelope (level and rpm or strength to their targets at full scale per AttackMs up and per ReleaseMs down; the sound stops only at level 0; volume = level x gain) on whichever source plays",
    "engine.mode se (default): no Subnautica loop; subnautica: hydrogen = SubControl.engineRPMManager.engineRpmSFX (Cyclops), ion = SeaMoth.engineSound.engineRpmSFX, atmospheric and other = SE's own; one loop per grid and type",
    "c7.s14 SnStatus 0xc4-0xc7 written by Subnautica every frame: version, flags (SnLoops, Hydrogen, Ion = the types its loop plays), 2 reserved; version 0 = absent (SE keeps its own thruster sound)",
    "thruster.gain gain(rpm) = GainMin + (1 - GainMin) x rpm ^ GainExponent",
    "listener.sn every FMOD listener = the main camera's pose (position, forward, up; attenuation at the camera) after StudioListener.SetListenerLocation while the bridge drives the camera",
    "weapons.worldmod Data/WaterConfig.xml BlockConfigs: the Water Mod's 20 weapon entries, FunctionalUnderwater true, every other attribute equal",
    "jets.above per above-water thruster flame a held flame cone of the SN plugin along the flame (Local space, Cone ConeAngleDeg, MaxParticles), its particles drawn with S7p's above-water additive material (Unity's Legacy Shaders/Particles/Additive, else Particles/Additive or Mobile/Particles/Additive, on a soft dot made in code) in SE's flame colour (S7p's thruster colour through its Glow); not the Prawn's jet: Exosuit.fxcontrol.emitters[0].fx draws with UWE/Particles/UBER underwater materials (waterBlob_01_sheet_premul_underwaterClipCloseLit, bubble_01_sheet_premul_underwaterCameraVertexLit) and shows nothing above the sea",
]

def canonical_text() -> str:
    out = ["ext s14 audio 6", "effect " + EFFECT]
    for g, n, v in CONSTS:
        out.append("const %s %s %d" % (g, n, v))
    for g in GUI:
        out.append("gui " + g)
    for f in sorted(FORMAT_LINES):   # ordinal (ASCII) order, as C#'s string.CompareOrdinal
        out.append("format " + f)
    return "".join(line + "\n" for line in out)

def fnv1a64(data: bytes) -> int:
    h = 0xcbf29ce484222325
    for b in data:
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def clamp01(v):
    try:
        v = float(v)
    except (TypeError, ValueError):
        return 0.0
    if v != v:
        return 0.0
    return 0.0 if v < 0 else 1.0 if v > 1 else v

def step(current, target, dt_ms, attack_ms, release_ms):
    """S14Rules.Step: full scale per attack up, per release down."""
    if not (dt_ms > 0):
        return current
    if target > current:
        if not (attack_ms > 0):
            return target
        return min(target, current + dt_ms / attack_ms)
    if target < current:
        if not (release_ms > 0):
            return target
        return max(target, current - dt_ms / release_ms)
    return current

def engine_gain(rpm):
    mn = C["GainMinPermille"] / 1000.0
    return mn + (1.0 - mn) * clamp01(rpm) ** (C["GainExponentPermille"] / 1000.0)

def engine_volume(level, rpm):
    return clamp01(level) * engine_gain(rpm)

def xaudio_frequency(hz):
    return max(0.0, min(1.0, 2.0 * math.sin(3.14 * hz / C["SampleRateHz"])))

def cutoff_hz(f):
    return math.asin(min(1.0, max(0.0, f) / 2.0)) * C["SampleRateHz"] / 3.14

def jet_strength(intensity):
    floor = C["IdleFloorPermille"] / 1000.0
    return floor + (1.0 - floor) * clamp01(intensity)

def jet_length(intensity, radius, length_scale=0.0, knob=1.0):
    """jets.length: S7p's FlameLength at the jet's strength (S7pEffectRules.FlameLength: 0 without a radius or knob)."""
    if not (radius > 0) or not (knob > 0):
        return 0.0
    ls = length_scale if length_scale > 0 else C["S7pDefaultLengthScalePermille"] / 1000.0
    return C["S7pFlameLengthPermille"] / 1000.0 * ls * radius * jet_strength(intensity) * knob

# ---- jets.visible (S14.N5V): the projected box of each injected flame and the brighter pixels in it

def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])

def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]

def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])

def _norm(a):
    n = math.sqrt(_dot(a, a)) or 1.0
    return (a[0] / n, a[1] / n, a[2] / n)

def project(cam, fwd, up, vfov_deg, w, h, p):
    """A Subnautica (Unity, left-handed) world point onto a capture w x h: (u, v) pixels from the top left, or None behind."""
    f = _norm(fwd)
    right = _norm(_cross(up, f))
    up2 = _cross(f, right)
    d = _sub(p, cam)
    z = _dot(d, f)
    if z <= 0.05:
        return None
    fp = (h / 2.0) / math.tan(math.radians(vfov_deg) / 2.0)
    return (w / 2.0 + fp * _dot(d, right) / z, h / 2.0 - fp * _dot(d, up2) / z)

def flame_box(pose, flame, w, h, pad=8):
    """The pixel box (x0, y0, x1, y1) around an injected flame: its nozzle to boxLenM along its direction, boxHalfM to each side."""
    cam, fwd, up = tuple(pose["cam"]), tuple(pose["fwd"]), tuple(pose.get("up") or (0.0, 1.0, 0.0))
    right = _norm(_cross(up, _norm(fwd)))
    at, dr = tuple(flame["at"]), _norm(tuple(flame["dir"]))
    ln, hw = float(flame.get("boxLenM", 10.0)), float(flame.get("boxHalfM", 1.8))
    tip = (at[0] + dr[0] * ln, at[1] + dr[1] * ln, at[2] + dr[2] * ln)
    pts = []
    for base in (at, tip):
        for sgn in (-1.0, 1.0):
            q = project(cam, fwd, up, float(pose.get("vfovDeg", 70.0)), w, h, (base[0] + sgn * hw * right[0], base[1] + sgn * hw * right[1], base[2] + sgn * hw * right[2]))
            if q is not None:
                pts.append(q)
    if len(pts) < 2:
        return None
    x0, x1 = max(0, int(min(q[0] for q in pts)) - pad), min(w - 1, int(max(q[0] for q in pts)) + pad)
    y0, y1 = max(0, int(min(q[1] for q in pts)) - pad), min(h - 1, int(max(q[1] for q in pts)) + pad)
    return (x0, y0, x1, y1) if x1 > x0 and y1 > y0 else None

def _luma(rgb):
    return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]

def brighter_pixels(on, ctl, box, luma_min):
    x0, y0, x1, y1 = box
    n = 0
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            if _luma(on.pixel(x, y)) - _luma(ctl.pixel(x, y)) >= luma_min:
                n += 1
    return n

def visible_min_px(rows):
    """jets.visible: VisibleMinPx at VisibleRefRows rows, x (rows / VisibleRefRows) ^ 2 (S14Rules.JetVisibleMinPx)."""
    k = float(rows) / C["VisibleRefRows"] if rows > 0 else 1.0
    return max(1, int(math.floor(C["VisibleMinPx"] * k * k + 0.5)))

def judge_jets_capture(on_path, ctl_path, pose):
    """([(kind, brighter px, box)] for the pose's flames, the pixels each needs); raises on unreadable images."""
    on, ctl = CP.load_image(on_path), CP.load_image(ctl_path)
    if (on.width, on.height) != (ctl.width, ctl.height):
        raise ValueError("the pair differs in size")
    out = []
    for fl in pose.get("flames") or []:
        box = flame_box(pose, fl, on.width, on.height)
        n = brighter_pixels(on, ctl, box, C["VisibleLumaMin"]) if box else 0
        out.append((str(fl.get("kind")), n, box))
    return out, visible_min_px(on.height)

def matrix_one_sided(left, right):
    """S14Rules.OneSided: the quieter side below OneSided of the louder one."""
    hi, lo = max(left, right), min(left, right)
    return hi > 1e-4 and lo < C["OneSidedPermille"] / 1000.0 * hi

def pan_centred(geo):
    return geo is not None and geo == geo and abs(geo) < C["CentredPermille"] / 1000.0

def pan_sign_agrees(geo, pan):
    if geo is None or geo != geo or abs(geo) < C["SignMinPermille"] / 1000.0:
        return True
    if pan is None or pan != pan:
        return False
    return (geo > 0) == (pan > 0)

def listener_ok(offset_m, angle_deg):
    try:
        return 0 <= float(offset_m) <= C["ToleranceMm"] / 1000.0 and 0 <= float(angle_deg) <= C["ToleranceCentiDeg"] / 100.0
    except (TypeError, ValueError):
        return False

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
    out = ["%-9s %-8s %-44s %-30s %s" % ("check", "status", "measured", "threshold", "what"), "-" * 150]
    for r in rows:
        out.append("%-9s %-8s %-44s %-30s %s" % (r["id"], r["status"] + ("" if r.get("required", True) else "*"),
                                                 str(r["measured"])[:44], str(r["threshold"])[:30], r["what"]))
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

def num(r, k, default=0.0):
    try:
        v = r.get(k, default)
        return default if v is None else float(v)
    except (TypeError, ValueError):
        return default

def median(xs):
    xs = sorted(xs)
    if not xs:
        return float("nan")
    m = len(xs) // 2
    return xs[m] if len(xs) % 2 else 0.5 * (xs[m - 1] + xs[m])

def jets_visibility(jets):
    """(status, measured) of S14.N5V from {'on': path, 'control': path, 'pose': event}; MISSING without the pair."""
    on, ctl, pose = jets.get("on"), jets.get("control"), jets.get("pose")
    if not (on and ctl and pose and os.path.isfile(on) and os.path.isfile(ctl)):
        return "MISSING", "no jets-on / jets-control capture pair with its pose (launcher.py s15 --parts jets)"
    try:
        res, need = judge_jets_capture(on, ctl, pose)
    except Exception as e:  # noqa: BLE001 - an unreadable pair is a FAIL row, never a crash
        return "FAIL", "the pair unreadable: %s" % e
    if not res:
        return "FAIL", "the pose names no flame"
    seen = [k for k, n, _b in res if n >= need]
    text = "; ".join("%s %d px%s" % (k, n, "" if b else " (box off the picture)") for k, n, b in res) + " (need %d each)" % need
    return ("PASS" if len(seen) == len(res) else "FAIL"), text

def find_jets(folder):
    """The capture pair and the pose of a scripted folder (launcher.py s15 --parts jets), or None."""
    if not folder:
        return None
    pose = None
    for f in sorted(glob.glob(os.path.join(folder, "*-session.jsonl"))):
        for e in read_jsonl([f]):
            if e.get("_ev") == "s14_jets_pose" or e.get("ev") == "s14_jets_pose":
                pose = e
    cap = os.path.join(folder, "captures")
    return {"on": os.path.join(cap, "jets-on.png"), "control": os.path.join(cap, "jets-control.png"), "pose": pose}

def evaluate_events(se, sn, jets_pair=None):
    rows = []
    # PIN
    text = canonical_text()
    h = fnv1a64(text.encode("ascii"))
    cs = cs_pinned_hash()
    ok = h == PINNED_HASH and text.count("\n") == PINNED_LINES and len(text) == PINNED_BYTES and (cs is None or cs == PINNED_HASH)
    row(rows, "S14.PIN", "PASS" if ok else "FAIL", "0x%016x" % h, "0x%016x" % PINNED_HASH, "the S14 conventions pinned (twin = C#)")

    # N1 the jetpack loop
    jets = [r for r in by(se, "s14.jetpack") if r.get("why") == "periodic" and r.get("swimHeld") and not r.get("jetpackRunning")]
    loud = [r for r in jets if r.get("idleLoopPlaying") or r.get("runLoopPlaying")]
    if len(jets) < 3:
        row(rows, "S14.N1", "MISSING", "%d judged rows" % len(jets), ">= 3 rows, 0 loud", "point 1: no jetpack loop while swimming with the jetpack off")
    else:
        row(rows, "S14.N1", "PASS" if not loud else "FAIL", "%d loud of %d rows" % (len(loud), len(jets)), "0 loud",
            "point 1: no jetpack loop while swimming with the jetpack off", silenced=sum(num(r, "silenced") for r in jets))

    # N2 the underwater treatment
    mrows = [r for r in by(se, "s14.muffle") if r.get("why") == "periodic"]
    # ext s14 audio 5: the muffle's gate is the eye (eyeUnder); older rows carry only the mix
    def _under(r):
        return r.get("eyeUnder") if "eyeUnder" in r else r.get("mixActive")
    under = [r for r in mrows if _under(r) and not r.get("inRoom") and r.get("polish", True)]
    above = [r for r in mrows if not _under(r)]
    mod = sum(num(r, "modifiable") for r in under)
    app = sum(num(r, "applied") for r in under)
    cut_u = [num(r, "sampleCutoffHz") for r in under if r.get("sampleCutoffHz") is not None]
    cut_a = [num(r, "sampleCutoffHz") for r in above if r.get("sampleCutoffHz") is not None]
    app_above = sum(num(r, "applied") for r in above)
    if mod < 10:
        row(rows, "S14.N2", "MISSING", "%d modifiable sounds under water" % mod, ">= 10, >= 90 % muffled", "point 2: every SE sound under water muffled")
    else:
        share = app / mod
        mu = median(cut_u)
        effects = [str(r.get("effect")) for r in under]
        effect = max(set(effects), key=effects.count) if effects else EFFECT
        ceiling = 1.25 * EFFECT_HZ.get(effect, 800)
        dry_ok = not cut_a or min(cut_a) >= C["DryCutoffMinHz"]
        ok = share >= 0.9 and (not cut_u or mu <= ceiling) and app_above == 0 and dry_ok
        row(rows, "S14.N2", "PASS" if ok else "FAIL",
            "%.0f%% of %d; filter %s Hz; above %d, %s" % (100 * share, mod, "%.0f" % mu if cut_u else "-", app_above,
                                                         "dry" if dry_ok else "min %.0f Hz" % min(cut_a)),
            ">= 90 %%, <= %.0f Hz; 0 above" % ceiling, "point 2: every SE sound under water muffled (SE's effect %s, read back)" % effect,
            routes=sorted({str(r.get("route")) for r in under}))
    gui = sum(num(r, "gui") for r in under)
    gum = sum(num(r, "guiMuffled") for r in under)
    if gui < 1:
        row(rows, "S14.N2G", "INFO", "no building sound under water", ">= 90 % muffled", "point 2: place/delete/rotate/colour blocks", required=False)
    else:
        row(rows, "S14.N2G", "PASS" if gum / gui >= 0.9 else "FAIL", "%d of %d" % (gum, gui), ">= 90 % muffled",
            "point 2: place/delete/rotate/colour blocks muffled under water")
    # N2C effect.chain (round 4): one low-pass per modifiable voice under water, either sound mode
    chained = [r for r in under if "chainPlays" in r]
    cp = sum(num(r, "chainPlays") for r in chained)
    modes = sorted({str(r.get("mode")) for r in chained if r.get("mode")})
    if cp < 10:
        row(rows, "S14.N2C", "MISSING", "%d sounds under water with the chain measured" % cp, ">= 10, 0 stacked, >= 90 % exactly one",
            "round 4: one low-pass per SE sound under water (Arcade and Realistic alike)")
    else:
        c1, c2, c0 = sum(num(r, "chainOne") for r in chained), sum(num(r, "chainTwo") for r in chained), sum(num(r, "chainZero") for r in chained)
        ok = c2 == 0 and c1 >= 0.9 * cp
        row(rows, "S14.N2C", "PASS" if ok else "FAIL", "one %d, two %d, none %d of %d (%s)" % (c1, c2, c0, cp, "/".join(modes) or "-"),
            "0 two, >= 90 % one", "round 4: one low-pass per SE sound under water (the effect; never over a Real* recording)",
            cueSwaps=sum(num(r, "cueSwaps") for r in chained), recordings=sum(num(r, "recordings") for r in chained))
    if under:
        avail = sum(1 for r in under if r.get("reverbAvailable"))
        on = sum(1 for r in under if r.get("reverbOn"))
        row(rows, "S14.N2R", "INFO", "reverb on %d of %d rows (available %d)" % (on, len(under), avail), "-",
            "point 2: SE's reverb under water (the player's EnableReverb option)", required=False)

    # N2E the eye (ext s14 audio 5): the decision against an independent camera read, frame by frame
    eye = by(se, "s14.eye")
    judged = sum(num(r, "judged") for r in eye)
    mism = sum(num(r, "mismatch") for r in eye)
    cross = sum(num(r, "crossings") for r in eye)
    if judged < 60 or cross < 1:
        row(rows, "S14.N2E", "MISSING", "%d judged frames, %d crossings" % (judged, cross), ">= 60 frames, >= 1 crossing",
            "ext s14 audio 5: the muffle follows the eye (S2's camera y), not the view's mix")
    else:
        allowed = 2 * cross
        row(rows, "S14.N2E", "PASS" if mism <= allowed else "FAIL", "%d of %d frames off the camera's side over %d crossings" % (mism, judged, cross),
            "<= 2 per crossing (the frame of lag)", "ext s14 audio 5: the muffle follows the eye (S2's camera y), not the view's mix (the diver at the surface)",
            muffledAbove=sum(1 for r in eye if r.get("muffle") and r.get("camY") is not None and num(r, "camY") > C["DeadbandMm"] / 1000.0))

    # N3 weapons
    w = [r for r in by(se, "s14.weapons") if r.get("waterMod") == "found"]
    if not w:
        row(rows, "S14.N3", "MISSING", "no s14.weapons row with the Water Mod found", "20 of 20 functional", "point 3: ship weapons work under water")
    else:
        last = w[-1]
        bad = [r for r in w if num(r, "functionalUnderwater") != num(r, "weaponConfigs") or num(r, "weaponConfigs") < C["Configs"]]
        row(rows, "S14.N3", "PASS" if not bad else "FAIL", "%d of %d functional (last row)" % (num(last, "functionalUnderwater"), num(last, "weaponConfigs")),
            "%d of %d in every row" % (C["Configs"], C["Configs"]), "point 3: the Water Mod keeps the weapons on under water (world mod override loaded)",
            weaponsUnderwater=max(num(r, "weaponsUnderwater") for r in w), enabledUnderwater=max(num(r, "enabledUnderwater") for r in w))
    wu = [r for r in by(se, "s14.weapons") if num(r, "weaponsUnderwater") > 0]
    if not wu:
        row(rows, "S14.N3E", "MISSING", "no weapon under water near the camera", "every one on", "point 3: the guns under water are on")
    else:
        last = wu[-1]
        on, n = num(last, "enabledUnderwater"), num(last, "weaponsUnderwater")
        row(rows, "S14.N3E", "PASS" if on >= n else "FAIL", "%d of %d on (last row); %d switched back on" % (on, n, max(num(r, "turnedOnTotal") for r in wu)),
            "every one on", "point 3: the guns under water are on (a weapon still off: the terminal toggle is the fallback)")

    # N4L the listener
    lis = by(sn, "s14.listener")
    if len(lis) < 5:
        row(rows, "S14.N4L", "MISSING", "%d rows" % len(lis), ">= 5 rows, >= 95 % at the camera", "point 4a: FMOD's listener on the camera")
    else:
        good = sum(1 for r in lis if listener_ok(r.get("readOffsetM"), r.get("readAngleDeg")))
        scenes = sorted({str(r.get("scene")) for r in lis})
        off = median([num(r, "sceneOffsetM") for r in lis])
        ang = median([num(r, "sceneAngleDeg") for r in lis])
        if listener_ok(off, ang):
            row(rows, "S14.N4L", "INCONCLUSIVE", "%d of %d at the camera; the scene's listener %.3f m, %.1f deg" % (good, len(lis), off, ang),
                "scene listener off the camera", "point 4a: listener already on the camera; the left-ear cause is elsewhere", required=False)
        else:
            row(rows, "S14.N4L", "PASS" if good >= 0.95 * len(lis) else "FAIL", "%d of %d at the camera" % (good, len(lis)),
                "<= 5 cm, <= 2 deg in >= 95 %", "point 4a: FMOD's listener moved from the scene's place onto the camera (third person included)")
        row(rows, "S14.N4C", "INFO", "%s, %.2f m from the camera" % (",".join(scenes)[:40], off), "-",
            "point 4a: the scene's StudioListener (where Subnautica alone would hear from)", required=False)

    # N4P / N4Q the left ear (round 4): SE's own voices' matrices against the geometry
    pans = [r for r in by(se, "s14.pan") if num(r, "judged") > 0 or (not r.get("firstPerson") and num(r, "ownShip2D") + num(r, "ownShip3D") > 0)]
    if len(pans) < 3:
        row(rows, "S14.N4P", "MISSING", "%d rows with a 3D SE voice judged" % len(pans), ">= 3 rows; 0 one-sided centred, <= 5 % flips",
            "round 4: SE's 3D sounds in both ears where they should be (play in third person)")
    else:
        judged = sum(num(r, "judged") for r in pans)
        one = sum(num(r, "oneSidedCentred") for r in pans)
        flips = sum(num(r, "signFlips") for r in pans)
        lis = [r for r in pans if r.get("listener") == "read"]
        lis_bad = [r for r in lis if num(r, "listenerFrontDeg") > 2.0 or num(r, "listenerTopDeg") > 2.0]
        own3 = sum(num(r, "ownShip3D") for r in pans)
        own2 = sum(num(r, "ownShip2D") for r in pans)
        ok = one == 0 and flips <= 0.05 * judged and not lis_bad and own3 == 0
        row(rows, "S14.N4P", "PASS" if ok else "FAIL", "%d one-sided centred, %d flips of %d; own ship 3D %d / 2D %d; listener off %d" % (one, flips, judged, own3, own2, len(lis_bad)),
            "0; <= 5 %; own 3D 0; <= 2 deg", "round 4: SE's sounds heard where they are, the own ship in both ears (no left-ear-only voice)",
            thirdPerson=sum(1 for r in pans if not r.get("firstPerson")))
    allpans = by(se, "s14.pan")
    if allpans:
        st3 = sum(num(r, "stereo3D") for r in allpans)
        raw1 = sum(num(r, "rawOneSidedCentred") for r in allpans)
        row(rows, "S14.N4Q", "INFO", "2-channel 3D calls %d, fixed %d, SE's own one-sided while centred %d, channel mismatch %d" % (
            st3, sum(num(r, "fixed") for r in allpans), raw1, sum(num(r, "mismatch") for r in allpans)), "-",
            "round 4: SE's own matrix of the 2-channel 3D voices before pan.stereo (> 0 one-sided centred: that was the left ear)", required=False)

    # N4E / N4V the envelope on whichever source plays (SE's own thruster sound, Subnautica's loop)
    eng = by(se, "s14.engine") + by(sn, "s14.engine")
    attacks = [num(r, "attackMs") for r in eng if r.get("why") == "attack" or (r.get("why") == "start" and num(r, "attackMs", -1) >= 0 and num(r, "level") >= 1.0)]
    releases = [num(r, "releaseMs") for r in eng if r.get("why") == "stopped" and num(r, "releaseMs", -1) >= 0]
    if len(attacks) < 3:
        row(rows, "S14.N4E", "MISSING", "%d attacks" % len(attacks), ">= 3, 150-300 ms; releases 200-400 ms", "point 4b: a short ramp")
    else:
        bad_a = [a for a in attacks if not 150.0 <= a <= 300.0]
        bad_r = [x for x in releases if not 200.0 <= x <= 400.0]
        row(rows, "S14.N4E", "PASS" if not bad_a and not bad_r else "FAIL",
            "attack %.0f ms (%d), release %s (%d)" % (median(attacks), len(attacks), "%.0f ms" % median(releases) if releases else "-", len(releases)),
            "150-300 / 200-400 ms", "point 4b: the thruster sound ramps in and out (no hard start/stop; the release is the envelope's clock: SE's own 100-tick update may stop a block's sound mid-fade)", badAttacks=bad_a[:5], badReleases=bad_r[:5],
            sources=sorted({str(r.get("source", "subnautica")) for r in eng}))
    per = [r for r in eng if r.get("why") == "periodic" and r.get("playing") and num(r, "volumeRead", -1) >= 0]
    if len(per) < 5:
        row(rows, "S14.N4V", "MISSING", "%d periodic rows" % len(per), ">= 5, |read - twin| <= 0.02", "point 4b: the level by thrust")
    else:
        good = sum(1 for r in per if abs(num(r, "volumeRead") - engine_volume(num(r, "level"), num(r, "rpmS"))) <= 0.02)
        row(rows, "S14.N4V", "PASS" if good >= 0.95 * len(per) else "FAIL", "%d of %d" % (good, len(per)), "|read - twin| <= 0.02 in >= 95 %",
            "point 4b: the sound plays level x gain(thrust) (small corrections quieter)")
        starts = sum(1 for r in eng if r.get("why") == "start")
        resumes = sum(1 for r in eng if r.get("why") == "resume")
        row(rows, "S14.N4R", "INFO", "%d starts, %d resumed without restart" % (starts, resumes), "-",
            "point 4b: corrections a moment apart keep one sound", required=False)
    types = {}
    for r in eng:
        types.setdefault(str(r.get("type", "?")), set()).add(str(r.get("source", "subnautica")))
    for r in by(se, "s14.thrusters"):
        for k in ("hydrogen", "ion", "atmospheric"):
            src = r.get(k + "Source")
            if src and src != "-":
                types.setdefault(k, set()).add(str(src))
    if types:
        row(rows, "S14.N4T", "INFO", "; ".join("%s=%s" % (k, "/".join(sorted(v))) for k, v in sorted(types.items())), "-",
            "point 4b: the source per thruster type (se = SE's own cue, subnautica = Cyclops/Seamoth loop)", required=False)

    # N4S SE's thrusters under water
    thr = [r for r in by(se, "s14.thrusters") if r.get("why") == "periodic" and num(r, "covered") + num(r, "shipCovered") > 0]
    if len(thr) < 3:
        row(rows, "S14.N4S", "MISSING", "%d rows with types Subnautica plays" % len(thr), ">= 3, all faded",
            "point 4b, engine subnautica: no double thruster sound", required=False)
    else:
        #  the block layer (unsilenced blocks' voices) and the ship layer (SE's grid-level sound per type)
        good = sum(1 for r in thr if num(r, "coveredQuiet") >= num(r, "covered") and num(r, "shipCoveredAudible") == 0)
        row(rows, "S14.N4S", "PASS" if good >= 0.9 * len(thr) else "FAIL", "%d of %d rows all faded" % (good, len(thr)), ">= 90 %",
            "point 4b, engine subnautica: SE's own sound of the types Subnautica's loop plays is faded (no double sound)")

    # N5 jets above water
    jr = [r for r in by(sn, "s14.jets") if num(r, "aboveThrusters") > 0]
    if len(jr) < 3:
        row(rows, "S14.N5", "MISSING", "%d rows" % len(jr), ">= 3, >= 90 % with every jet", "point 5: thrusters above the water")
    else:
        good = sum(1 for r in jr if num(r, "jets") >= num(r, "aboveThrusters"))
        row(rows, "S14.N5", "PASS" if good >= 0.9 * len(jr) else "FAIL", "%d of %d rows" % (good, len(jr)), ">= 90 %",
            "point 5: every above-water thruster flame has its jet", source=jr[-1].get("source"))
    # N5O jets.old (round 4): no kit thruster puff while the jets are on
    jo = [r for r in by(sn, "s14.jets") if r.get("on", True) and "oldFlames" in r]
    if len(jo) < 3:
        row(rows, "S14.N5O", "MISSING", "%d rows with the jets on" % len(jo), ">= 3 rows, oldFlames 0", "round 4: the jet in place of the old yellow puffs")
    else:
        old = sum(num(r, "oldFlames") for r in jo)
        row(rows, "S14.N5O", "PASS" if old == 0 else "FAIL", "jets %d, old %d (kit skipped %d)" % (sum(num(r, "jets") for r in jo), old, sum(num(r, "kitSkipped") for r in jo)),
            "old 0", "round 4: above water the jet replaces scene-above's thruster puffs")
        row(rows, "S14.N5P", "INFO", "particles %d (median per row), visible %d of %d jets, material %s" % (
            median([num(r, "particles") for r in jo]), sum(num(r, "visible") for r in jo), sum(num(r, "jets") for r in jo),
            jo[-1].get("material") or jo[-1].get("source") or "-"), "-", "the jets alive and in a camera's view (not a visibility proof: N5V)", required=False)
    # N5V jets.visible (round 5): the flames on Subnautica's picture
    vis = jets_visibility(jets_pair or {})
    row(rows, "S14.N5V", vis[0], vis[1], ">= %d px (at %d rows) brighter by %d per flame" % (C["VisibleMinPx"], C["VisibleRefRows"], C["VisibleLumaMin"]),
        "round 5: every above-water thruster kind shows a flame on Subnautica's picture (the capture pair, not the particle count)")
    samples = []
    for r in jr:
        for part in str(r.get("samples") or "").split(";"):
            bits = part.split(",")
            if len(bits) == 4:
                try:
                    samples.append((float(bits[0]), float(bits[1]), float(bits[2]), float(bits[3]), num(r, "knob", 1.0)))
                except ValueError:
                    pass
    if not samples:
        row(rows, "S14.N5S", "MISSING", "no round-5 jet sample", "within 2 % of the twin", "point 5: the jet as long as SE's flame", required=False)
    else:
        bad = [x for x in samples if abs(x[2] - jet_length(x[0], x[1], x[3], x[4])) > 0.02 * max(jet_length(x[0], x[1], x[3], x[4]), 1e-6) + 1e-3]
        row(rows, "S14.N5S", "INFO", "%d of %d off (written length vs S7p's flame length at the jet's strength)" % (len(bad), len(samples)), "within 2 % of the twin",
            "point 5: the jet as long as SE's flame (the written value)", required=False)
    return rows

def evaluate(folder=None, se_logs=None, sn_logs=None):
    se_logs = list(se_logs or [])
    sn_logs = list(sn_logs or [])
    if folder:
        se_logs += sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        sn_logs += sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))
    return evaluate_events(read_jsonl(se_logs), read_jsonl(sn_logs), find_jets(folder))

# ------------------------------------------------------------------------------------------------------------- self-test

def _engine_rows(source="subnautica", types=("ion",)):
    """A synthetic engine session from the twin: 4 corrections (attack/release), periodic rows during the burns."""
    out = []
    ty = list(types)
    dt = 1000.0 / 60.0
    level = rpm_s = 0.0
    t = 0.0
    for burn, rpm in enumerate((0.2, 0.6, 1.0, 0.35)):
        t0 = t
        typ = ty[burn % len(ty)]
        out.append({"_ev": "s14.engine", "why": "start", "level": 0.0, "attackMs": -1, "playing": True, "source": source, "type": typ})
        n = 0
        while level < 1.0:
            level = step(level, 1.0, dt, C["AttackMs"], C["ReleaseMs"])
            rpm_s = step(rpm_s, rpm, dt, C["AttackMs"], C["ReleaseMs"])
            t += dt
            n += 1
        out.append({"_ev": "s14.engine", "why": "attack", "level": level, "attackMs": t - t0 - dt, "playing": True, "source": source, "type": typ})
        for _ in range(60):
            rpm_s = step(rpm_s, rpm, dt, C["AttackMs"], C["ReleaseMs"])
            t += dt
        for _ in range(2):
            out.append({"_ev": "s14.engine", "why": "periodic", "level": level, "rpmS": rpm_s, "volumeRead": round(engine_volume(level, rpm_s), 4),
                        "playing": True, "source": source, "type": typ})
        tr = t
        while level > 0.0:
            level = step(level, 0.0, dt, C["AttackMs"], C["ReleaseMs"])
            t += dt
        out.append({"_ev": "s14.engine", "why": "stopped", "level": 0.0, "releaseMs": t - tr - dt, "playing": False, "source": source, "type": typ})
    return out

def _clean():
    se = [{"_ev": "s14.jetpack", "why": "change", "swimHeld": True, "jetpackRunning": False, "idleLoopPlaying": True, "runLoopPlaying": False}]
    se += [{"_ev": "s14.jetpack", "why": "periodic", "swimHeld": True, "jetpackRunning": False, "idleLoopPlaying": False,
            "runLoopPlaying": False, "silenced": 60} for _ in range(5)]
    se += [{"_ev": "s14.jetpack", "why": "periodic", "swimHeld": False, "jetpackRunning": True, "idleLoopPlaying": True}]   # flying: SE's own
    se += [{"_ev": "s14.muffle", "why": "periodic", "mixActive": True, "inRoom": False, "polish": True, "route": "arcade", "effect": EFFECT, "plays": 14,
            "modifiable": 12, "applied": 12, "gui": 2, "guiMuffled": 2, "sampleCutoffHz": float(C["UnderwaterCutoffHz"]), "reverbAvailable": True, "reverbOn": True,
            "mode": "arcade" if i % 2 == 0 else "realistic", "chainPlays": 12, "chainOne": 12, "chainTwo": 0, "chainZero": 0, "recordings": 0,
            "cueSwaps": 0 if i % 2 == 0 else 3}
           for i in range(4)]
    # round 4: the left ear in third person (ship and character voices judged against the geometry)
    se += [{"_ev": "s14.pan", "why": "periodic", "firstPerson": False, "voices": 3, "voices3D": 2, "judged": 2, "oneSidedCentred": 0, "signFlips": 0,
            "rawOneSidedCentred": 1, "stereo3D": 60, "fixed": 60, "mismatch": 0, "listener": "read", "listenerFrontDeg": 0.0, "listenerTopDeg": 0.0,
            "ownShip": 142823253881049946, "ownShip2D": 3, "ownShip3D": 0}
           for _ in range(4)]
    se += [{"_ev": "s14.muffle", "why": "periodic", "mixActive": False, "eyeUnder": False, "inRoom": False, "polish": True, "route": "arcade", "plays": 9,
            "modifiable": 8, "applied": 0, "sampleCutoffHz": round(cutoff_hz(1.0), 1)} for _ in range(2)]
    # ext s14 audio 5: a dive and a float at the surface (the eye bobbing through the waterline band): the decision on the camera's side
    se += [{"_ev": "s14.eye", "why": "periodic", "eyeY": y, "camY": y, "eyeUnder": y < 0, "muffle": y < 0, "mixActive": y < -0.05, "inRoom": False,
            "polish": True, "frames": 60, "judged": 52, "mismatch": 1 if k % 2 else 0, "crossings": 1, "mutantS14M10": False}
           for k, y in enumerate((-2.0, -0.12, 0.4, -0.15, 0.5, -0.3))]
    se += [{"_ev": "s14.weapons", "why": "periodic", "waterMod": "found", "weaponConfigs": 20, "functionalUnderwater": 20,
            "weaponsUnderwater": 4, "enabledUnderwater": 4, "turnedOnTotal": 2}]
    # the default mode: SE's own cue per type with the envelope
    se += [{"_ev": "s14.thrusters", "why": "periodic", "mixActive": True, "registered": 6, "submerged": 6, "covered": 0, "coveredQuiet": 0,
            "seAudible": 6, "snMode": "se", "hydrogenSource": "se", "ionSource": "se", "atmosphericSource": "se"} for _ in range(3)]
    se += _engine_rows("se", ("hydrogen", "ion", "atmospheric"))
    # then /de sn s14 engine subnautica: hydrogen and ion covered (faded on SE), atmospheric stays SE's
    se += [{"_ev": "s14.thrusters", "why": "periodic", "mixActive": True, "registered": 6, "submerged": 6, "covered": 4, "coveredQuiet": 4,
            "seAudible": 2, "snMode": "subnautica", "hydrogenSource": "subnautica", "ionSource": "subnautica", "atmosphericSource": "se",
            "shipCovered": 2, "shipCoveredAudible": 0}
           for _ in range(3)]
    sn = [{"_ev": "s14.listener", "scene": "Player/camPivot", "sceneOffsetM": 1.62, "sceneAngleDeg": 40.0, "readOffsetM": 0.0,
           "readAngleDeg": 0.0, "overridden": True} for _ in range(6)]
    sn += _engine_rows("subnautica", ("hydrogen", "ion"))
    sn += [{"_ev": "s14.jets", "aboveThrusters": 3, "jets": 3, "knob": 1.0, "on": True, "oldFlames": 0, "kitSkipped": 180, "particles": 240, "visible": 2,
            "material": "Legacy Shaders/Particles/Additive",
            "samples": ";".join("%s,%s,%s,%s" % (i, r, round(jet_length(i, r, ls), 4), ls) for i, r, ls in ((0.2, 1.2, 1.15), (0.9, 2.5, 1.3), (0.0, 0.3, 1.15)))}
           for _ in range(4)]
    return se, sn

JETS_POSE = {"cam": [0.0, 16.0, -28.0], "fwd": [0.0, -9.0, 28.0], "up": [0.0, 1.0, 0.0], "vfovDeg": 70.0,
             "flames": [{"kind": k, "at": [x, 11.0, 0.0], "dir": [0.0, -1.0, 0.0], "boxLenM": 10.0, "boxHalfM": 1.8}
                        for k, x in (("hydrogen", -7.0), ("ion", 0.0), ("atmospheric", 7.0))]}

def _jets_pair(folder, missing=None, w=1280, h=720):
    """A synthetic capture pair (the sea with three bright cones on the 'on' picture, one per kind but `missing`) and its pose."""
    sea = (90, 110, 130)
    ctl = bytearray(sea * (w * h))
    on = bytearray(ctl)
    for fl in JETS_POSE["flames"]:
        if fl["kind"] == missing:
            continue
        box = flame_box(JETS_POSE, fl, w, h)
        x0, y0, x1, y1 = box
        cx, half = (x0 + x1) // 2, max(2, (x1 - x0) // 6)
        for y in range(y0 + 10, y1 - 10):
            for x in range(cx - half, cx + half):
                o = (y * w + x) * 3
                on[o:o + 3] = bytes((250, 205, 150))
    p_on, p_ctl = os.path.join(folder, "jets-on.png"), os.path.join(folder, "jets-control.png")
    with open(p_on, "wb") as f:
        f.write(CP.encode_png(CP.Image(w, h, on)))
    with open(p_ctl, "wb") as f:
        f.write(CP.encode_png(CP.Image(w, h, ctl)))
    return {"on": p_on, "control": p_ctl, "pose": JETS_POSE}

def _mutate(name, se, sn):
    se = [dict(r) for r in se]
    sn = [dict(r) for r in sn]
    for r in se:
        if name == "S14.M1" and r["_ev"] == "s14.jetpack" and r.get("swimHeld"):
            r["idleLoopPlaying"] = True        # the idle loop started in the air goes on
        if name == "S14.M2" and r["_ev"] == "s14.muffle":
            r["applied"] = 0                    # an Arcade world: SE's effect path never runs
            r["sampleCutoffHz"] = round(cutoff_hz(1.0), 1)
            if "chainPlays" in r:
                r["chainZero"], r["chainOne"] = r["chainPlays"], 0   # no low-pass at all
            r["guiMuffled"] = r.get("guiMuffled", 0)   # the GUI postfix is not the Arcade route: unchanged
        if name == "S14.M3" and r["_ev"] == "s14.weapons":
            r["functionalUnderwater"] = 0
            r["enabledUnderwater"] = 0           # the Water Mod switches them off again
        if name == "S14.M8" and r["_ev"] == "s14.pan":
            r["oneSidedCentred"] = r.get("rawOneSidedCentred", 1) or 1   # SE's own matrix kept: the left ear
            r["ownShip3D"], r["ownShip2D"] = r.get("ownShip2D", 0), 0     # the own ship back on SE's 3D at its grid centre
        if name == "S14.M10" and r["_ev"] == "s14.eye":
            # the view's mix again: the waterline band (-0.20.. -0.05 m) and the 300 ms ramp off the eye's side
            r["muffle"] = r["mixActive"]
            r["mismatch"] = 18
        if name == "S14.M9" and r["_ev"] == "s14.muffle" and "chainPlays" in r:
            r["chainTwo"], r["chainOne"], r["recordings"] = r["chainPlays"], 0, r["chainPlays"]   # the effect over a Real* recording
        if name == "S14.M7" and r["_ev"] == "s14.thrusters":
            r["coveredQuiet"] = 0                # SE's own sound kept over Subnautica's loop
            r["shipCoveredAudible"] = r.get("shipCovered", 0)
        if name == "S14.M7" and r["_ev"] == "s14.engine":
            if r["why"] == "attack":
                r["attackMs"] = 0.0              # SE's own level at once (no envelope)
            if r["why"] == "stopped":
                r["releaseMs"] = 0.0
            if r["why"] == "periodic":
                r["volumeRead"] = 1.0
    for r in sn:
        if name == "S14.M4" and r["_ev"] == "s14.listener":
            r["readOffsetM"], r["readAngleDeg"], r["overridden"] = r["sceneOffsetM"], r["sceneAngleDeg"], False
        if name == "S14.M5" and r["_ev"] == "s14.engine":
            if r["why"] == "attack":
                r["attackMs"] = 0.0
            if r["why"] == "stopped":
                r["releaseMs"] = 0.0
            if r["why"] == "periodic":
                r["volumeRead"] = 1.0               # S7's constant level
        if name == "S14.M6" and r["_ev"] == "s14.jets":
            r["jets"], r["samples"] = 0, ""
            r["oldFlames"], r["kitSkipped"] = 180, 0      # scene-above's yellow puffs back
    return se, sn

def selftest() -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S14 analyzer self-test (tests/s14/analyze_s14.py --selftest)")
    text = canonical_text()
    check(fnv1a64(text.encode("ascii")) == PINNED_HASH, "twin hash 0x%016x = pinned" % fnv1a64(text.encode("ascii")))
    check(text.count("\n") == PINNED_LINES and len(text) == PINNED_BYTES, "%d lines, %d bytes" % (text.count("\n"), len(text)))
    cs = cs_pinned_hash()
    check(cs == PINNED_HASH, "the C# source pins the same hash (%s)" % ("0x%016x" % cs if cs is not None else "unreadable"))
    check(abs(cutoff_hz(xaudio_frequency(750)) - 750) < 1e-6 and abs(cutoff_hz(xaudio_frequency(C["UnderwaterCutoffHz"])) - 1800) < 1e-6
          and cutoff_hz(1.0) > C["DryCutoffMinHz"], "cutoff readback (Wire S14ConventionsTests)")
    check(matrix_one_sided(1.4, 0.0) and not matrix_one_sided(0.5, 0.4) and not matrix_one_sided(0, 0) and pan_centred(0.2) and not pan_centred(0.35)
          and pan_sign_agrees(0.1, -0.9) and not pan_sign_agrees(0.8, -0.2), "pan rules (Wire Round4_*)")
    check(abs(engine_gain(0) - 0.25) < 1e-12 and abs(engine_gain(1) - 1.0) < 1e-12 and engine_gain(0.2) < 0.35, "gain curve")
    lv, n = 0.0, 0
    while lv < 1.0:
        lv, n = step(lv, 1.0, 1000.0 / 60.0, 200, 300), n + 1
    check(n == 12, "attack 200 ms = 12 frames at 60 fps")
    check(abs(jet_length(1, 0.805) - 9.5 * 1.15 * 0.805) < 1e-9 and abs(jet_length(0, 0.5, 1.3) - 9.5 * 1.3 * 0.5 * 0.3) < 1e-9 and jet_length(1, 1, 0, 0) == 0.0,
          "jet length (Wire S14ConventionsTests: S7p's flame at the jet's strength)")
    check(visible_min_px(1440) == 400 and visible_min_px(720) == 100, "the visible threshold scales with the capture's rows")
    import tempfile
    jd = tempfile.mkdtemp(prefix="s14-jets-")
    jets = _jets_pair(jd)
    se, sn = _clean()
    rows = evaluate_events(se, sn, jets)
    st = {r["id"]: r["status"] for r in rows}
    check(all(s in ("PASS", "INFO") for s in st.values()), "clean session: every row PASS (%s)" % ", ".join("%s=%s" % kv for kv in st.items() if kv[1] != "PASS"))
    for name, red in sorted(MUTANT_ROWS.items()):
        se2, sn2 = _mutate(name, se, sn)
        st2 = {r["id"]: r["status"] for r in evaluate_events(se2, sn2, jets)}
        reds = sorted(k for k, v in st2.items() if v == "FAIL")
        check(reds == sorted(red), "%s red exactly on %s (got %s)" % (name, ",".join(red), ",".join(reds) or "none"))
    st3 = {r["id"]: r["status"] for r in evaluate_events([], [])}
    check(all(st3.get(k) == "MISSING" for k in ("S14.N1", "S14.N2", "S14.N2C", "S14.N3", "S14.N4L", "S14.N4P", "S14.N4E", "S14.N4S", "S14.N5", "S14.N5O", "S14.N5V")),
          "an empty session is MISSING")
    # round 5: the capture pair judges the flames; the ion one missing (the jets of the 16:49 session: on, counted, unseen) is red
    st_v = {r["id"]: r for r in evaluate_events(se, sn, jets)}
    check(st_v["S14.N5V"]["status"] == "PASS", "N5V: three flames seen (%s)" % st_v["S14.N5V"]["measured"])
    jets_bad = _jets_pair(jd, missing="ion")
    st_b = {r["id"]: r for r in evaluate_events(se, sn, jets_bad)}
    check(st_b["S14.N5V"]["status"] == "FAIL" and "ion" in st_b["S14.N5V"]["measured"], "N5V: an unseen flame is red (%s)" % st_b["S14.N5V"]["measured"])
    check(st_b["S14.N5"]["status"] == "PASS", "the rows alone cannot see it (N5 green while N5V is red)")
    for pth in glob.glob(os.path.join(jd, "*")):
        os.remove(pth)
    os.rmdir(jd)
    # s14 jets off): the kit's puffs are back and not judged
    se10, sn10 = _clean()
    for r in sn10:
        if r["_ev"] == "s14.jets":
            r["on"], r["oldFlames"], r["jets"] = False, 120, 0
    st10 = {r["id"]: r["status"] for r in evaluate_events(se10, sn10)}
    check(st10.get("S14.N5O") == "MISSING", "jets off by the owner: N5O not judged (the kit as before)")
    # round 4: a listener turned away from the camera fails N4P even with clean matrices
    se11, sn11 = _clean()
    for r in se11:
        if r["_ev"] == "s14.pan":
            r["listenerFrontDeg"] = 12.0
    check({r["id"]: r["status"] for r in evaluate_events(se11, sn11)}.get("S14.N4P") == "FAIL", "SE's listener off the camera turns N4P red")
    # round 4 (pan.own): the own ship in 3D in third person alone turns N4P red; first person is not judged for it
    se12, sn12 = _clean()
    for r in se12:
        if r["_ev"] == "s14.pan":
            r["ownShip3D"], r["ownShip2D"] = 3, 0
    check({r["id"]: r["status"] for r in evaluate_events(se12, sn12)}.get("S14.N4P") == "FAIL", "the own ship 3D in third person turns N4P red")
    se13, sn13 = _clean()
    for r in se13:
        if r["_ev"] == "s14.pan":
            r["firstPerson"], r["judged"], r["ownShip3D"] = True, 0, 2
    check({r["id"]: r["status"] for r in evaluate_events(se13, sn13)}.get("S14.N4P") == "MISSING", "first person with nothing judged: N4P MISSING")
    #  a scene listener already on the camera makes N4L INCONCLUSIVE (not required), never PASS
    se8, sn8 = _clean()
    for r in sn8:
        if r["_ev"] == "s14.listener":
            r["sceneOffsetM"], r["sceneAngleDeg"] = 0.01, 0.5
    st8 = {r["id"]: r for r in evaluate_events(se8, sn8)}
    check(st8["S14.N4L"]["status"] == "INCONCLUSIVE" and not st8["S14.N4L"]["required"], "the scene listener on the camera: N4L INCONCLUSIVE")
    #  a gun still off under water turns N3E red although the configs are functional
    se9, sn9 = _clean()
    for r in se9:
        if r["_ev"] == "s14.weapons":
            r["enabledUnderwater"] = 3
    st9 = {r["id"]: r["status"] for r in evaluate_events(se9, sn9)}
    check(st9.get("S14.N3E") == "FAIL" and st9.get("S14.N3") == "PASS", "a gun still off under water: N3E red, N3 green")
    # the default mode alone (SE's own sound, no Subnautica loop): the envelope judged on SE's rows, N4S not required
    se7, sn7 = _clean()
    se7 = [r for r in se7 if not (r["_ev"] == "s14.thrusters" and r.get("covered"))]
    sn7 = [r for r in sn7 if r["_ev"] != "s14.engine"]
    st7 = {r["id"]: r for r in evaluate_events(se7, sn7)}
    check(st7["S14.N4E"]["status"] == "PASS" and st7["S14.N4V"]["status"] == "PASS" and st7["S14.N4S"]["status"] == "MISSING"
          and not st7["S14.N4S"]["required"] and verdict([r for r in st7.values() if r["id"] != "S14.N5V"]),
          "SE's own source alone: envelope judged, N4S not required (N5V needs the capture pair)")
    # the change row at the dive (the loop still ending) is not judged; a periodic loud row is
    se4, sn4 = _clean()
    se4.append({"_ev": "s14.jetpack", "why": "periodic", "swimHeld": True, "jetpackRunning": False, "idleLoopPlaying": False, "runLoopPlaying": True})
    check({r["id"]: r["status"] for r in evaluate_events(se4, sn4)}.get("S14.N1") == "FAIL", "the jetpack run loop alone turns N1 red")
    # a sound muffled above water is a failure too
    se5, sn5 = _clean()
    for r in se5:
        if r["_ev"] == "s14.muffle" and not r["mixActive"]:
            r["applied"] = 3
    # ext s14 audio 5: the eye under the sea while the view's mix is off (entering the waterline band) is muffled, not a failure
    se14, sn14 = _clean()
    se14.append({"_ev": "s14.muffle", "why": "periodic", "mixActive": False, "eyeUnder": True, "inRoom": False, "polish": True, "route": "arcade", "plays": 4,
                 "modifiable": 4, "applied": 4, "sampleCutoffHz": float(C["UnderwaterCutoffHz"])})
    check({r["id"]: r["status"] for r in evaluate_events(se14, sn14)}.get("S14.N2") == "PASS", "the eye under water with the mix off: muffled (N2 by the eye)")
    check({r["id"]: r["status"] for r in evaluate_events(se5, sn5)}.get("S14.N2") == "FAIL", "SE muffled above water turns N2 red")
    # the folder reader takes Subnautica's nested rows and SE's flat ones
    import tempfile
    d = tempfile.mkdtemp(prefix="s14-selftest-")
    try:
        with open(os.path.join(d, "sn-x.jsonl"), "w", encoding="utf-8") as f:
            for _ in range(5):
                f.write(json.dumps({"t": "x", "event": "s14.listener", "ev": "s14.listener", "data": {"scene": "Player", "readOffsetM": 0.0, "readAngleDeg": 0.0, "sceneOffsetM": 1.5, "sceneAngleDeg": 30.0}}) + "\n")
        with open(os.path.join(d, "se-x.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"t": "x", "ev": "s14.weapons", "why": "periodic", "waterMod": "found", "weaponConfigs": 20, "functionalUnderwater": 20}) + "\n")
        st6 = {r["id"]: r["status"] for r in evaluate(d)}
        check(st6.get("S14.N4L") == "PASS" and st6.get("S14.N3") == "PASS", "folder reader (nested sn rows, flat se rows)")
    finally:
        for p in glob.glob(os.path.join(d, "*")):
            os.remove(p)
        os.rmdir(d)
    ok = all(results)
    print("S14 ANALYZE SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
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
    print("S14 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
