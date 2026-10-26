"""S15 oracle table (ROADMAP §14 "### S15 Gameplay and immersion polish" and its addenda; docs/S15-BRIEF.md; CONTRACT §5.4

 python -I tests/s15/analyze_s15.py <tests/out/s15/<stamp>> [--lenient] [--json]
 python -I tests/s15/analyze_s15.
 python -I tests/s15/analyze_s15.py --selftest

Reads a folder written by `launcher.py s15` (s15-session.jsonl; se-*.jsonl, sn-*.jsonl; captures/*.png) and judges, with the
Python twin's tables and rules (tools/s15/s15_ref.py; never the plugins' own numbers):

 S15.PIN the SE plugin's conventions hash (s15.site) is the twin's pinned hash
 S15.ABOARD every s15.aboard grid was entered by a cockpit seat, an airtight room or a station's box (never a ship's box: a door,
 a ladder, a deck); the launcher's grids: the ship and the subs by "seat", the room by "room", the deck never aboard
 S15.VOICE the twin's gate (rising edges, rearm, S16's cooldown, an edge met while SE's audio is live skipped for good, a
 boarding's power baseline) replayed over SE's own s15.cond rows gives exactly SE's s15.voice decisions; the scripted
 voice part played each of the four lines at least once
 S15.WELCOME a welcome is decided at the boarding itself (the condition's own rising edge, never later: no dive greeting), the
 Cyclops's for a large grid or a station and the Seamoth's for a small grid; the scripted part: the ship and the room
 greeted by the Cyclops, the sub by the Seamoth, the sub boarded with SE's audio live (s15_surface) skipped and never
 greeted after the dive
 S15.POWER every EnginePowerUp at the aboard grid's power edge (powered now, not on the scan before) and every EnginePowerDown at
 its switch going off from a powered grid: never on thrust; the scripted part: up by the fuel, the switch and a
 reactor placed with fuel, down by the switch (the cause read from SE's s15.power rows within 5 s before the edge)
 S15.MIX every event SE sent (played or forced) reaches Subnautica as one line (S16SeqReader: increments missed in one
 frame are one line; a full queue drops the newest): played at S16's voice volume, or refused because its own
 C5 says SE's audio is live; no line without an SE event; the FMOD paths (S15.PATHS, INFO)
 S15.S2 Subnautica never started a line while its C5 snBusTarget said SE's audio is live (the S2 rule)
 S15.MUSIC the audio groups (s15.mix): settled above water the groups are on, bus:/ unmuted, the music and the voice at their
 above-water level (0.6 unless tuned) within 0.02, the effects at 0; settled under water all at 1; a surface
 crossing's music fade from the under-water level takes 0.7-1.4 x Mix.CrossfadeMs (a fade starting from a partial
 level, the camera back above water within a fade, turns around in place: counted apart, not timed); rows under S2's own mute (muteWhy no live SE, SE not
 foreground, quitting: every sound off on both sides) are counted apart, not judged
 S15.LINE every line started under water that crossed the surface (s15.voiceend) played to its end, never muted, the voice group
 never under its above-water level - 0.02; the scripted part crossed one
 S15.STORY no Subnautica story goal ran (every s15.story row skipped), no PDA line of a story goal started (s15.pda), the Aurora
 held (s15.aurora: never running, its countdown and explosion sounds never playing); the scripted trigger of the
 Aurora's first warning (s15_story) skipped and silent
 S15.ALERT Subnautica's own LowOxygenAlert is there and enabled (S15 no longer touches it)
 S15.MOONS, S15.SKYLIST, S15.SKYDIR, S15.SKYSIZE (S15.SKYPX, S15.MOONS.READ INFO), S15.WATER (INFO), S15.BAND, S15.BAND.FAR, S15.AIR,
 S15.ORBIT (S15.LIMB INFO): the sky bodies and the planet from orbit to the seabed, unchanged (see each check)
 S15.SEAM SE's air captures at SE's noon (air-30, air-5): no brightness step across the sea above SEAM_MAX_LUMA (12, sRGB luma;
 sea_step: the largest difference of the median sea luma between two windows either side of a column or row);
 S15.SEAM.PAIR (INFO): the pair at the site's sunrise with the Water Mod's own specular, the camera 4 km east between
 the two (the step moves with the ground: the mod's terminator on its quads), and the planet's specular 0 (no step)
 The kept picks: S15.NIGHT (the block lights 0.4 + 0.6 x the day's light; a night row seen), S15.SPLASH (every surfacing after a
 dive splashed the lens), S15.SONAR (every SE ping played with the screen pulse), S15.SEABASE (the base hum loops and the room
 light in an airtight room under the sea), S15.FISH (a fast ship section scares the fish near it)
 S15.Mx.red a seeded failure turns exactly its rows red (rows a mutant may also touch are listed in MAY_AFFECT)
"""
from __future__ import annotations

import glob
import json
import math
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tools", "s15"), os.path.join(ROOT, "tests", "s7t")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C          # noqa: E402 (row, table, verdict, jsonl reader)
import s15_ref as T            # noqa: E402 (the twin)
import band_compare as BC      # noqa: E402 (compare.py bands: horizon finder, bands, judge)
import colour_patch as CP      # noqa: E402 (the image loader / PNG writer)

row = C.row
MUTANT_ROWS = {"S15.M1": ("S15.VOICE",), "S15.M2": ("S15.ABOARD",), "S15.M3": ("S15.SKYDIR",), "S15.M4": ("S15.AIR", "S15.ORBIT"),
               "S15.M5": ("S15.MOONS",), "S15.M6": ("S15.SKYSIZE",), "S15.M7": ("S15.S2",),
               "S15.M11": ("S15.NIGHT",), "S15.M12": ("S15.SPLASH",), "S15.M13": ("S15.SONAR",), "S15.M14": ("S15.SEABASE",), "S15.M15": ("S15.FISH",),
               "S15.M16": ("S15.POWER",), "S15.M17": ("S15.WELCOME", "S15.VOICE"), "S15.M18": ("S15.WELCOME",),
               "S15.M19": ("S15.MUSIC", "S15.LINE"), "S15.M20": ("S15.STORY",)}
# rows a mutant may turn red besides its own (S15.M4's see-through sea also shows at the band)
MAY_AFFECT = {"S15.M4": ("S15.BAND", "S15.BAND.FAR", "S15.SEAM")}
SE_MUTANTS = ("S15.M1", "S15.M2", "S15.M4", "S15.M16", "S15.M17", "S15.M18")
SN_MUTANTS = ("S15.M3", "S15.M5", "S15.M6", "S15.M7", "S15.M11", "S15.M12", "S15.M13", "S15.M14", "S15.M15", "S15.M19", "S15.M20")
FOUR_LINES = ("Welcome", "EnginePowerUp", "EnginePowerDown", "SmallWelcome")
WELCOMES = (T.WELCOME, T.SMALL_WELCOME)
POWER_LINES = (T.POWER_UP, T.POWER_DOWN)
LEVEL_TOL = 0.02
CAUSE_WINDOW_MS = 5000
# a moon capture is judged at night only: the sky around the centre this dark at most (run 20261024-041144: L146 at a "midnight"
# that was noon on Subnautica's side)
MOON_NIGHT_LUMA = 60.0
EVENT_IDS = {v[1]: v[0] for v in T.VOICES}
EVENT_NAMES = {v[0]: v[1] for v in T.VOICES}
MIN_PAIRS = 3
ORBIT_KM = tuple(km for km, _c, _l in T.ORBIT)
AIR_KM = tuple(T.AIR_KM)
DISC_LUMA_MIN = 20.0
# the moon captures (format moons.hide): the centre circle and the ring of sky around it, per mille of the frame height; a
# direction below this elevation is not judged (the moon would be under the horizon)
MOON_CENTRE, MOON_RING = 0.08, (0.30, 0.45)
MOON_MIN_SIN = math.sin(math.radians(5.0))
# the sky-size captures (format sky.size, information): a pixel differs from the border's median luma by this much
SKYPX_LUMA = 12.0
RIM_OUTER = 1.10   # the rim beyond the disc, as a multiple of its radius (S15.LIMB)

# ======================================================================================================================
# reading
# ======================================================================================================================
def parse_rows(records):
    """(name, data) of every s15.* record: SE rows are flat ({"ev": "s15.x",...}), Subnautica's carry "data"."""
    out = []
    for r in records:
        name = r.get("ev") or r.get("event")
        if not isinstance(name, str) or not name.startswith("s15."):
            continue
        data = r.get("data") if isinstance(r.get("data"), dict) else {k: v for k, v in r.items() if k not in ("ev", "event")}
        out.append((name, data))
    return out

class Session:
    def __init__(self, folder=None, se_logs=(), sn_logs=(), captures=None):
        self.dir = folder
        self.session = C.read_jsonl(os.path.join(folder, "s15-session.jsonl")) if folder else []
        se = list(se_logs) + (sorted(glob.glob(os.path.join(folder, "se-*.jsonl"))) if folder else [])
        sn = list(sn_logs) + (sorted(glob.glob(os.path.join(folder, "sn-*.jsonl"))) if folder else [])
        self.se = parse_rows(C.read_jsonl(se))
        self.sn = parse_rows(C.read_jsonl(sn))
        self.start = next((e for e in self.session if e.get("ev") == "start"), {}) or {}
        self.parts = list(self.start.get("parts") or [])
        self.mutants = list(self.start.get("mutants") or [])
        self.mode = self.start.get("mode")
        self.captures = {}
        self.cap_ev = {}
        cap_dir = captures or (os.path.join(folder, "captures") if folder else None)
        for e in self.session:
            if e.get("ev") == "s15_capture" and e.get("path"):
                self.captures[e.get("kind")] = e["path"]
                self.cap_ev[e.get("kind")] = e
        if cap_dir and os.path.isdir(cap_dir):
            for p in glob.glob(os.path.join(cap_dir, "*.png")):
                k = os.path.splitext(os.path.basename(p))[0]
                self.captures.setdefault(k, p)
        self.grids = {}
        for e in self.session:
            if e.get("ev") == "s15_grid" and e.get("grid"):
                self.grids[str(e["grid"])] = e.get("label")

    def rows(self, side, name):
        return [d for n, d in (self.se if side == "se" else self.sn) if n == name]

    def events(self, name):
        return [e for e in self.session if e.get("ev") == name]

    def cap(self, kind):
        p = self.captures.get(kind)
        return p if p and os.path.isfile(p) else None

    def ran(self, *parts):
        return not self.parts or any(p in self.parts for p in parts)

    def scripted(self, part):
        return bool(self.parts) and part in self.parts

def _f(v, d=float("nan")):
    try:
        return float(v)
    except (TypeError, ValueError):
        return d

def _gid(d, key="grid"):
    v = d.get(key + "Str", d.get(key))
    if v is None:
        return "0"
    if isinstance(v, float):
        v = int(v)
    return str(v)

def median(vals):
    v = sorted(vals)
    if not v:
        return float("nan")
    n = len(v)
    return v[n // 2] if n % 2 else 0.5 * (v[n // 2 - 1] + v[n // 2])

def _rgb(c):
    return "-" if c is None else "(%d,%d,%d)" % tuple(round(v) for v in c)

# ======================================================================================================================
# the checks
# ======================================================================================================================
def check_pin(S, rows):
    sites = S.rows("se", "s15.site")
    if not sites:
        row(rows, "S15.PIN", "MISSING", "no s15.site row", "0x%016x" % T.PINNED_HASH, "the SE plugin's conventions hash", "selftest")
        return
    hashes = sorted({str(d.get("hash")) for d in sites})
    ok = hashes == ["0x%016x" % T.PINNED_HASH]
    row(rows, "S15.PIN", "PASS" if ok else "FAIL", ",".join(hashes), "0x%016x" % T.PINNED_HASH, "the SE plugin's conventions hash is the twin's", "selftest")

def check_aboard(S, rows):
    ab = [d for d in S.rows("se", "s15.aboard") if _gid(d) != "0"]
    if not ab:
        row(rows, "S15.ABOARD", "MISSING", "no s15.aboard row", "seat|room|station", "aboard by a cockpit seat, an airtight room or a station", "aboard")
        return
    bad = [d for d in ab if d.get("how") not in ("seat", "room", "station")]
    want = {"ship": "seat", "sub": "seat", "sub2": "seat", "room": "room"}
    wrong = []
    for gid, label in S.grids.items():
        hows = {d.get("how") for d in ab if _gid(d) == gid}
        if label == "deck" and hows:
            wrong.append("deck aboard by %s" % ",".join(sorted(str(h) for h in hows)))
        elif label in want and want[label] not in hows:
            wrong.append("%s never aboard by %s" % (label, want[label]))
    ok = not bad and not wrong
    row(rows, "S15.ABOARD", "PASS" if ok else "FAIL", "%d entries, %d by a box%s" % (len(ab), len(bad), ("; " + "; ".join(wrong)) if wrong else ""),
        "seat|room|station; deck never", "aboard only by a cockpit seat, an airtight room or a station (no door, ladder or deck)", "aboard")

def _row_live(d):
    return T.sn_audio_live(_f(d.get("snBus"), 0.0)) and d.get("voiceOn", True) is not False

def replay_gate(conds):
    """The twin's gate over SE's condition changes: [(ev, grid, ms, result)] of every non-NONE decision.

 SE steps the gate every scan; between rows nothing changes but the time, so the decisions come only at a condition's change
 (its row). At one ms the conditions going off first, then those coming on and the baselines.), so SE's live changes play no part."""
    g = T.Gate()
    out = []
    timeline = sorted(((int(_f(d.get("ms"), 0)), 1 if d.get("on") else 0, k, d) for k, d in enumerate(conds)), key=lambda e: (e[0], e[1], e[2]))
    for ms, _o, _k, d in timeline:
        ev = int(_f(d.get("evId"), 0))
        grid = _gid(d)
        live = _row_live(d)
        if d.get("baseline"):
            g.baseline(ev, grid, bool(d.get("on")), ms)
        elif d.get("on"):
            r = g.step(ev, grid, True, ms, live)
            if r != T.NONE:
                out.append((ev, grid, ms, r))
        else:
            last = d.get("lastOnMs")
            if last is not None:
                lo = int(_f(last))
                r = g.step(ev, grid, True, lo, live)   # the last sample the condition was on
                if r != T.NONE:
                    out.append((ev, grid, lo, r))
            g.step(ev, grid, False, ms, live)
    return out

RESULT_IDS = {"played": T.PLAYED, "cooldown": T.COOLDOWN, "seAudio": T.SEAUDIO}

def _decisions(S, evs=None):
    return [d for d in S.rows("se", "s15.voice") if d.get("result") != "forced" and (evs is None or int(_f(d.get("evId"), 0)) in evs)]

def check_voice(S, rows):
    conds = S.rows("se", "s15.cond")
    voices = _decisions(S)
    if not conds and not voices:
        row(rows, "S15.VOICE", "MISSING", "no s15.cond / s15.voice rows", "the twin's gate", "the voice gate's decisions", "voice")
        return
    want = sorted(replay_gate(conds))
    have = sorted((int(_f(d.get("evId"), 0)), _gid(d), int(_f(d.get("ms"), 0)), RESULT_IDS.get(d.get("result"), -1)) for d in voices)
    missing = [w for w in want if w not in have]
    extra = [h for h in have if h not in want]
    played = {EVENT_NAMES.get(ev, str(ev)) for ev, _g, _ms, r in have if r == T.PLAYED}
    baselines = sum(1 for d in conds if d.get("baseline"))
    scripted = S.mode == "S15b" and S.scripted("voice")
    lacking = sorted(set(FOUR_LINES) - played) if scripted else []
    ok = not missing and not extra and not lacking
    detail = "%d decisions (%d played: %s; %d skipped with SE's audio, %d baselines)" % (
        len(have), sum(1 for h in have if h[3] == T.PLAYED), ",".join(sorted(played)) or "none", sum(1 for h in have if h[3] == T.SEAUDIO), baselines)
    if missing or extra:
        detail += "; twin-only %s; SE-only %s" % ([(EVENT_NAMES.get(e, e), r, m) for e, _g, m, r in missing[:3]], [(EVENT_NAMES.get(e, e), r, m) for e, _g, m, r in extra[:3]])
    if lacking:
        detail += "; never played: %s" % ",".join(lacking)
    row(rows, "S15.VOICE", "PASS" if ok else "FAIL", detail, "replay == SE; the four lines played",
        "SE's voice decisions are the twin's gate (rising edge, rearm, cooldown, skipped while SE's audio is live, baseline)", "voice")

def _cond_on(S, ev, grid, ms):
    for d in S.rows("se", "s15.cond"):
        if d.get("on") and int(_f(d.get("evId"), 0)) == ev and _gid(d) == grid and int(_f(d.get("ms"), -1)) == ms:
            return d
    return None

def check_welcome(S, rows):
    dec = _decisions(S, WELCOMES)
    if not dec:
        row(rows, "S15.WELCOME", "MISSING", "no welcome decision", "at the boarding, by size", "welcome aboard on boarding only (never a dive)", "voice")
        return
    bad = []
    for d in dec:
        ev, grid, ms = int(_f(d.get("evId"), 0)), _gid(d), int(_f(d.get("ms"), 0))
        c = _cond_on(S, ev, grid, ms)
        if c is None:
            bad.append("%s on %s at %d ms after its boarding (a held line)" % (EVENT_NAMES[ev], S.grids.get(grid, grid), ms))
            continue
        big = bool(c.get("large")) or bool(c.get("station"))
        if (ev == T.WELCOME) != big:
            bad.append("%s on a %s grid (%s)" % (EVENT_NAMES[ev], "large" if big else "small", S.grids.get(grid, grid)))
    lacking = []
    if S.scripted("voice"):
        s15b = S.mode == "S15b"
        for label, ev in (("ship", T.WELCOME), ("room", T.WELCOME), ("sub", T.SMALL_WELCOME)):
            gids = [g for g, l in S.grids.items() if l == label]
            ok = any(int(_f(d.get("evId"), 0)) == ev and _gid(d) in gids and (d.get("played") or (not s15b and d.get("result") == "seAudio")) for d in dec)
            if gids and not ok:
                skipped = any(int(_f(d.get("evId"), 0)) == ev and _gid(d) in gids and d.get("result") == "seAudio" for d in dec)
                lacking.append("%s not greeted by %s%s" % (label, EVENT_NAMES[ev], " (skipped: SE's audio live at the boarding)" if skipped else ""))
        for g in [g for g, l in S.grids.items() if l == "sub2"]:
            mine = [d for d in dec if _gid(d) == g]
            if not any(d.get("result") == "seAudio" for d in mine):
                lacking.append("sub2's boarding with SE's audio live not skipped")
            if any(d.get("played") for d in mine):
                lacking.append("sub2 greeted after the dive")
    ok = not bad and not lacking
    row(rows, "S15.WELCOME", "PASS" if ok else "FAIL", "%d welcome decisions (%d played)%s%s" % (len(dec), sum(1 for d in dec if d.get("played")),
                                                                                               "; bad: %s" % "; ".join(bad[:3]) if bad else "",
                                                                                               "; %s" % "; ".join(lacking) if lacking else ""),
        "at the boarding; Cyclops large, Seamoth small", "welcome aboard only on boarding, never on a dive; the Seamoth's line on a small grid", "voice")

def _power_cause(S, grid, ms):
    """The latest change before a power edge (within CAUSE_WINDOW_MS) in SE's s15.power rows of that grid."""
    pr = sorted((d for d in S.rows("se", "s15.power") if _gid(d) == grid), key=lambda d: _f(d.get("ms"), 0))
    cause, prev = None, None
    for d in pr:
        t = _f(d.get("ms"), 0)
        if t > ms:
            break
        if prev is not None and t >= ms - CAUSE_WINDOW_MS and not d.get("baseline"):
            if int(_f(d.get("reactors"), 0)) > int(_f(prev.get("reactors"), 0)):
                cause = "reactor"
            elif _f(d.get("uraniumKg"), 0) > _f(prev.get("uraniumKg"), 0) + 1e-6:
                cause = "fuel"
            elif d.get("switchOn") and not prev.get("switchOn"):
                cause = "switch"
        prev = d
    return cause

def check_power(S, rows):
    dec = _decisions(S, POWER_LINES)
    if not dec:
        row(rows, "S15.POWER", "MISSING", "no engine decision", "power edges only", "engine powering up / shutting down on the grid's power state", "voice")
        return
    bad, causes, downs = [], [], 0
    for d in dec:
        ev, grid, ms = int(_f(d.get("evId"), 0)), _gid(d), int(_f(d.get("ms"), 0))
        c = _cond_on(S, ev, grid, ms)
        if c is None:
            bad.append("%s at %d ms without its condition's edge" % (EVENT_NAMES[ev], ms))
            continue
        if ev == T.POWER_UP:
            if not (c.get("powered") and not c.get("powerBefore")):
                bad.append("EnginePowerUp at %d ms with the grid %s before (thrust %.2f)" % (ms, "powered" if c.get("powerBefore") else "unpowered", _f(c.get("thrust"), 0)))
            elif d.get("played") or S.mode != "S15b":
                causes.append((S.grids.get(grid, grid), _power_cause(S, grid, ms)))
        else:
            if c.get("switchOn") or not c.get("powerBefore"):
                bad.append("EnginePowerDown at %d ms (switch %s, powered before %s)" % (ms, c.get("switchOn"), c.get("powerBefore")))
            elif d.get("played") or S.mode != "S15b":
                downs += 1
    lacking = []
    if S.scripted("voice"):
        have = {c for _l, c in causes}
        lacking += ["up by the %s" % c for c in ("fuel", "switch", "reactor") if c not in have]
        if downs < 1:
            lacking.append("down by the switch")
    ok = not bad and not lacking
    row(rows, "S15.POWER", "PASS" if ok else "FAIL", "%d decisions; up %s; down %d%s%s" % (len(dec), ",".join("%s:%s" % c for c in causes) or "none", downs,
                                                                                         "; bad: %s" % "; ".join(bad[:3]) if bad else "",
                                                                                         "; never: %s" % ", ".join(lacking) if lacking else ""),
        "power edges and the switch; never thrust", "engine powering up on the power from none to some, shutting down on the switch off", "voice")

def _sn_lines(S):
    out = []
    for d in S.rows("sn", "s15.voice"):
        ev = EVENT_IDS.get(d.get("event")) or int(_f(d.get("evId"), 0))
        out.append((ev, int(_f(d.get("seq"), 0)), d))
    return out

def check_mix(S, rows):
    sent = [(int(_f(d.get("evId"), 0)), int(_f(d.get("seq"), 0))) for d in S.rows("se", "s15.voice") if d.get("played") and int(_f(d.get("seq"), 0)) > 0]
    lines = _sn_lines(S)
    if not lines:
        row(rows, "S15.MIX", "MISSING", "no Subnautica s15.voice row (%d sent)" % len(sent), "one line per SE event", "Subnautica plays SE's events", "voice")
        return
    sent_set = set(sent)
    orphan = [(EVENT_NAMES.get(e, e), s) for e, s, _d in lines if (e, s) not in sent_set]
    by_ev = {}
    for e, s in sent:
        by_ev.setdefault(e, []).append(s)
    coalesced = max([int(_f(d.get("coalesced"), 0)) for _e, _s, d in lines] or [0])
    dropped = max([int(_f(d.get("dropped"), 0)) for _e, _s, d in lines] or [0])
    unheard = 0
    for e, seqs in by_ev.items():
        got = {s for ee, s, _d in lines if ee == e}
        unheard += sum(1 for s in seqs if s not in got)
    bad = [d for _e, _s, d in lines if not (d.get("played") or d.get("reason") in ("seAudio", "voice off"))]
    vol = T.V["VolumePermille"] / 1000.0
    wrong_vol = [d for _e, _s, d in lines if d.get("played") and abs(_f(d.get("volume")) - vol) > 1e-3]
    paths = sorted({"%s %s" % (d.get("event"), d.get("path")) for _e, _s, d in lines if d.get("played")})
    ok = not orphan and unheard <= coalesced + dropped and not bad and not wrong_vol
    row(rows, "S15.MIX", "PASS" if ok else "FAIL",
        "%d sent, %d lines (%d played), unheard %d <= coalesced %d + dropped %d%s%s%s" % (
            len(sent), len(lines), sum(1 for l in lines if l[2].get("played")), unheard, coalesced, dropped,
            "; orphan %s" % orphan[:3] if orphan else "", "; refused/missing %d" % len(bad) if bad else "",
            "; volume %d off" % len(wrong_vol) if wrong_vol else ""),
        "1 line per event, volume %.1f" % vol, "Subnautica plays one Cyclops/Seamoth line per SE event through S16's mix", "voice", paths=paths)
    sources = sorted({"%s=%s" % (d.get("event"), d.get("source")) for _e, _s, d in lines if d.get("played")})
    row(rows, "S15.PATHS", "INFO", ",".join(paths)[:160] or "none", "event:/sub/cyclops/*, the Seamoth's",
        "the FMOD paths Subnautica played (prefab data, logged at run time): %s" % "; ".join(sources)[:200], "voice", required=False)

def check_s2(S, rows):
    lines = _sn_lines(S)
    if not lines:
        row(rows, "S15.S2", "MISSING", "no Subnautica s15.voice row", "none while SE's audio is live", "the S2 rule on Subnautica's side", "voice")
        return
    live_bad = [d for _e, _s, d in lines if d.get("played") and not T.sn_audio_live(_f(d.get("snBus"), 0.0))]
    tested = sum(1 for _e, _s, d in lines if not T.sn_audio_live(_f(d.get("snBus"), 0.0)))
    row(rows, "S15.S2", "PASS" if not live_bad else "FAIL", "%d played while SE's audio was live (%d lines met SE's live audio)" % (len(live_bad), tested),
        "0", "Subnautica never starts a line while its C5 says SE's audio is live", "voice")

S2_OWN_MUTES = ("no live SE", "SE not foreground", "quitting")   # MixRules.ShouldMute's reasons other than the above-water one

def check_music(S, rows):
    mix = sorted(S.rows("sn", "s15.mix"), key=lambda d: _f(d.get("ms"), 0))
    if not mix:
        row(rows, "S15.MUSIC", "MISSING", "no s15.mix row", "music and voice live above water", "Subnautica's music across the surface", "voice")
        return
    settle = T.MX["CrossfadeMs"] + 500
    side, since = None, None
    above, under, bad, fades, s2_mutes, partial = [], [], [], [], [], []
    fade_from = None
    for d in mix:
        t = _f(d.get("ms"), 0)
        s = _f(d.get("s2Target"), 1.0) >= 0.5
        if s != side:
            side, since = s, t
            fade_from = t if not s else None
            m0 = _f(d.get("music"), 1.0)
            if fade_from is not None and m0 < 1.0 - LEVEL_TOL:
                # the camera went back above water before the music reached 1 (a dip shorter than the crossfade): the fade turns
 # around in place at the same rate (S2Rules.Crossfade), so its time is the swing's share of CrossfadeMs, not
 # all of it (run 20261024-150805: 0.69 to 0.6 in 625 ms); counted apart, never timed
                partial.append(m0)
                fade_from = None
        if fade_from is not None and abs(_f(d.get("music"), 1.0) - _f(d.get("musicAbove"), 0.6)) <= LEVEL_TOL:
            fades.append(t - fade_from)
            fade_from = None
        if t - since < settle:
            continue
        if d.get("muted") and str(d.get("muteWhy") or "") in S2_OWN_MUTES:
            s2_mutes.append(str(d.get("muteWhy")))   # S2's own mute (no live SE, SE not in front, quitting): every sound off, both sides
            continue
        if s:
            under.append(d)
            if _f(d.get("music"), 0) < 1 - LEVEL_TOL or _f(d.get("voice"), 0) < 1 - LEVEL_TOL:
                bad.append("under water music %.2f voice %.2f" % (_f(d.get("music")), _f(d.get("voice"))))
        else:
            above.append(d)
            m_ok = abs(_f(d.get("music"), -1) - _f(d.get("musicAbove"), 0.6)) <= LEVEL_TOL
            v_ok = abs(_f(d.get("voice"), -1) - _f(d.get("voiceAbove"), 0.6)) <= LEVEL_TOL
            if not (d.get("active") and m_ok and v_ok and _f(d.get("sfx"), 1) <= LEVEL_TOL and not d.get("muted")):
                bad.append("above water active %s muted %s sfx %.2f music %.2f/%.2f voice %.2f/%.2f (%s)" % (
                    d.get("active"), d.get("muted"), _f(d.get("sfx")), _f(d.get("music")), _f(d.get("musicAbove")), _f(d.get("voice")), _f(d.get("voiceAbove")), d.get("why")))
    lo, hi = 0.7 * T.MX["CrossfadeMs"], 1.4 * T.MX["CrossfadeMs"] + T.MX["RowMs"]
    slow = [f for f in fades if not (lo <= f <= hi)]
    if not above:
        row(rows, "S15.MUSIC", "MISSING", "%d rows, none settled above water" % len(mix), "music %.1f above water" % (T.MX["MusicAbovePermille"] / 1000.0),
            "Subnautica's music across the surface", "voice")
        return
    ok = not bad and not slow
    row(rows, "S15.MUSIC", "PASS" if ok else "FAIL", "%d settled above, %d under; fades %s ms%s%s%s" % (
        len(above), len(under), ",".join("%.0f" % f for f in fades[:4]) or "-", "; bad: %s" % bad[0] if bad else "", "; fade off %s" % slow[:2] if slow else "",
        "; %d rows under S2's own mute (%s)" % (len(s2_mutes), ",".join(sorted(set(s2_mutes)))) if s2_mutes else "") +
        ("; %d fades from a partial level (a dip shorter than the crossfade, from %s; not timed)" % (len(partial), ",".join("%.2f" % m for m in partial[:3])) if partial else ""),
        "groups on; music/voice at their level, effects 0 above water; fade %.0f-%.0f ms" % (lo, hi),
        "the music and the voice stay live across the surface (the effects follow the S2 rule)", "voice")

def check_line(S, rows):
    ends = S.rows("sn", "s15.voiceend")
    crossed = [d for d in ends if d.get("crossed") and d.get("startedUnder")]
    if not crossed:
        row(rows, "S15.LINE", "MISSING", "%d lines ended, none started under water and crossed the surface" % len(ends), "a line completes above water",
            "a line started under water plays to its end above water", "voice")
        return
    floor = min([_f(d.get("voiceAbove"), 0.6) for d in S.rows("sn", "s15.mix")] or [T.MX["VoiceAbovePermille"] / 1000.0]) - LEVEL_TOL
    bad = [d for d in crossed if not d.get("complete") or d.get("muted") or _f(d.get("voiceMin"), 0) < floor]
    row(rows, "S15.LINE", "PASS" if not bad else "FAIL", "%d crossed: %s" % (len(crossed), "; ".join("%s %s min %.2f%s" % (
        d.get("event"), "complete" if d.get("complete") else "cut at %s of %s ms" % (d.get("posMs"), d.get("lenMs")), _f(d.get("voiceMin")),
        " MUTED" if d.get("muted") else "") for d in crossed[:3])),
        "complete, unmuted, voice >= %.2f" % floor, "a line started under water plays to its end above water (the voice group live)", "voice")

def check_story(S, rows):
    story = S.rows("sn", "s15.story")
    pda = S.rows("sn", "s15.pda")
    aurora = S.rows("sn", "s15.aurora")
    trig = [e.get("key") for e in S.events("s15_story") if e.get("key")]
    if not story and not aurora and not trig:
        row(rows, "S15.STORY", "MISSING", "no s15.story / s15.aurora row", "story off", "Subnautica's story lines off", "voice")
        return
    keys = {str(d.get("key")) for d in story} | set(trig)
    ran = [d for d in story if not d.get("skipped")]
    story_pda = [d for d in pda if d.get("storyKey") or str(d.get("subtitles")) in keys]
    running = [d for d in aurora if d.get("state") == "running" or d.get("countdownPlaying") or d.get("explodePlaying")]
    lacking = [k for k in trig if not any(str(d.get("key")) == k and d.get("skipped") for d in story)]
    ok = not ran and not story_pda and not running and not lacking
    paths = sorted({str(d.get("path")) for d in story if d.get("path")})
    row(rows, "S15.STORY", "PASS" if ok else "FAIL", "%d goals skipped, %d ran; %d PDA lines (%d of a story goal); aurora %s%s" % (
        sum(1 for d in story if d.get("skipped")), len(ran), len(pda), len(story_pda), ",".join(sorted({str(d.get("state")) for d in aurora})) or "-",
        "; trigger not skipped: %s" % lacking if lacking else ""),
        "0 ran, 0 story PDA lines, the Aurora held", "Subnautica's story goals and the Aurora's countdown off (skipped lines: %s)" % ",".join(paths)[:160], "voice",
        skipped=sorted(keys)[:12], pdaLines=sorted({str(d.get("sound")) for d in pda})[:12])

def check_alert(S, rows):
    al = S.rows("sn", "s15.alert")
    if not al:
        row(rows, "S15.ALERT", "MISSING", "no s15.alert row", "found and enabled", "Subnautica's own oxygen alert back as it was", "voice")
        return
    bad = [d for d in al if not d.get("found") or not d.get("enabled")]
    row(rows, "S15.ALERT", "PASS" if not bad else "FAIL", "%d rows: found %s, enabled %s" % (len(al), al[-1].get("found"), al[-1].get("enabled")),
        "found, enabled", "Subnautica's own LowOxygenAlert untouched by S15 (owner 2026-10-24)", "voice")

# ---------------------------------------------------------------------------------------------------------------- pictures
def _circle_luma(img, cx, cy, r0, r1):
    """The mean luma of the pixels between radii r0 and r1 (pixels) around (cx, cy), or None."""
    w, h = img.width, img.height
    step = max(1, int(r1) // 80)
    s = n = 0
    for y in range(max(0, int(cy - r1)), min(h, int(cy + r1) + 1), step):
        for x in range(max(0, int(cx - r1)), min(w, int(cx + r1) + 1), step):
            d = math.hypot(x - cx, y - cy)
            if r0 <= d <= r1:
                s += BC.luma(*img.pixel(x, y))
                n += 1
    return s / n if n else None

def face_stats(img, cx, cy, rad):
    """(pixels, sea, cloud, seabed, the sea pixels' mean rgb or None) within rad of (cx, cy) (format air.colour)."""
    w, h = img.width, img.height
    step = max(1, int(rad) // 120)
    n = sea = cloud = bed = 0
    acc = [0, 0, 0]
    for y in range(max(0, int(cy - rad)), min(h, int(cy + rad) + 1), step):
        for x in range(max(0, int(cx - rad)), min(w, int(cx + rad) + 1), step):
            if (x - cx) ** 2 + (y - cy) ** 2 > rad * rad:
                continue
            p = img.pixel(x, y)
            k = T.classify(p)
            n += 1
            if k == "sea":
                sea += 1
                acc[0] += p[0]
                acc[1] += p[1]
                acc[2] += p[2]
            elif k == "cloud":
                cloud += 1
            else:
                bed += 1
    return n, sea, cloud, bed, (tuple(v / sea for v in acc) if sea else None)

def judge_face(stats, target, hue=True):
    """(ok, text): the seabed share and (hue) the sea pixels' hue and luma (formats air.colour, orbit.colour)."""
    n, sea, cloud, bed, mean = stats
    if n == 0:
        return False, "no pixel"
    bed_pm = 1000.0 * bed / n
    ok_bed = bed_pm <= T.CO["SeabedMaxPermille"]
    txt = "sea %d / cloud %d / seabed %d permille" % (round(1000.0 * sea / n), round(1000.0 * cloud / n), round(bed_pm))
    if mean is None:
        return False, txt + ", no sea pixel"
    ok_sea, hue_off, lu = T.sea_ok(mean, target)
    if not hue:
        return ok_bed, txt + ", sea %s hue %.0f L%.0f (near sea: information)" % (_rgb(mean), hue_off, lu)
    return ok_bed and ok_sea, txt + ", sea %s hue %.0f L%.0f" % (_rgb(mean), hue_off, lu)

def check_moons(S, rows):
    reads = S.rows("sn", "s15.moons")
    if reads:
        shown = [d for d in reads if not d.get("hidden")]
        row(rows, "S15.MOONS.READ", "INFO", "%d of %d rows with a body on (planet %d, moon %d)" %
            (len(shown), len(reads), sum(1 for d in shown if not d.get("planetHidden")), sum(1 for d in shown if not d.get("moonHidden"))),
            "0", "the read-back: Subnautica's planet and moon off in the skybox material", "sky", required=False)
    judged, parts, bright, below, day = 0, [], [], [], []
    tol = T.CO["TolLuma"]
    for kind in ("moon-a", "moon-b"):
        p = S.cap(kind)
        if not p:
            continue
        d = (S.cap_ev.get(kind) or {}).get("dir")
        if d and len(d) == 3 and _f(d[1], 0.0) < MOON_MIN_SIN:
            below.append(kind)
            continue
        img = CP.load_image(p)
        hh = img.height
        c = _circle_luma(img, img.width / 2.0, hh / 2.0, 0.0, MOON_CENTRE * hh)
        ring = _circle_luma(img, img.width / 2.0, hh / 2.0, MOON_RING[0] * hh, MOON_RING[1] * hh)
        if c is None or ring is None:
            continue
        if ring > MOON_NIGHT_LUMA:
            day.append("%s sky L%.0f" % (kind, ring))
            continue
        judged += 1
        parts.append("%s centre L%.0f sky L%.0f" % (kind, c, ring))
        if c - ring > tol:
            bright.append(kind)
    what = "Subnautica's moon gone from its night sky: a capture along each sign of uSky's moon axis, the centre vs the sky around it"
    if judged:
        row(rows, "S15.MOONS", "FAIL" if bright else "PASS", "; ".join(parts) + (" | a body in %s" % ",".join(bright) if bright else "") +
            (" (%s below the horizon)" % ",".join(below) if below else ""), "centre - sky <= L%d" % tol, what, "sky")
    elif day:
        row(rows, "S15.MOONS", "INFO", "no night capture (%s: brighter than L%.0f, not night)" % (", ".join(day), MOON_NIGHT_LUMA), "centre - sky <= L%d" % tol, what, "sky", required=False)
    elif below:
        row(rows, "S15.MOONS", "INFO", "the moon axis on the horizon (%s not judged)" % ",".join(below), "centre - sky <= L%d" % tol, what, "sky", required=False)
    elif not reads or (S.mode == "S15b" and S.scripted("sky")):
        row(rows, "S15.MOONS", "MISSING", "no moon-a/moon-b capture%s" % ("" if reads else ", no s15.moons row"), "centre - sky <= L%d" % tol, what, "sky")
    else:
        row(rows, "S15.MOONS", "INFO", "no night capture (free play): the read-back only", "centre - sky <= L%d" % tol, what, "sky", required=False)

def check_skylist(S, rows):
    se = {int(_f(d.get("listSeq"), -1)): sorted(b.get("name") for b in (d.get("bodies") or [])) for d in S.rows("se", "s15.skylist")}
    sn = S.rows("sn", "s15.skylist")
    if not sn:
        row(rows, "S15.SKYLIST", "MISSING", "no Subnautica s15.skylist row (%d on SE)" % len(se), "SE's lists", "Subnautica received SE's body list", "sky")
        return
    bad = []
    for d in sn:
        ls = int(_f(d.get("listSeq"), -1))
        names = sorted(str(n).replace(" (bound)", "") for n in (d.get("names") or []))
        if se.get(ls) != names:
            bad.append(ls)
    row(rows, "S15.SKYLIST", "PASS" if not bad else "FAIL", "%d lists, %d differ%s" % (len(sn), len(bad), " (listSeq %s)" % bad[:3] if bad else ""),
        "equal", "Subnautica's body lists are SE's", "sky")

def _body_key(b):
    """A body of one list: its list index when the row carries it (two bodies may share a name), else its name."""
    return ("#%d %s" % (int(_f(b.get("idx"))), b.get("name"))) if b.get("idx") is not None else b.get("name")

def sky_pairs(S):
    """{body: [(angle deg, size ratio)]} over SE rows with a still camera paired by tick (and listSeq) with Subnautica's rows."""
    sn_by_tick = {}
    for d in S.rows("sn", "s15.sky"):
        sn_by_tick.setdefault(int(_f(d.get("tick"), -1)), d)
    pairs = {}
    for d in S.rows("se", "s15.sky"):
        if not d.get("still"):
            continue
        sn = sn_by_tick.get(int(_f(d.get("tick"), -2)))
        if sn is None or (d.get("listSeq") is not None and sn.get("listSeq") is not None and int(_f(d.get("listSeq"))) != int(_f(sn.get("listSeq")))):
            continue
        snb = {}
        for b in sn.get("bodies") or []:
            if b.get("shown") and b.get("cam"):
                snb.setdefault(_body_key(b), []).append(b)
        se_keys = [_body_key(b) for b in d.get("bodies") or []]
        for b in d.get("bodies") or []:
            same = snb.get(_body_key(b)) or []
            if len(same) != 1 or se_keys.count(_body_key(b)) != 1 or not b.get("cam"):
                continue   # missing, or a name shared without indices: never paired by guess
            o = same[0]
            ang = T.angle_deg(tuple(b["cam"]), tuple(o["cam"]))
            ratio = _f(o.get("angDeg")) / _f(b.get("angDeg"), float("nan"))
            pairs.setdefault(_body_key(b), []).append((ang, ratio))
    return pairs

def disc_px(img, expect_px=None):
    """The radius in pixels of the body at the frame's centre: the pixels within 1.5 x the expected radius (+ 6 px) of the centre
 whose luma differs by SKYPX_LUMA from the median of the sky ring at 2-3 x it, as one disc; None when there are none. Without
 an expected radius, a 0.1-height window (run 20261024-003024: a 0.45-height window over a body near the horizon counted the
 sea, 31 deg for a 1.7 deg body)."""
    w, h = img.width, img.height
    e = expect_px if expect_px and expect_px > 0 else 0.05 * h
    r_in, r0, r1 = 1.5 * e + 6, 2.0 * e + 8, 3.0 * e + 12
    cx, cy = w / 2.0, h / 2.0
    step = max(1, int(r1) // 120)
    ring, inner = [], []
    for y in range(max(0, int(cy - r1)), min(h, int(cy + r1) + 1), step):
        for x in range(max(0, int(cx - r1)), min(w, int(cx + r1) + 1), step):
            d = math.hypot(x - cx, y - cy)
            if d <= r_in:
                inner.append(BC.luma(*img.pixel(x, y)))
            elif r0 <= d <= r1:
                ring.append(BC.luma(*img.pixel(x, y)))
    if not ring:
        return None
    bg = median(ring)
    n = sum(1 for v in inner if abs(v - bg) > SKYPX_LUMA)
    return math.sqrt(n * step * step / math.pi) if n else None

def check_sky(S, rows):
    pairs = sky_pairs(S)
    n = sum(len(v) for v in pairs.values())
    if not pairs or max(len(v) for v in pairs.values()) < MIN_PAIRS:
        row(rows, "S15.SKYDIR", "MISSING", "%d pairs (SE still rows x Subnautica rows on the same tick)" % n, ">= %d per body" % MIN_PAIRS,
            "each body's direction in both pictures", "sky")
        row(rows, "S15.SKYSIZE", "MISSING", "%d pairs" % n, ">= %d per body" % MIN_PAIRS, "the transport of each body's size (the rows)", "sky")
    else:
        tol_a = T.OR["AngleCentiDeg"] / 100.0
        tol_s = T.OR["SizePermille"] / 1000.0
        dirs = {k: median([a for a, _r in v]) for k, v in pairs.items() if len(v) >= MIN_PAIRS}
        sizes = {k: median([r for _a, r in v]) for k, v in pairs.items() if len(v) >= MIN_PAIRS}
        bad_d = {k: round(v, 3) for k, v in dirs.items() if not (v <= tol_a)}
        bad_s = {k: round(v, 3) for k, v in sizes.items() if not (abs(v - 1.0) <= tol_s)}
        row(rows, "S15.SKYDIR", "PASS" if not bad_d else "FAIL", "max median %.3f deg over %d bodies, %d pairs%s" % (max(dirs.values()), len(dirs), n, " bad %s" % bad_d if bad_d else ""),
            "<= %.1f deg" % tol_a, "each body's direction: SE's camera frame vs Subnautica's transforms", "sky")
        worst = max(abs(v - 1.0) for v in sizes.values())
        row(rows, "S15.SKYSIZE", "PASS" if not bad_s else "FAIL", "max |ratio - 1| %.3f over %d bodies%s" % (worst, len(sizes), " bad %s" % bad_s if bad_s else ""),
            "<= %.2f" % tol_s, "the transport: Subnautica's disc angular radius (its transform) vs SE's (the list and the frame), from the rows", "sky")
    # the pixels (information: a disc on a daylight sky is not a reliable measurement)
    got = []
    for kind in ("sky-se", "sky-sn"):
        p = S.cap(kind)
        if not p:
            continue
        e = S.cap_ev.get(kind) or {}
        img = CP.load_image(p)
        vfov = _f(e.get("vFovDeg"))
        if not (vfov > 0):
            got.append("%s: no vFovDeg" % kind)
            continue
        focal = (img.height / 2.0) / math.tan(math.radians(vfov) / 2.0)
        expect = focal * math.tan(math.radians(_f(e.get("angDeg"), 0.0))) if _f(e.get("angDeg"), 0.0) > 0 else None
        r = disc_px(img, expect)
        if r is None:
            got.append("%s: no disc" % kind)
            continue
        got.append("%s %.2f deg (r %.1f px)" % (kind, math.degrees(math.atan(r / focal)), r))
    if got:
        e = S.cap_ev.get("sky-se") or S.cap_ev.get("sky-sn") or {}
        row(rows, "S15.SKYPX", "INFO", "; ".join(got) + (" | %s listed %.2f deg" % (e.get("body"), _f(e.get("angDeg"))) if e.get("body") else ""),
            "information", "each picture's disc measured in pixels, aimed at the largest shown body (atan(r px / focal px))", "sky", required=False)

def check_water(S, rows):
    w = S.rows("se", "s15.water")
    what = "SE's water read-back (the captures judge the look: S15.AIR, S15.ORBIT)"
    if not w:
        row(rows, "S15.WATER", "INFO", "no s15.water row", "%s, transparent off" % T.WATER["material"], what, "selftest", required=False)
        return
    last = w[-1]
    row(rows, "S15.WATER", "INFO", "applied %s, texture %s, transparent %s%s" % (last.get("applied"), last.get("texture"), last.get("transparent"),
                                                                            (" (" + str(last.get("why")) + ")") if last.get("why") else ""),
        "%s, transparent off" % T.WATER["material"], what, "selftest", required=False)

BAND_T5 = ("sky_top", "sky_horizon", "near_water")   # format band.colour: T5's own; far_water by band.far (the haze line)

def _band_far(res):
    """format band.far on a compare.py bands result: SE's far_water against Subnautica's far_water-to-horizon segment."""
    by = {r["band"]: r for r in res["rows"]}
    if "far_water" not in by or "sky_horizon" not in by:
        return None
    ok, d_max, d_l, tt, point = T.haze_line(by["far_water"]["a"]["rgb"], by["far_water"]["b"]["rgb"], by["sky_horizon"]["b"]["rgb"])
    return {"ok": ok, "dMax": d_max, "dL": d_l, "t": tt, "point": [round(c, 1) for c in point], "se": by["far_water"]["a"]["rgb"],
            "snFar": by["far_water"]["b"]["rgb"], "snHorizon": by["sky_horizon"]["b"]["rgb"]}

def _band_text(res, far):
    parts = ["%s dMax %.0f dL %.0f" % (r["band"], r["dMax"], r["dL"]) for r in res["rows"] if r["band"] in BAND_T5]
    if far:
        parts.append("far_water haze line dMax %.0f dL %.0f (t %.2f; vs Subnautica's far water %s)" %
                     (far["dMax"], far["dL"], far["t"], "; ".join("%.0f" % r["dMax"] for r in res["rows"] if r["band"] == "far_water")))
    return "; ".join(parts)

def check_band(S, rows):
    a, b = S.cap("band-se"), S.cap("band-sn")
    if not a or not b:
        row(rows, "S15.BAND", "MISSING", "no band-se/band-sn capture pair", "T5 tolerance", "the colours at the switch altitude", "band")
        row(rows, "S15.BAND.FAR", "MISSING", "no band-se/band-sn capture pair", "T5 of the haze line", "the far water at the switch altitude", "band")
        return
    tol = "<= %d / %d" % (T.CO["TolChannel"], T.CO["TolLuma"])
    res = BC.run_pair(CP.load_image(a), CP.load_image(b), tol=T.CO["TolChannel"], ltol=T.CO["TolLuma"])
    bad = [r["band"] for r in res["rows"] if r["band"] in BAND_T5 and r["verdict"] != "PASS"]
    lacking = [n for n in BAND_T5 if n not in {r["band"] for r in res["rows"]}]
    row(rows, "S15.BAND", "PASS" if not bad and not lacking else "FAIL", "; ".join("%s dMax %.0f dL %.0f" % (r["band"], r["dMax"], r["dL"])
                                                                                   for r in res["rows"] if r["band"] in BAND_T5)[:200] +
        ("; missing %s" % ",".join(lacking) if lacking else ""), tol + " (sky_top, sky_horizon, near_water)",
        "SE's picture above the band vs Subnautica's below it (compare.py bands; far_water: S15.BAND.FAR)", "band", bands=res)
    far = _band_far(res)
    if far is None:
        row(rows, "S15.BAND.FAR", "MISSING", "no far_water or sky_horizon band in the pair", "T5 of the haze line", "the far water at the switch altitude", "band")
    else:
        row(rows, "S15.BAND.FAR", "PASS" if far["ok"] else "FAIL", "SE (%.0f, %.0f, %.0f) vs the haze line's (%.0f, %.0f, %.0f) at t %.2f: dMax %.0f dL %.0f" %
            (tuple(far["se"]) + tuple(far["point"]) + (far["t"], far["dMax"], far["dL"])), tol + " of Subnautica's far water to horizon",
            "SE's far water on Subnautica's haze line (format band.far: SE's atmosphere carries its far sea to the horizon colour)", "band", haze=far)
    c = S.cap("band-se-plain")
    if c:
        rp = BC.run_pair(CP.load_image(c), CP.load_image(b), tol=T.CO["TolChannel"], ltol=T.CO["TolLuma"])
        row(rows, "S15.BAND.PLAIN", "INFO", _band_text(rp, _band_far(rp))[:240], tol,
            "the same pair with the Water Mod's own material (%s, Reflectivity 0.925): the planet's is %s (%s)" %
            (T.WATER_FAR["plain"], T.WATER_FAR["seaMaterial"], T.WATER_FAR["reflectivity"]), "band", required=False, bands=rp)

def _threshold():
    return "seabed <= %d permille; sea hue <= %d permille, L %.0f..%.0f" % (
        T.CO["SeabedMaxPermille"], T.CO["SeaChromaPermille"], T.luma(*T.ENDING["sea"]) - T.CO["TolLuma"], T.luma(*T.SN_NEAR_WATER) + T.CO["TolLuma"])

def check_air(S, rows):
    have = [km for km in AIR_KM if S.cap("air-%d" % km)]
    what = "the sea from the air (SE's screenshot looking down): opaque, the ending's hue"
    if not have:
        row(rows, "S15.AIR", "MISSING", "no air-30/air-5 capture", _threshold(), what, "air")
        return
    parts, bad = [], []
    for km in AIR_KM:
        if km not in have:
            continue
        img = CP.load_image(S.cap("air-%d" % km))
        st = face_stats(img, img.width / 2.0, img.height / 2.0, T.CO["FacePermille"] / 1000.0 * img.height)
        ok, txt = judge_face(st, T.ENDING["sea"], hue=km * 1000 >= T.WATER_FAR["up"])
        parts.append("%d km %s" % (km, txt))
        if not ok:
            bad.append("%d km" % km)
    missing = [km for km in AIR_KM if km not in have]
    st = "FAIL" if bad else ("MISSING" if missing else "PASS")
    row(rows, "S15.AIR", st, ("; ".join(parts) + (" | red: " + ", ".join(bad) if bad else ""))[:240], _threshold(),
        what + (" (missing %s km)" % missing if missing else ""), "air")

# the sea's brightness seam. The Water Mod lights each surface quad as a whole: max(sunDot, Ambient) x ColorIntensity plus,
# only where sunDot > 0, a specular term whose half vector mixes planet-local and world space,
# so its terminator is a step along quad edges; SE's real noon puts it beyond the limb from 30 km (S15's air captures 3-6 luma,
# the look sheet's 32-44: its "noon" was S7's dev phase, which never reaches SE's picture nor the Water Mod)
SEAM_MAX_LUMA = 12.0
SEAM_KM = 5
SEAM_SHIFT_M = 4000.0       # the pair's camera move east (launcher_s15 SEAM_SHIFT_M)
SEAM_PAIR_MARGIN = 0.03
SEAM_QUAD_FRAC = 0.31       # the step sits on a quad edge: up to a quad (about 1.9 km from 5 km up, 450 of 1440 px) off the true terminator

def sea_step(img, margin=0.15):
    """The largest step of the sea's brightness across the picture: per sampled column (a vertical seam) and per sampled row (a
 horizontal one) the median luma of the sea's pixels (blue water: g > r + 10, b > g + 4, luma 12-150; clouds, land, the
 atmosphere ring and space out) inside the sea's extent less `margin` on each side (the central 70 %: the limb from 30 km); the step is the largest difference between the
 means of two windows (w / 100 wide, w / 600 apart) on either side of a column or row. (step, axis, at px, sea fraction)."""
    w, h = img.width, img.height
    st = max(1, min(w, h) // 360)
    xs, ys = list(range(0, w, st)), list(range(0, h, st))
    sea = {}
    for y in ys:
        for x in xs:
            r, g, b = img.pixel(x, y)
            lum = BC.luma(r, g, b)
            if g > r + 10 and b > g + 4 and 12.0 < lum < 150.0:
                sea[(x, y)] = lum
    if len(sea) < 0.05 * len(xs) * len(ys):
        return 0.0, "-", -1, len(sea) / float(max(1, len(xs) * len(ys)))
    sx = sorted(k[0] for k in sea)
    sy = sorted(k[1] for k in sea)
    x0, x1 = sx[int(margin * (len(sx) - 1))], sx[int((1.0 - margin) * (len(sx) - 1))]
    y0, y1 = sy[int(margin * (len(sy) - 1))], sy[int((1.0 - margin) * (len(sy) - 1))]
    inner = {k: v for k, v in sea.items() if x0 <= k[0] <= x1 and y0 <= k[1] <= y1}
    win = max(3, (w // 100) // st)
    gap = max(1, (w // 600) // st)
    best, judged = (0.0, "-", -1), False
    for axis, keys, other in (("col", xs, 0), ("row", ys, 1)):
        groups = {}
        for k, v in inner.items():
            groups.setdefault(k[other], []).append(v)
        need = max(5, len(ys if other == 0 else xs) // 10)
        prof = []
        for c in keys:
            vals = sorted(groups.get(c, ()))
            prof.append(vals[len(vals) // 2] if len(vals) >= need else None)
        for i in range(win + gap, len(prof) - win - gap):
            lo, hi = prof[i - gap - win:i - gap], prof[i + gap:i + gap + win]
            if any(v is None for v in lo) or any(v is None for v in hi):
                continue
            d = abs(sum(hi) / len(hi) - sum(lo) / len(lo))
            if not judged or d > best[0]:
                best, judged = (d, axis, keys[i]), True
    return best[0], best[1], best[2], len(sea) / float(len(xs) * len(ys))

def check_seam(S, rows):
    what = "no brightness step across the sea at SE's noon from %s km (the Water Mod's quad terminator beyond the view)" % "/".join(str(k) for k in AIR_KM)
    have = [km for km in AIR_KM if S.cap("air-%d" % km)]
    if not have:
        row(rows, "S15.SEAM", "MISSING", "no air-30/air-5 capture", "step <= %.0f luma" % SEAM_MAX_LUMA, what, "air")
    else:
        parts, bad, empty = [], [], []
        for km in AIR_KM:
            if km not in have:
                continue
            d, axis, at, frac = sea_step(CP.load_image(S.cap("air-%d" % km)))
            if axis == "-":
                empty.append(km)
                parts.append("%d km: no sea (%.0f %%)" % (km, 100 * frac))
                continue
            parts.append("%d km step %.1f (%s %d)" % (km, d, axis, at))
            if d > SEAM_MAX_LUMA:
                bad.append(km)
        st = "FAIL" if bad else ("MISSING" if empty or len(have) < len(AIR_KM) else "PASS")
        row(rows, "S15.SEAM", st, "; ".join(parts), "step <= %.0f luma (sRGB) at each height" % SEAM_MAX_LUMA, what, "air")
    pair = [S.cap(k) for k in ("seam-5a", "seam-5b", "seam-5s")]
    if not all(pair):
        return
    # from 5 km the sea fills the frame: the whole of it but 3 % a side (run 20261024-150805: 5b's step sat at x 320 of 2560, the
 # central 70 % missed it)
    res = [sea_step(CP.load_image(p), SEAM_PAIR_MARGIN) for p in pair]
    ev = S.cap_ev.get("seam-5a") or {}
    img = CP.load_image(pair[0])
    vfov = _f(ev.get("vFovDeg"), ORBIT_VFOV_DEG)
    f_px = (img.height / 2.0) / math.tan(math.radians(vfov) / 2.0)
    want = f_px * SEAM_SHIFT_M / (SEAM_KM * 1000.0)
    moved = res[0][1] == res[1][1] == "col" and res[0][2] >= 0 and res[1][2] >= 0
    shift = (res[1][2] - res[0][2]) if moved else float("nan")
    world = moved and abs(abs(shift) - want) <= SEAM_QUAD_FRAC * img.height
    txt = "sunrise, the mod's specular: 5a step %.1f (%s %d), 5b (camera %.0f km east) step %.1f (%s %d): moved %s px of %.0f predicted (%s); ours (specular 0) step %.1f" % (
        res[0][0], res[0][1], res[0][2], SEAM_SHIFT_M / 1000.0, res[1][0], res[1][1], res[1][2], "%.0f" % shift if moved else "-", want,
        "with the ground: the sea's terminator" if world else "not with the ground", res[2][0])
    row(rows, "S15.SEAM.PAIR", "INFO", txt, "information", "the seam's cause: the Water Mod's terminator on its quads (it moves with the ground), gone with SpecularIntensity 0", "air",
        required=False)

def measure_disc(img):
    """(centre rgb, limb rgb, radius px, centre xy) of the bright disc on a dark background, or None."""
    w, h = img.width, img.height
    step = max(1, min(w, h) // 400)
    sx = sy = n = 0
    for y in range(0, h, step):
        for x in range(0, w, step):
            r, g, b = img.pixel(x, y)
            if BC.luma(r, g, b) >= DISC_LUMA_MIN:
                sx += x
                sy += y
                n += 1
    if n < 20:
        return None
    cx, cy = sx / n, sy / n
    rad = math.sqrt(n * step * step / math.pi)
    c_in = T.CO["CentrePermille"] / 1000.0 * rad
    l0, l1 = T.CO["LimbInnerPermille"] / 1000.0 * rad, T.CO["LimbOuterPermille"] / 1000.0 * rad
    acc_c, acc_l = [0, 0, 0, 0], [0, 0, 0, 0]
    y0, y1 = max(0, int(cy - rad)), min(h, int(cy + rad) + 1)
    x0, x1 = max(0, int(cx - rad)), min(w, int(cx + rad) + 1)
    for y in range(y0, y1, step):
        for x in range(x0, x1, step):
            d = math.hypot(x - cx, y - cy)
            acc = acc_c if d <= c_in else (acc_l if l0 <= d <= l1 else None)
            if acc is None:
                continue
            r, g, b = img.pixel(x, y)
            acc[0] += r
            acc[1] += g
            acc[2] += b
            acc[3] += 1
    if acc_c[3] == 0 or acc_l[3] == 0:
        return None
    return tuple(v / acc_c[3] for v in acc_c[:3]), tuple(v / acc_l[3] for v in acc_l[:3]), rad, (cx, cy)

# the orbit geometry when the capture event lacks it (run 20261024-003024: the site's sea radius, SE's 70 deg default)
ORBIT_RS_M, ORBIT_VFOV_DEG = 60000.0, 70.0

def expected_disc(img, km, rs, vfov_deg):
    """(centre rgb, limb rgb, radius px, centre xy) of the planet the spectator looks at: the image's centre and the radius from
 the geometry (asin(Rs / (Rs + h)) through the vertical field of view): the nebula, the sun and the other bodies of SE's sky
 never move it (run 20261024-003024: the bright-pixel centroid followed the nebula)."""
    if not (rs > 0 and vfov_deg > 0 and km > 0):
        return measure_disc(img)
    w, h = img.width, img.height
    focal = (h / 2.0) / math.tan(math.radians(vfov_deg) / 2.0)
    rad = focal * math.tan(math.asin(rs / (rs + km * 1000.0)))
    cx, cy = w / 2.0, h / 2.0
    step = max(1, int(rad) // 200)
    c_in = T.CO["CentrePermille"] / 1000.0 * rad
    l0, l1 = T.CO["LimbInnerPermille"] / 1000.0 * rad, T.CO["LimbOuterPermille"] / 1000.0 * rad
    acc_c, acc_l = [0, 0, 0, 0], [0, 0, 0, 0]
    for y in range(max(0, int(cy - rad)), min(h, int(cy + rad) + 1), step):
        for x in range(max(0, int(cx - rad)), min(w, int(cx + rad) + 1), step):
            d = math.hypot(x - cx, y - cy)
            acc = acc_c if d <= c_in else (acc_l if l0 <= d <= l1 else None)
            if acc is None:
                continue
            p = img.pixel(x, y)
            for i in range(3):
                acc[i] += p[i]
            acc[3] += 1
    if acc_c[3] == 0 or acc_l[3] == 0:
        return None
    return tuple(v / acc_c[3] for v in acc_c[:3]), tuple(v / acc_l[3] for v in acc_l[:3]), rad, (cx, cy)

def rim_colours(img, cx, cy, rad):
    """(warm mean, warm n, cool mean, cool n) of the non-black pixels between the disc's radius and RIM_OUTER x it."""
    w, h = img.width, img.height
    step = max(1, int(rad) // 200)
    warm, cool = [0, 0, 0, 0], [0, 0, 0, 0]
    r1 = RIM_OUTER * rad
    for y in range(max(0, int(cy - r1)), min(h, int(cy + r1) + 1), step):
        for x in range(max(0, int(cx - r1)), min(w, int(cx + r1) + 1), step):
            d = math.hypot(x - cx, y - cy)
            if not (rad < d <= r1):
                continue
            p = img.pixel(x, y)
            if BC.luma(*p) < DISC_LUMA_MIN:
                continue
            acc = warm if p[0] - p[2] >= 30 else (cool if p[2] >= p[0] else None)
            if acc is None:
                continue
            for i in range(3):
                acc[i] += p[i]
            acc[3] += 1
    mean = lambda a: tuple(v / a[3] for v in a[:3]) if a[3] else None   # noqa: E731
    return mean(warm), warm[3], mean(cool), cool[3]

def check_orbit(S, rows):
    have = [km for km in ORBIT_KM if S.cap("orbit-%d" % km)]
    what = "the planet from orbit (SE's screenshot at the planet centre): the disc centre opaque with the ending's hue"
    if not have:
        row(rows, "S15.ORBIT", "MISSING", "no orbit-600/200/100 capture", _threshold(), what, "orbit")
        return
    parts, bad, info = [], [], []
    for km, centre_t, limb_t in T.ORBIT:
        if km not in have:
            continue
        img = CP.load_image(S.cap("orbit-%d" % km))
        e = S.cap_ev.get("orbit-%d" % km) or {}
        m = expected_disc(img, km, _f(e.get("rs"), ORBIT_RS_M), _f(e.get("vFovDeg"), ORBIT_VFOV_DEG))
        if m is None:
            bad.append("%d km: no disc" % km)
            continue
        _c, limb, rad, (cx, cy) = m
        st = face_stats(img, cx, cy, T.CO["CentrePermille"] / 1000.0 * rad)
        ok, txt = judge_face(st, centre_t)
        parts.append("%d km %s" % (km, txt))
        if not ok:
            bad.append("%d km" % km)
        _ok, d_l, l_l = T.colour_within(limb, limb_t)
        wm, wn, cm, cn = rim_colours(img, cx, cy, rad)
        cover = round(1000.0 * st[2] / st[0]) if st[0] else 0
        info.append("%d km limb %s d%.0f/L%.0f (ending %s); rim warm %s (%d px) cool %s (%d px) (ending %s / %s); clouds %d permille (ending %d)" % (
            km, _rgb(limb), d_l, l_l, _rgb(limb_t), _rgb(wm), wn, _rgb(cm), cn, _rgb(T.ENDING["rimWarm"]), _rgb(T.ENDING["rimCool"]), cover, T.ENDING["cover"]))
    missing = [km for km in ORBIT_KM if km not in have]
    st = "FAIL" if bad else ("MISSING" if missing else "PASS")
    row(rows, "S15.ORBIT", st, ("; ".join(parts) + (" | red: " + ", ".join(bad) if bad else ""))[:240], _threshold(),
        what + (" (missing %s km)" % missing if missing else ""), "orbit")
    swaps = S.rows("se", "s15.waterswap")
    if swaps:
        row(rows, "S15.FARSEA", "INFO", "; ".join("%s at %.0f m%s" % ("far" if d.get("far") else "near", _f(d.get("altitude")), "" if d.get("ok") else " (refused)") for d in swaps)[:200],
            "far above %d m, near below %d m" % (T.WATER_FAR["up"], T.WATER_FAR["down"]), "the far sea swapped by altitude (format water.far)", "orbit", required=False)
    if info:
        row(rows, "S15.LIMB", "INFO", " | ".join(info)[:400], "information",
            "the limb ring, the rim beyond the disc and the clouds against the ending (SE draws the sea over its haze and clouds)", "orbit", required=False)

def check_night(S, rows):
    n = S.rows("sn", "s15.night")
    if not n:
        row(rows, "S15.NIGHT", "MISSING", "no s15.night row", "0.4 + 0.6 x light", "the block lights at night", "sky")
        return
    off = [d for d in n if abs(_f(d.get("factor")) - T.night_factor(_f(d.get("light")))) > 0.01]
    night = [d for d in n if _f(d.get("light"), 1.0) <= 0.1]
    lacking = S.scripted("sky") and not night
    ok = not off and not lacking
    row(rows, "S15.NIGHT", "PASS" if ok else ("MISSING" if lacking and not off else "FAIL"),
        "%d rows, %d night (factor %s), %d off the rule" % (len(n), len(night), ",".join("%.2f" % _f(d.get("factor")) for d in night[:3]) or "-", len(off)),
        "0.4 + 0.6 x light", "the block lights dimmed at night the Subnautica way", "sky")

def check_splash(S, rows):
    sp = S.rows("sn", "s15.splash")
    if not sp:
        row(rows, "S15.SPLASH", "MISSING", "no surfacing after a dive", "every surfacing", "water on the lens when surfacing", "voice")
        return
    missed = [d for d in sp if not d.get("created")]
    row(rows, "S15.SPLASH", "PASS" if not missed else "FAIL", "%d surfacings, %d splashed%s" % (len(sp), len(sp) - len(missed), "" if not missed else " (lens %s)" % missed[0].get("lens")),
        "every surfacing", "LensWater.CreateSplash when the camera surfaces after a dive", "voice")

def check_sonar(S, rows):
    pings = S.rows("se", "s15.sonar")
    heard = S.rows("sn", "s15.sonar")
    if not pings:
        row(rows, "S15.SONAR", "MISSING", "no SE ping", "every ping", "the sonar from a ship", "sonar")
        return
    played = [d for d in heard if d.get("reason") == "played"]
    nofx = [d for d in played if not d.get("fx")]
    unheard = len(pings) - len(heard)
    ok = heard and not nofx and unheard <= 1
    row(rows, "S15.SONAR", "PASS" if ok else "FAIL", "%d pings, %d heard (%d played, %d without the pulse)" % (len(pings), len(heard), len(played), len(nofx)),
        "every ping heard, the pulse with each", "the Cyclops's sonar sound and SNCameraRoot.SonarPing", "sonar")

def check_seabase(S, rows):
    sb = S.rows("sn", "s15.seabase")
    enters = [d for d in sb if d.get("what") == "enter"]
    if not enters:
        row(rows, "S15.SEABASE", "MISSING", "no airtight room entered under the sea", "hum and light", "a seabase that feels like one", "voice")
        return
    bad = [d for d in enters if int(_f(d.get("hum"), 0)) < int(_f(d.get("candidates"), 0)) or not d.get("light")]
    none = all(int(_f(d.get("candidates"), 0)) == 0 for d in enters)
    st = "FAIL" if bad else ("INFO" if none else "PASS")
    row(rows, "S15.SEABASE", st, "%d entries: hum %s of %s candidates, light %s%s" % (len(enters), ",".join(str(int(_f(d.get("hum"), 0))) for d in enters[:3]),
                                                                                   ",".join(str(int(_f(d.get("candidates"), 0))) for d in enters[:3]),
                                                                                   ",".join("on" if d.get("light") else "off" for d in enters[:3]),
                                                                                   " (no OnlyInBase loop in this scene)" if none else ""),
        "every candidate switched, the light on", "the base hum and the room light in an airtight room under the sea", "voice", required=not none)

def check_fish(S, rows):
    f = S.rows("sn", "s15.fish")
    near = [d for d in f if int(_f(d.get("near"), 0)) > 0]
    if not near:
        row(rows, "S15.FISH", "MISSING", "no fish near a fast ship section (%d rows)" % len(f), "scared", "small fish react to the ship", "life")
        return
    missed = [d for d in near if int(_f(d.get("scared"), 0)) < int(_f(d.get("near"), 0))]
    row(rows, "S15.FISH", "PASS" if not missed else "FAIL", "%d scans with fish near, scared %d of %d; max speed %.1f m/s" % (
        len(near), sum(int(_f(d.get("scared"), 0)) for d in near), sum(int(_f(d.get("near"), 0)) for d in near), max(_f(d.get("maxSpeed"), 0) for d in f)),
        "every fish near a fast section", "a fast ship scares the small fish (Scareable, FleeWhenScared)", "life")

VOICE_PARTS = ("voice", "sonar", "life")
CHECKS = ((("selftest", "voice", "sky"), check_pin), (("voice",), check_aboard), (VOICE_PARTS, check_voice), (("voice",), check_welcome),
          (("voice",), check_power), (VOICE_PARTS, check_mix), (VOICE_PARTS, check_s2), (("voice",), check_music), (("voice",), check_line),
          (("voice", "selftest"), check_story), (("voice", "selftest"), check_alert),
          (("sky",), check_moons), (("sky",), check_skylist), (("sky",), check_sky), (("selftest",), check_water),
          (("band",), check_band), (("air",), check_air), (("air",), check_seam), (("orbit",), check_orbit),
          (("sky",), check_night), (("voice",), check_splash), (("sonar",), check_sonar), (("voice",), check_seabase), (("life",), check_fish))
# rows a mutant may also touch: S15.M1 sends a boarding's states too (no baseline) and greets with SE's audio live; S15.M18's
# small grid never plays the Seamoth's line, so the voice part may lack it
MAY_AFFECT.update({"S15.M1": ("S15.WELCOME", "S15.POWER"), "S15.M18": ("S15.VOICE",)})

def mutant_rows(S, rows):
    for m in S.mutants:
        want = MUTANT_ROWS.get(m)
        if not want:
            continue
        may = MAY_AFFECT.get(m, ())
        for r in rows:
            if r["id"] in want:
                r["required"] = False
                r["what"] = r["what"] + " [expected red under %s]" % m
            elif r["id"] in may:
                r["required"] = False
                r["what"] = r["what"] + " [may go red under %s]" % m
        measured = [r for r in rows if r["id"] in want and r["status"] in ("PASS", "FAIL")]
        red = [r["id"] for r in measured if r["status"] == "FAIL"]
        others = [r["id"] for r in rows if r["id"] not in want and r["id"] not in may and r["status"] == "FAIL" and r.get("required", True) and r["id"] in ALL_ROWS]
        st = "MISSING" if not measured else ("PASS" if len(red) == len(want) and not others else "FAIL")
        row(rows, "%s.red" % m, st, "red: %s%s" % (",".join(red) or "none", "; also red: %s" % ",".join(others) if others else ""),
            "red: %s only" % ",".join(want), "seeded failure %s turns exactly its rows red" % m, "mutants")

SN_CHECKS = (check_mix, check_s2, check_music, check_line, check_story, check_alert, check_moons, check_skylist, check_sky, check_band, check_night,
             check_splash, check_sonar, check_seabase, check_fish)
ALL_ROWS = ("S15.PIN", "S15.ABOARD", "S15.VOICE", "S15.WELCOME", "S15.POWER", "S15.MIX", "S15.S2", "S15.MUSIC", "S15.LINE", "S15.STORY", "S15.ALERT",
            "S15.MOONS", "S15.SKYLIST", "S15.SKYDIR", "S15.SKYSIZE", "S15.BAND", "S15.BAND.FAR", "S15.AIR", "S15.SEAM", "S15.ORBIT",
            "S15.NIGHT", "S15.SPLASH", "S15.SONAR", "S15.SEABASE", "S15.FISH")
INFO_ROWS = ("S15.PATHS", "S15.MOONS.READ", "S15.SKYPX", "S15.WATER", "S15.LIMB")   # also BAND.PLAIN and FARSEA when their captures and rows exist

def evaluate(folder=None, se_logs=(), sn_logs=(), captures=None):
    S = Session(folder, se_logs, sn_logs, captures)
    rows = []
    for parts, fn in CHECKS:
        if S.parts and not S.ran(*parts):
            continue
        if S.mode == "S15se" and fn in SN_CHECKS:
            continue   # the stand-in host plays nothing and draws no sky: Subnautica's rows need S15b
        try:
            fn(S, rows)
        except Exception as e:  # noqa: BLE001 - an analyser bug is a FAIL row, never a crash
            row(rows, "S15.%s" % fn.__name__[6:].upper(), "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120], parts[0])
    mutant_rows(S, rows)
    return rows, S

def write_results(folder, rows, strict=True, extra=None) -> bool:
    ok = C.verdict(rows, strict)
    if folder:
        res = {"tool": "analyze_s15", "out": folder, "ok": ok, "rows": rows}
        res.update(extra or {})
        with open(os.path.join(folder, "s15-results.json"), "w", encoding="utf-8") as f:
            json.dump(res, f, indent=1, default=str)
        with open(os.path.join(folder, "s15-table.txt"), "w", encoding="utf-8") as f:
            f.write(C.table(rows) + "\n")
    return ok

# ======================================================================================================================
# self-test: synthetic sessions in the plugins' row forms, simulated with the twin's rules
# ======================================================================================================================
SHIP, ROOM, DECK, SUB, SUB2 = 9001, 9002, 9003, 9004, 9005
LABELS = {SHIP: "ship", ROOM: "room", DECK: "deck", SUB: "sub", SUB2: "sub2"}
SEA_OURS = (58, 97, 122)
SEE_THROUGH = (107, 110, 124)
JWATER2 = (36, 84, 125)           # the Water Mod's JWater2 from 40 km, opaque
CLOUD = (230, 235, 240)
NIGHT_TOP, NIGHT_LOW = (6, 10, 24), (14, 22, 40)
RESULT_NAMES_R = {T.PLAYED: "played", T.COOLDOWN: "cooldown", T.SEAUDIO: "seAudio"}
STORY_KEY = "AuroraWarning1"

def _image(w, h, fn):
    data = bytearray(w * h * 3)
    for y in range(h):
        for x in range(w):
            o = (y * w + x) * 3
            data[o:o + 3] = bytes(fn(x, y))
    return CP.Image(w, h, data)

def _save(img, path):
    with open(path, "wb") as f:
        f.write(CP.encode_png(img))

class _Synth:
    """A scripted session: SE's gate run by the twin over a condition script, Subnautica's lines, the mix and the story rows,
 the sky and the captures."""

    def __init__(self, folder, mutants=(), parts=("selftest", "voice", "sky", "band", "air", "orbit", "sonar", "life")):
        self.folder = folder
        os.makedirs(os.path.join(folder, "captures"), exist_ok=True)
        self.mut = set(mutants)
        self.sess = [{"ev": "start", "mode": "S15b", "parts": list(parts), "mutants": list(mutants)}]
        self.sess += [{"ev": "s15_grid", "label": lab, "grid": str(g)} for g, lab in LABELS.items()]
        self.se, self.sn = [], []
        self.gate = T.Gate()
        self.seq = {k: 0 for k in range(1, T.EVENT_COUNT + 1)}
        self.cond = {}
        self.state = {}   # grid -> dict(powered, switchOn, reactors, uranium, large, station, how)

    def se_row(self, name, **kw):
        d = {"t": "2026-10-24T00:00:00Z", "ev": "s15." + name}
        d.update(kw)
        self.se.append(d)

    def sn_row(self, name, **kw):
        self.sn.append({"t": "2026-10-24T00:00:00Z", "ev": "s15." + name, "event": "s15." + name, "data": kw})

    # ---- SE's side
    def decide(self, ev, grid, ms, r, bus, held=False):
        seq = 0
        live = T.sn_audio_live(bus)
        if r == T.PLAYED:
            self.seq[ev] += 1
            seq = self.seq[ev]
            played = "S15.M7" in self.mut or live
            self.sn_row("voice", event=EVENT_NAMES[ev], evId=ev, seq=seq, played=played, reason="played" if played else "seAudio", volume=1.6, snBus=bus,
                        path="event:/sub/%s/AI_test" % ("seamoth" if ev == T.SMALL_WELCOME else "cyclops"), source="test", coalesced=0, dropped=0)
        self.se_row("voice", evId=ev, event=EVENT_NAMES[ev], grid=grid, gridStr=str(grid), ms=ms, result=RESULT_NAMES_R[r], played=r == T.PLAYED, held=held, seq=seq, snBus=bus)

    def fields(self, grid, before):
        s = self.state[grid]
        return dict(how=s["how"], large=s["large"], station=s["station"], powered=s["powered"], powerBefore=before, switchOn=s["switchOn"],
                    reactors=s["reactors"], uraniumKg=s["uranium"], thrust=s.get("thrust", 0.0))

    def sample(self, ev, grid, on, ms, bus=1.0, before=None, baseline=False):
        key = (ev, grid)
        was = self.cond.get(key, False)
        if baseline:
            self.cond[key] = on
            if on:
                self.se_row("cond", evId=ev, event=EVENT_NAMES[ev], grid=grid, gridStr=str(grid), on=True, ms=ms, snBus=bus, voiceOn=True, baseline=True,
                            **self.fields(grid, self.state[grid]["powered"]))
            self.gate.baseline(ev, str(grid), on, ms)
            return
        if was == on:
            return
        nr = "S15.M1" in self.mut
        hold = "S15.M17" in self.mut and ev in WELCOMES
        b = self.state[grid]["powered"] if before is None else before
        d = dict(evId=ev, event=EVENT_NAMES[ev], grid=grid, gridStr=str(grid), on=on, ms=ms, snBus=bus, voiceOn=True, **self.fields(grid, b))
        self.cond[key] = on
        if not on:
            d["lastOnMs"] = ms - 167
            self.se_row("cond", **d)
            self.gate.step(ev, str(grid), True, ms - 167, T.sn_audio_live(bus), no_rules=nr, hold=hold)
            self.gate.step(ev, str(grid), False, ms, T.sn_audio_live(bus), no_rules=nr, hold=hold)
            return
        self.se_row("cond", **d)
        r = self.gate.step(ev, str(grid), True, ms, T.sn_audio_live(bus), no_rules=nr, hold=hold)
        if r != T.NONE:
            self.decide(ev, grid, ms, r, bus)

    def held_release(self, ms, bus):
        """S15.M17: the held welcomes sent at the first live scan (no condition row at that ms)."""
        for ev, key in self.gate.pending():
            r = self.gate.step(ev, key, True, ms, True, hold=True)
            if r != T.NONE:
                self.decide(ev, int(key), ms, r, bus, held=True)

    def power_row(self, grid, ms, baseline=False, before=None):
        s = self.state[grid]
        self.se_row("power", grid=grid, gridStr=str(grid), how=s["how"], powered=s["powered"], powerBefore=s["powered"] if before is None else before,
                    switchOn=s["switchOn"], reactors=s["reactors"], working=s["reactors"] if s["powered"] else 0, uraniumKg=s["uranium"],
                    large=s["large"], station=s["station"], baseline=baseline, ms=ms)

    def board(self, grid, how, ms, large, station=False, powered=False, bus=1.0):
        self.state[grid] = dict(how=how, large=large, station=station, powered=powered, switchOn=True, reactors=0, uranium=0.0)
        if how == "box" and "S15.M2" not in self.mut:
            return False
        self.se_row("aboard", grid=grid, gridStr=str(grid), how=how, large=large, station=station, ms=ms)
        self.power_row(grid, ms, baseline=True)
        big = large or station or "S15.M18" in self.mut
        up_on = powered if "S15.M16" not in self.mut else False
        if "S15.M1" in self.mut:
            self.sample(T.POWER_UP, grid, up_on, ms, bus, before=False)   # no baseline: a powered grid is an "edge"
        else:
            self.sample(T.POWER_UP, grid, up_on, ms, bus, baseline=True)
            self.sample(T.POWER_DOWN, grid, False, ms, bus, baseline=True)
        self.sample(T.WELCOME if big else T.SMALL_WELCOME, grid, True, ms, bus)
        return True

    def leave(self, grid, ms):
        for ev in range(1, T.EVENT_COUNT + 1):
            if self.cond.get((ev, grid)):
                self.sample(ev, grid, False, ms)
        self.se_row("aboard", grid=0, gridStr="0", from_=grid, how="none", ms=ms)

    def power(self, grid, ms, bus=1.0, **change):
        """A change of the grid's power state (a reactor, fuel, the switch): its s15.power row, then the conditions."""
        s = self.state[grid]
        before = s["powered"]
        s.update(change)
        self.power_row(grid, ms, before=before)
        if "S15.M16" not in self.mut:
            self.sample(T.POWER_UP, grid, s["powered"], ms, bus, before=before)
        if "switchOn" in change:
            self.sample(T.POWER_DOWN, grid, (not s["switchOn"]) and before, ms, bus, before=before)

    def mix_rows(self, t0, t1, above_from, above_to):
        """s15.mix rows every 250 ms from t0 to t1 with the camera above water between above_from and above_to (SN's ms)."""
        cut = "S15.M19" in self.mut
        g = {"music": 1.0, "voice": 1.0}
        sfx = 1.0
        t = t0
        while t <= t1:
            above = above_from <= t < above_to
            s2 = 0.0 if above else 1.0
            sfx = max(0.0, sfx - 250 / 300.0) if above else min(1.0, sfx + 250 / 300.0)
            for k in ("music", "voice"):
                target = T.group_target(s2, 0.6)
                step = 250.0 / T.MX["CrossfadeMs"] * (1 - 0.6)
                g[k] = max(target, g[k] - step) if g[k] > target else min(target, g[k] + step)
            if cut:
                m = above and sfx == 0.0
                self.sn_row("mix", active=False, why="S15.M19 (the old cut)", sfx=sfx, music=sfx, voice=sfx, musicTarget=1.0, voiceTarget=1.0, musicAbove=0.6,
                            voiceAbove=0.6, s2Target=s2, muted=m, muteWhy="bus target 0 (above water)" if m else "audible", ms=t, mutant=True)
            else:
                # S2's own mute for a moment above water (SE not in front: run 20261024-133305, 5 s): counted apart, not judged
                s2mute = above and t == above_from + 4000
                self.sn_row("mix", active=True, why="groups", sfx=sfx, music=round(g["music"], 4), voice=round(g["voice"], 4), musicTarget=T.group_target(s2, 0.6),
                            voiceTarget=T.group_target(s2, 0.6), musicAbove=0.6, voiceAbove=0.6, s2Target=s2, muted=s2mute,
                            muteWhy="SE not foreground" if s2mute else "audible", ms=t, mutant=False)
            t += 250

    def script(self):
        bus_live = 1.0
        # the large ship: welcome, a reactor placed empty (nothing), fuel (up), Y off (down), Y on (up, a line crossing the surface)
        self.board(SHIP, "seat", 0, large=True)
        self.power(SHIP, 3000, reactors=1)
        self.power_row(SHIP, 6000)
        self.state[SHIP]["uranium"] = 5.0
        self.power_row(SHIP, 6000, before=False)
        self.power(SHIP, 6167, powered=True)
        self.power(SHIP, 12000, switchOn=False, powered=False)
        self.power(SHIP, 24000, switchOn=True, powered=True)
        cut = "S15.M19" in self.mut
        self.sn_row("voiceend", event="EnginePowerUp", seq=self.seq[T.POWER_UP], heardMs=2600, lenMs=2500, posMs=1100 if cut else 2480, complete=not cut,
                    voiceMin=0.0 if cut else 0.6, crossed=True, startedUnder=True, muted=cut, groups=not cut, mutant=cut)
        self.sn_row("voiceend", event="Welcome", seq=1, heardMs=2400, lenMs=2300, posMs=2290, complete=True, voiceMin=1.0, crossed=False, startedUnder=True,
                    muted=False, groups=not cut, mutant=cut)
        self.mix_rows(23000, 36000, 24500, 32000)
        # never on thrust: S15.M16 takes the thrust as the engine condition
        self.state[SHIP]["thrust"] = 1.0
        if "S15.M16" in self.mut:
            self.sample(T.POWER_UP, SHIP, True, 34000, before=True)
        self.state[SHIP]["thrust"] = 0.0
        if "S15.M16" in self.mut:
            self.sample(T.POWER_UP, SHIP, False, 37000, before=True)
        self.leave(SHIP, 40000)
        # the room (a large grid): welcome by room, a reactor placed with its fuel (up)
        self.board(ROOM, "room", 60000, large=True)
        self.power_row(ROOM, 63000)
        self.state[ROOM].update(reactors=1, uranium=5.0)
        self.power_row(ROOM, 63000, before=False)
        self.power(ROOM, 63167, powered=True)
        self.leave(ROOM, 70000)
        # the deck: never aboard (only S15.M2's box)
        if self.board(DECK, "box", 80000, large=True):
            self.leave(DECK, 85000)
        # the small sub (a battery: powered at boarding, the baseline): the Seamoth's welcome
        self.board(SUB, "seat", 100000, large=False, powered=True)
        self.leave(SUB, 110000)
        # de.s15 play welcome above water (forced): Subnautica must refuse it
        self.seq[T.WELCOME] += 1
        self.se_row("voice", evId=1, event="Welcome", grid=SUB, gridStr=str(SUB), ms=302000, result="forced", played=True, seq=self.seq[T.WELCOME], snBus=0.0)
        played = "S15.M7" in self.mut
        self.sn_row("voice", event="Welcome", evId=1, seq=self.seq[T.WELCOME], played=played, reason="played" if played else "seAudio", volume=1.6, snBus=0.0,
                    path="event:/sub/cyclops/AI_test", source="test", coalesced=0, dropped=0)
        # boarded with SE's audio live (above water), then the dive: no line (S15.M17 holds it and greets under water)
        self.board(SUB2, "seat", 400000, large=False, powered=True, bus=0.0)
        self.se_row("live", live=True, snBus=bus_live, voiceOn=True, ms=405000)
        if "S15.M17" in self.mut:
            self.held_release(405000, bus_live)
        self.leave(SUB2, 420000)
        self.sess.append({"ev": "s15_surface", "grid": str(SUB2)})
        # Subnautica's story: the Aurora's first warning triggered (skipped unless S15.M20), the Aurora held, the oxygen alert untouched
        on = "S15.M20" in self.mut
        self.sess.append({"ev": "s15_story", "key": STORY_KEY})
        self.sn_row("story", key=STORY_KEY, type="PDA", skipped=not on, path="event:/player/story/Aurora_warning1", via="game", mutant=on)
        if on:
            self.sn_row("pda", sound="event:/player/story/Aurora_warning1", subtitles=STORY_KEY, storyKey=False, mutant=True)
        self.sn_row("pda", sound="event:/player/oxygen_low_10", subtitles=None, storyKey=False, mutant=on)   # a system line: fine
        self.sn_row("aurora", state="running" if on else "held", countdownS=1e9 if not on else 3000.0, warningS=1e9 if not on else 100.0, exploded=False,
                    countdownPlaying=False, explodePlaying=False, timePassed=800.0, mutant=on)
        self.sn_row("alert", found=True, enabled=True, alerts=2)

    def sky(self):
        names = ["DE_4546B", "Moon"]
        self.se_row("skylist", listSeq=1, count=2, bodies=[{"name": n} for n in names])
        self.sn_row("skylist", listSeq=1, count=2, names=["DE_4546B (bound)", "Moon"])
        for k in range(6):
            tick = 600 + 60 * k
            cam = T.wm.unit((0.2, 0.3 + 0.01 * k, 0.93))
            ang = 1.2
            self.se_row("sky", tick=tick, still=True, bodies=[{"name": "Moon", "cam": list(cam), "angDeg": ang}])
            sn_cam = T.wm.unit((cam[0] + 0.001, cam[1], cam[2]))
            if "S15.M3" in self.mut:
                sn_cam = T.wm.unit((cam[0] + 0.3, cam[1] - 0.2, cam[2]))
            sn_ang = ang * (2.0 if "S15.M6" in self.mut else 1.01)
            self.sn_row("sky", tick=tick, bodies=[{"name": "Moon", "shown": True, "cam": list(sn_cam), "angDeg": sn_ang}])
        moon_on = "S15.M5" in self.mut
        for k in range(3):
            self.sn_row("moons", hidden=not moon_on, planetHidden=True, moonHidden=not moon_on)

    def water(self):
        m4 = "S15.M4" in self.mut
        self.se_row("water", applied=not m4, texture="JWater2" if m4 else T.WATER["material"], transparent="on" if m4 else "off",
                    before="JWater2", why="S15.M4 (the previous look set: JWater2, transparent on)" if m4 else None)
        self.se_row("site", hash="0x%016x" % T.PINNED_HASH)

    def capture(self, kind, img, **extra):
        path = os.path.join(self.folder, "captures", kind + ".png")
        _save(img, path)
        e = {"ev": "s15_capture", "kind": kind, "path": path, "written": True}
        e.update(extra)
        self.sess.append(e)

    def captures(self):
        cap = os.path.join(self.folder, "captures")
        m4 = "S15.M4" in self.mut
        sn = (123, 176, 234), (197, 241, 255), (129, 155, 187), (65, 97, 122)
        for name, cols in (("band-se", sn), ("band-sn", sn)):
            _save(BC._scene(160, 90, 0.45, *cols), os.path.join(cap, name + ".png"))

        def sea_px(x, y):
            if (x // 7 + y // 5) % 11 == 0:
                return CLOUD
            if m4:
                return JWATER2 if (x // 9 + y // 9) % 5 == 0 else SEE_THROUGH
            return SEA_OURS

        for km in AIR_KM:
            _save(_image(160, 90, sea_px), os.path.join(cap, "air-%d.png" % km))
        for km in ORBIT_KM:
            r = 40 if km == 600 else 50

            def px(x, y, r=r):
                d = math.hypot(x - 60, y - 60)
                if d > r:
                    return (178, 140, 120) if d <= r + 2 and x > 60 else (0, 0, 0)   # a warm rim on the sun's side
                return T.ENDING["limb"] if d >= 0.85 * r else sea_px(x, y)

            _save(_image(120, 120, px), os.path.join(cap, "orbit-%d.png" % km))
        moon_on = "S15.M5" in self.mut

        def night(x, y):
            if moon_on and math.hypot(x - 80, y - 45) <= 0.15 * 90:
                return (220, 220, 210)
            k = y / 89.0
            return tuple(round(a + (b - a) * k) for a, b in zip(NIGHT_TOP, NIGHT_LOW))

        self.sess.append({"ev": "s15_moon", "axis": [0.0, -0.6, -0.8]})
        self.capture("moon-a", _image(160, 90, night), dir=[0.0, 0.6, 0.8], via="sn")
        self.capture("moon-b", _image(160, 90, night), dir=[0.0, -0.6, -0.8], via="sn")
        disc = lambda x, y: (200, 200, 200) if math.hypot(x - 80, y - 45) <= 6 else (120, 170, 230)   # noqa: E731
        f = 45.0 / math.tan(math.radians(30.0))
        for kind in ("sky-se", "sky-sn"):
            self.capture(kind, _image(160, 90, disc), body="Moon", angDeg=math.degrees(math.atan(6.0 / f)), vFovDeg=60.0)

    def picks(self):
        """The kept picks in the plugins' row forms: the sonar, the fish, the night, the splash, the seabase."""
        m = self.mut
        for k in range(3):
            self.se_row("sonar", seq=k + 1, grid=SUB, gridStr=str(SUB), block="Battery Sonar", ms=500000 + 5000 * k)
            self.sn_row("sonar", seq=k + 1, played=True, fx="S15.M13" not in m, reason="played", path="event:/sub/cyclops/sonar")
        self.sn_row("fish", fast=1, maxSpeed=5.0, near=4, scared=0 if "S15.M15" in m else 4, fish=12, mutant="S15.M15" in m)
        self.sn_row("night", light=1.0, factor=1.0, dayScalar=0.5, mutant=False)
        self.sn_row("night", light=0.0, factor=1.0 if "S15.M11" in m else 0.4, dayScalar=0.0, mutant="S15.M11" in m)
        self.sn_row("splash", created="S15.M12" not in m, lens=True, y=50.0, mutant="S15.M12" in m)
        hum = 0 if "S15.M14" in m else 2
        self.sn_row("seabase", what="enter", inside=True, hum=hum, candidates=2, restored=0, paths="event:/env/base_amb", light=True, mutant="S15.M14" in m)
        self.sn_row("seabase", what="leave", inside=False, hum=0, candidates=2, restored=hum, paths="", light=False, mutant="S15.M14" in m)

    def write(self):
        self.script()
        self.picks()
        self.sky()
        self.water()
        self.captures()
        with open(os.path.join(self.folder, "s15-session.jsonl"), "w", encoding="utf-8") as f:
            for e in self.sess:
                f.write(json.dumps(e) + "\n")
        with open(os.path.join(self.folder, "se-synthetic.jsonl"), "w", encoding="utf-8") as f:
            for e in self.se:
                f.write(json.dumps(e) + "\n")
        with open(os.path.join(self.folder, "sn-synthetic.jsonl"), "w", encoding="utf-8") as f:
            for e in self.sn:
                f.write(json.dumps(e) + "\n")

def selftest() -> int:
    res = []

    def check(c, what):
        res.append(bool(c))
        print("  %s %s" % ("PASS" if c else "FAIL", what))

    print("S15 analyzer self-test (tests/s15/analyze_s15.py --selftest)")
    check(T.fnv1a64(T.canonical_text()) == T.PINNED_HASH, "the twin's canonical text is its pin")
    tmp = tempfile.mkdtemp(prefix="s15-selftest-")
    try:
        base = os.path.join(tmp, "base")
        _Synth(base).write()
        rows, _S = evaluate(base)
        st = {r["id"]: r for r in rows}
        for rid in ALL_ROWS:
            check(st.get(rid, {}).get("status") == "PASS", "clean session: %s %s (%s)" % (rid, st.get(rid, {}).get("status"), str(st.get(rid, {}).get("measured"))[:110]))
        check(all(st.get(r, {}).get("status") == "INFO" for r in ("S15.MOONS.READ", "S15.SKYPX", "S15.WATER", "S15.LIMB", "S15.PATHS")),
              "the information rows: %s" % ",".join("%s=%s" % (r, st.get(r, {}).get("status")) for r in INFO_ROWS))
        p = st.get("S15.POWER", {}).get("measured", "")
        check("ship:fuel" in p and "ship:switch" in p and "room:reactor" in p, "the power causes read from SE's rows (%s)" % p[:90])
        for m, want in sorted(MUTANT_ROWS.items()):
            f = os.path.join(tmp, m.replace(".", "_"))
            _Synth(f, mutants=(m,)).write()
            rows, _S = evaluate(f)
            st = {r["id"]: r["status"] for r in rows}
            red = sorted(r for r in ALL_ROWS if st.get(r) == "FAIL")
            may = set(MAY_AFFECT.get(m, ()))
            check(set(want) <= set(red) <= set(want) | may and st.get("%s.red" % m) == "PASS",
                  "%s turns %s red (red: %s)" % (m, ",".join(want), ",".join(red) or "none"))
        # the colour rows go red on a wrong picture (the band pair and the orbit disc, seeded in the captures)
        bad = os.path.join(tmp, "colours")
        _Synth(bad).write()
        cap = os.path.join(bad, "captures")
        _save(BC._scene(160, 90, 0.45, (123, 176, 234), (197, 241, 255), (129, 155, 187), (25, 60, 110)), os.path.join(cap, "band-se.png"))   # SE's old near water
        _save(_image(120, 120, lambda x, y: (27, 68, 104) if math.hypot(x - 60, y - 60) <= 50 else (0, 0, 0)), os.path.join(cap, "orbit-100.png"))
        _save(_image(160, 90, lambda x, y: JWATER2), os.path.join(cap, "air-30.png"))
        rows, _S = evaluate(bad)
        st = {r["id"]: r["status"] for r in rows}
        check(st.get("S15.BAND") == "FAIL" and st.get("S15.ORBIT") == "FAIL" and st.get("S15.AIR") == "FAIL",
              "seeded wrong seas: BAND %s, ORBIT %s, AIR %s" % (st.get("S15.BAND"), st.get("S15.ORBIT"), st.get("S15.AIR")))
        # the sea seam: a step across the sea from 5 km (the look sheet 124136's) is red; the pair moves with the ground and goes with
 # the specular off (seeded: the step 51 px left after the 4 km move, f = 45 / tan 35 deg at 90 px high)
        seam = os.path.join(tmp, "seam")
        _Synth(seam).write()
        cap = os.path.join(seam, "captures")
        lit, dark = SEA_OURS, tuple(int(v * 0.6) for v in SEA_OURS)
        _save(_image(160, 90, lambda x, y: dark if x < 100 else lit), os.path.join(cap, "air-5.png"))
        rows, _S = evaluate(seam)
        st = {r["id"]: r for r in rows}
        check(st.get("S15.SEAM", {}).get("status") == "FAIL", "a seam across the sea from 5 km is red (%s)" % st.get("S15.SEAM", {}).get("measured"))
        _save(_image(160, 90, lambda x, y: SEA_OURS), os.path.join(cap, "air-5.png"))
        _save(_image(160, 90, lambda x, y: dark if x < 80 else lit), os.path.join(cap, "seam-5a.png"))
        _save(_image(160, 90, lambda x, y: dark if x < 29 else lit), os.path.join(cap, "seam-5b.png"))
        _save(_image(160, 90, lambda x, y: SEA_OURS), os.path.join(cap, "seam-5s.png"))
        rows, _S = evaluate(seam)
        st = {r["id"]: r for r in rows}
        pr = st.get("S15.SEAM.PAIR", {})
        check(st.get("S15.SEAM", {}).get("status") == "PASS" and pr.get("status") == "INFO" and "with the ground" in str(pr.get("measured")),
              "the seam pair: moved with the ground, gone with the specular off (%s)" % str(pr.get("measured"))[:160])
        # the music fade: a dip under water shorter than the crossfade turns the fade around in place (run 20261024-150805: 0.69 to
 # 0.6 in 625 ms): counted apart, never timed; the full swings take CrossfadeMs
        class _MixS:
            def __init__(self, mix):
                self._m = mix

            def rows(self, side, name):
                return list(self._m) if (side, name) == ("sn", "s15.mix") else []

        mix, lvl = [], 1.0
        for t in range(0, 20000, 250):
            above = 2000 <= t < 9000 or 9500 <= t < 16000
            mix.append({"ms": t, "s2Target": 0.0 if above else 1.0, "music": round(lvl, 4), "voice": round(lvl, 4), "musicAbove": 0.6, "voiceAbove": 0.6,
                        "sfx": 0.0 if above else 1.0, "active": True, "muted": False, "muteWhy": "audible", "why": "groups"})
            target, step = (0.6 if above else 1.0), 250.0 / T.MX["CrossfadeMs"] * 0.4
            lvl = max(target, lvl - step) if lvl > target else min(target, lvl + step)
        rr = []
        check_music(_MixS(mix), rr)
        check(bool(rr) and rr[0]["status"] == "PASS" and "1 fades from a partial level" in rr[0]["measured"],
              "MUSIC: a dip shorter than the crossfade counted apart (%s)" % (rr[0]["measured"] if rr else "no row"))
        # the gate replay: a return within 5 s is no event; an edge with SE's audio live skipped and never replayed; a baseline
        g = [{"evId": 1, "grid": 5, "on": True, "ms": 0, "snBus": 1.0}, {"evId": 1, "grid": 5, "on": False, "ms": 1000, "lastOnMs": 900, "snBus": 1.0},
             {"evId": 1, "grid": 5, "on": True, "ms": 3000, "snBus": 1.0}]
        check(replay_gate(g) == [(1, "5", 0, T.PLAYED)], "replay: a return within 5 s is no new event")
        held = [{"evId": 4, "grid": 5, "on": True, "ms": 0, "snBus": 0.0}, {"evId": 4, "grid": 5, "on": False, "ms": 20000, "lastOnMs": 19833, "snBus": 1.0}]
        check(replay_gate(held) == [(4, "5", 0, T.SEAUDIO)], "replay: boarded with SE's audio live, then the dive: skipped, never replayed")
        base_rows = [{"evId": 2, "grid": 5, "on": True, "ms": 0, "snBus": 1.0, "baseline": True},
                     {"evId": 2, "grid": 5, "on": False, "ms": 4000, "lastOnMs": 3833, "snBus": 1.0},
                     {"evId": 2, "grid": 5, "on": True, "ms": 9000, "snBus": 1.0}]
        check(replay_gate(base_rows) == [(2, "5", 9000, T.PLAYED)], "replay: a powered boarding is the baseline, the next power edge plays")
        # the disc finder and the face classes
        img = _image(100, 100, lambda x, y: (65, 97, 122) if math.hypot(x - 50, y - 50) <= 30 else (0, 0, 0))
        m = measure_disc(img)
        check(m is not None and abs(m[2] - 30) < 2 and all(abs(a - b) < 1 for a, b in zip(m[0], (65, 97, 122))), "the disc finder: radius and centre colour")
        see = _image(100, 100, lambda x, y: SEE_THROUGH if (x + y) % 7 else SEA_OURS)
        ok, txt = judge_face(face_stats(see, 50, 50, 30), T.ENDING["sea"])
        check(not ok and "seabed 8" in txt, "the owner's see-through 30 km look is red (%s)" % txt)
        check(disc_px(_image(160, 90, lambda x, y: (200, 200, 200) if math.hypot(x - 80, y - 45) <= 10 else (120, 170, 230))) is not None,
              "the sky-size disc finder")
        empty = os.path.join(tmp, "empty")
        os.makedirs(empty)
        rows, _S = evaluate(empty)
        check(all(r["status"] == "MISSING" for r in rows if r["id"] in ALL_ROWS) and not C.verdict(rows, True) and C.verdict(rows, False),
              "an empty folder: every row MISSING (strict FAIL, lenient PASS)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    ok = all(res)
    print("S15 ANALYZER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(res), len(res)))
    return 0 if ok else 1

def main(argv):
    import argparse
    ap = argparse.ArgumentParser(description="S15 oracle table")
    ap.add_argument("folder", nargs="?")
    ap.add_argument("--se-log", action="append", default=[])
    ap.add_argument("--sn-log", action="append", default=[])
    ap.add_argument("--captures", default=None)
    ap.add_argument("--lenient", action="store_true")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.folder and not (a.se_log or a.sn_log):
        ap.print_help()
        return 2
    rows, _S = evaluate(a.folder, a.se_log, a.sn_log, a.captures)
    ok = write_results(a.folder, rows, strict=not a.lenient)
    print(json.dumps(rows, indent=1, default=str) if a.json else C.table(rows))
    print("S15 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
