"""S16 conventions, the Python twin of the Wire depth-progression rules (src/DeepEngineers.Wire/S16Conventions.cs,
S16Rules.cs): the canonical text and its hash (`ext s16 depth 11`; CONTRACT §5.4 "S16 conventions"), the block class, the
exposure of a block to water (the S7 air rule), the hull ladder and a grid's crush depth, the damage curve past the limit,
the creak level and cadence, the voice rule (arming and the once-per-cooldown rule shared with S15), the HUD state and the
diver's breathing multiplier, and the C5 extension codec. Shares no code with the C#; the vectors it writes under
tests/s16/vectors/ are read by the C# tests (S16RulesTests in tests/DeepEngineers.Wire.Tests).

Usage (repository root; stdlib only):
 python -I tools/s16/s16_ref.py --selftest every rule's hand checks, the pin, the CONTRACT block, vectors current
 python -I tools/s16/s16_ref.py --vectors (re)writes tests/s16/vectors/{classify,exposure,crush,damage,creak,voice,hud,wire}.tsv
 python -I tools/s16/s16_ref.py --text prints the canonical text
 python -I tools/s16/s16_ref.py --pin prints hash, lines, bytes
 python -I tools/s16/s16_ref.py --ladder prints the ladder and the curves as written
"""
import math
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
VEC_DIR = os.path.join(ROOT, "tests", "s16", "vectors")
CONTRACT = os.path.join(ROOT, "docs", "CONTRACT.md")

# =====================================================================================================================
# the tables (CONTRACT §5.4 "S16 conventions"; the same numbers as S16Conventions.cs)
# =====================================================================================================================
HEADER = "ext s16 depth 11"
PINNED_HASH = 0xbad78a2237d74b6d
CANON_LINES, CANON_BYTES = 118, 8516

SMALL, LARGE = 1, 2
SIZES = {SMALL: "Small", LARGE: "Large"}
LIGHT, HEAVY, FITTING = 1, 2, 3
CLASSES = ["", "LightArmor", "HeavyArmor", "Fitting"]
HULL = [(SMALL, LIGHT, 350), (SMALL, HEAVY, 500), (LARGE, LIGHT, 800), (LARGE, HEAVY, 1200)]
TIERS = [(1, "DE_PressureHullMk1", 1400, "Cobalt,Magnesium,Silver"),   # Mk3 gone)
         (2, "DE_PressureHullMk2", 2000, "Gold,Platinum,Uranium")]
ALLOY_PREFIX = "DE_HullAlloyMk"
DAMAGE = [("PeriodTicks", 60), ("BaseMilliHpS", 10000), ("PerMetreMilliHpS", 1000), ("CapMilliHpS", 100000),
          ("MaxBlocksPerPeriod", 8), ("KSmallPermille", 1000), ("KLargePermille", 25000), ("GraceTicks", 3600)]
D = dict(DAMAGE)
SE_TYPE = "DeepPressure"
CREAK = [("BandM", 50), ("Level2M", 25), ("PeriodTicks1", 540), ("PeriodTicks2", 360), ("PeriodTicks3", 180)]
K = dict(CREAK)
VOICE = [("CooldownMs", 20000), ("RearmM", 10), ("RepeatMs", 20000), ("VolumePermille", 1600), ("DuckMs", 3500), ("DuckPermille", 250)]
V = dict(VOICE)
HUD_STATES = ["off", "safe", "creak", "limit", "damage"]
OFF, SAFE, CREAKING, LIMIT, DAMAGED = 0, 1, 2, 3, 4
HUD = [("RefreshTicks", 30), ("AliveMs", 1500), ("MinDepthDm", 5), ("TopPermille", 60), ("CautionRgb", 0xFFC040), ("FitPermille", 900), ("MinFontPermille", 250), ("SeatedLongDigits", 6), ("CentreTolPermille", 50), ("RefWidth", 1920), ("RefHeight", 1080),
       ("MaxPartPermille", 500)]
DAMAGE_GATES = ["none", "destructible", "indestructible", "off"]
GATE_NONE, GATE_DESTRUCTIBLE, GATE_INDESTRUCTIBLE, GATE_OFF = 0, 1, 2, 3
SE_LINES = ["none", "grid", "suppressed"]
LINE_NONE, LINE_GRID, LINE_SUPPRESSED = 0, 1, 2
TONES = ["normal", "caution", "danger"]
TONE_NORMAL, TONE_CAUTION, TONE_DANGER = 0, 1, 2
HU = dict(HUD)
DIVER = [("UnsafeDepthM", 100), ("CrushDepthM", 200), ("BreathX10Safe", 10), ("BreathX10Unsafe", 15), ("BreathX10Crush", 20)]
DV = dict(DIVER)
C5 = [("Offset", 0x80), ("Size", 0x2A), ("Version", 2)]
C5D = dict(C5)
C5FLAGS = [("Grid", 1), ("Diver", 2), ("DamageOn", 4), ("Docked", 8), ("Exempt", 16), ("Grace", 32)]
FLAG_GRID, FLAG_DIVER, FLAG_DAMAGE, FLAG_DOCKED, FLAG_EXEMPT, FLAG_GRACE = 1, 2, 4, 8, 16, 32
C5FIELDS = [("version", 0x80, 1, "u8"), ("flags", 0x81, 1, "u8"), ("creakLevel", 0x82, 1, "u8"), ("hullCode", 0x83, 1, "u8"),
            ("tier", 0x84, 1, "u8"), ("breathX10", 0x85, 1, "u8"), ("hudState", 0x86, 2, "u16"), ("depthM", 0x88, 4, "f32"),
            ("crushM", 0x8c, 4, "f32"), ("creakSeq", 0x90, 4, "u32"), ("voiceSeq", 0x94, 4, "u32"), ("damageSeq", 0x98, 4, "u32"),
            ("worldEpoch", 0x9c, 4, "u32"), ("gridId", 0xa0, 8, "i64"), ("hullPct", 0xa8, 1, "u8"), ("powerPct", 0xa9, 1, "u8")]
C7 = [("Offset", 0xBC), ("Size", 4), ("Version", 1)]
C7D = dict(C7)
C7FLAGS = [("CompassOk", 1), ("SeamothOk", 2), ("DiverShown", 4), ("GridShown", 8)]
COMPASS_OK, SEAMOTH_OK, DIVER_SHOWN, GRID_SHOWN = 1, 2, 4, 8
C7FIELDS = [("c7version", 0xbc, 1, "u8"), ("c7flags", 0xbd, 1, "u8"), ("c7reserved", 0xbe, 2, "u16")]
VIEW_SPACE, VIEW_APPROACH, VIEW_UNDERWATER, VIEW_SURFACING = 0, 1, 2, 3
FORMATS = [
    "block.class HeavyArmor: definition EdgeType Heavy; LightArmor: TypeId CubeBlock and SubtypeId contains Armor (ordinal, ignoring case); every other block (windows, doors, cockpits, vents, connectors) a Fitting; the tier blocks are Fittings that set the tier while ComponentStack.IsFunctional",
    "c5.s16 SeState 0x80-0xa9; version 0 = absent; creakSeq, voiceSeq, damageSeq count events (u32, never reset in an SE session); a reader takes its first value as the baseline and plays one sound per increment seen in the same worldEpoch",
    "c7.s16 SnStatus 0xbc-0xbf written by Subnautica every frame; version 0 = absent; SE hides its grid line while GridShown is set in a fresh record (DiverShown only reports Subnautica's diver depth meter)",
    "hud.se SE's HUD text line: None for the diver (under water Subnautica's compass shows the depth, above water nothing); for a seated grid Grid, Suppressed while Subnautica shows its readouts (GridShown in a fresh C7 record)",
    "crush.depth max(min over counted blocks of hull(size, class), crushM of the best functional tier block or 0); no counted block: hull(size, LightArmor); a static grid (a station) is never tracked nor rated: no crush depth, no damage, seated in its cockpit the diver readout",
    "creak.cadence one creak when the level becomes >= 1 or rises, then one every PeriodTicks(level of the last creak) while the level stays >= 1",
    "creak.level d = the HUD grid's lowest point depth; 0 if d < L - BandM, 1 if d < L - Level2M, 2 if d <= L, 3 if d > L",
    "damage.block every PeriodTicks per grid (first period PeriodTicks + id mod PeriodTicks after it is tracked), none during GraceTicks after S16 becomes active: the MaxBlocksPerPeriod deepest damage-set blocks whose centre depth d > L each take MySlimBlock.DoDamage(min(Cap, Base + PerMetre * (d - L)) * PeriodTicks / 60 * k(size), DeepPressure, sync, no hit info, attacker 0); the damage set is the counted blocks, or every block with a wet face when none counts",
    "exposure a face-neighbour cell outside the block is wet when empty and not in an airtight room (MyGridGasSystem.GetOxygenRoomForCubeGridPosition, IsAirtight), dry when empty and in one; armour counts when a neighbour is wet, a Fitting when one is wet and one dry, as the majority class of the armour cells it touches (HeavyArmor only with more heavy than light cells, else LightArmor)",
    "group.crush L = max(crush.depth, crushM of the best functional tier block of the mechanical group); docked (a Physical or NoContactDamage link to a larger grid outside the mechanical group): L = max(L, the carrier's L), no damage while every block centre lies in an airtight room of the carrier",
    "hud.diver no grid: depth = the eye; breathX10 = BreathX10Crush if d > CrushDepthM, BreathX10Unsafe if d > UnsafeDepthM, else BreathX10Safe; no crush depth for the diver",
    "hud.hull hullPct = ceil(100 * sum(Integrity - AccumulatedDamage) / the largest sum(MaxIntegrity) seen) over the damage set; powerPct = ceil(100 * stored / capacity) over the grid's batteries, 255 without one",
    "hud.sn Subnautica shows its own HUD while the picture is its own (C5 viewState Underwater or Surfacing) and hudState is not Off: uGUI_DepthCompass's player readout (shadow, halfMoon, depthText, suffixText); the diver: depthText = depth, suffixText = MeterSuffix; seated, the Cyclops helm's form depth / crush (CyclopsHelmHUDManager.cs:106-119 '{depth}m / {crush}m') in one field: depthText = depth + ' ' + MeterSuffix + ' / ' + crushM + ' ' + MeterSuffix, or depth + ' / ' + crushM + ' ' + MeterSuffix when the two numbers have more than SeatedLongDigits digits together, crushM <= 0 (no crush depth): depth + ' ' + MeterSuffix only; suffixText off, no word wrapping, overflow, out of any layout group, centred on the compass (|text centre - half-moon centre| <= CentreTolPermille / 1000 of the compass width), its font size measured: min(its own, its own x target / the preferred width at its own), at least MinFontPermille / 1000 of its own, target = the compass width x FitPermille / 1000; it fits when TMP renders one line no wider than the target (live textInfo.lineCount, textBounds after ForceMeshUpdate); its tone Normal for Safe, Caution (CautionRgb) for Creak, Danger (textColorDanger, halfMoonDanger) for Limit and Damage; no Seamoth panel",
    "hud.state Off if d < MinDepthDm / 10; else Damage if d > L and damage applies, Limit if d > L, Creak if d >= L - BandM, Safe",
    "voice.rule one hullCritical event when d > L while armed (disarmed by it, armed again when d < L - RearmM) and one more every RepeatMs while d stays > L; it plays when the same grid played none within CooldownMs",
    "voice.mix Subnautica plays the voice at VolumePermille / 1000 and every S16 creak and damage groan that starts within DuckMs after a voice at DuckPermille / 1000 of its own volume",
    "damage.gate crush damage applies when the world's DestructibleBlocks is on, whatever the game mode (Creative included), unless de.s16 damage is off (the dev override): Destructible, Indestructible, Off, None without a session; SE's DoDamage also honours the grid's own destructible flag (MyCubeGrid.BlocksDestructionEnabled)",
    "hud.place the canvas scale is min(W / RefWidth, H / RefHeight) x uiScale; an element is moved so that the bounds of its readout (the shown mode's own fields and the graphics on their parents up to the element; a part wider than W x MaxPartPermille / 1000 or taller than H x MaxPartPermille / 1000 is a backdrop and left out; canvas units, origin the canvas centre) have its top-centre at (0, H/2 - H x TopPermille / 1000), then shifted back inside the canvas; on screen (pixels, top-left origin) x = (u + W/2) x scale, y = (H/2 - v) x scale",
]

def canonical_text():
    out = [HEADER]

    def c(group, name, v):
        out.append("const %s %s %d" % (group, name, v))

    c("S16Size", "Small", SMALL)
    c("S16Size", "Large", LARGE)
    for i in range(1, 4):
        c("S16Class", CLASSES[i], i)
    for s, cl, m in HULL:
        out.append("hull %s %s crushM %d" % (SIZES[s], CLASSES[cl], m))
    for t, sub, m, ing in TIERS:
        out.append("tier %d %s crushM %d ingots %s" % (t, sub, m, ing))
    out.append("const S16Alloy %s %d" % (ALLOY_PREFIX, len(TIERS)))
    for n, v in DAMAGE:
        c("Damage", n, v)
    out.append("const Damage SeType %s" % SE_TYPE)
    for n, v in CREAK:
        c("Creak", n, v)
    for n, v in VOICE:
        c("Voice", n, v)
    for i, n in enumerate(HUD_STATES):
        c("HudState", n, i)
    for n, v in HUD:
        c("Hud", n, v)
    for i, n in enumerate(DAMAGE_GATES):
        c("DamageGate", n, i)
    for i, n in enumerate(SE_LINES):
        c("SeLine", n, i)
    for i, n in enumerate(TONES):
        c("Tone", n, i)
    for n, v in DIVER:
        c("Diver", n, v)
    for n, v in C5:
        c("C5", n, v)
    for n, v in C5FLAGS:
        c("C5Flags", n, v)
    for n, off, size, typ in C5FIELDS:
        out.append("field s16 %s 0x%x %d %s" % (n, off, size, typ))
    for n, v in C7:
        c("C7", n, v)
    for n, v in C7FLAGS:
        c("C7Flags", n, v)
    for n, off, size, typ in C7FIELDS:
        out.append("field s16 %s 0x%x %d %s" % (n, off, size, typ))
    for f in sorted(FORMATS):
        out.append("format " + f)
    return "\n".join(out) + "\n"

def fnv1a64(data):
    h = 0xcbf29ce484222325
    for b in data:
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def text_hash():
    t = canonical_text()
    return fnv1a64(t.encode("ascii")), t.count("\n"), len(t.encode("ascii"))

# =====================================================================================================================
# block class, exposure, ladder
# =====================================================================================================================
def class_of(type_id, subtype, edge):
    if edge == "Heavy":
        return HEAVY
    t = type_id or ""
    if t.startswith("MyObjectBuilder_"):
        t = t[len("MyObjectBuilder_"):]
    if t == "CubeBlock" and "armor" in (subtype or "").lower():
        return LIGHT
    return FITTING

def tier_of(subtype):
    for t, sub, _m, _i in TIERS:
        if subtype in (sub + "Large", sub + "Small"):
            return t
    return 0

WET, DRY = 1, 2

def exposure_of(mn, mx, block_at, airtight, full=False):
    """(bits WET|DRY, light neighbours, heavy neighbours) over the face-neighbour cells of the box mn..mx, as
 S16Exposure.Of: block_at(cell) is the class of the block there (0 empty); stops at WET|DRY unless full."""
    bits, light, heavy = 0, 0, 0
    for face in range(6):
        if not full and bits == WET | DRY:
            break
        axis, plus = face >> 1, face & 1
        fixed = (mx[axis] + 1) if plus else (mn[axis] - 1)
        oa = [i for i in range(3) if i != axis]
        for a in range(mn[oa[0]], mx[oa[0]] + 1):
            for b in range(mn[oa[1]], mx[oa[1]] + 1):
                if not full and bits == WET | DRY:
                    break
                cell = [0, 0, 0]
                cell[axis], cell[oa[0]], cell[oa[1]] = fixed, a, b
                cell = tuple(cell)
                cl = block_at(cell)
                if cl:
                    if cl == LIGHT:
                        light += 1
                    elif cl == HEAVY:
                        heavy += 1
                    continue
                bits |= DRY if airtight(cell) else WET
    return bits, light, heavy

def counts(cls, exp):
    if cls in (LIGHT, HEAVY):
        return bool(exp & WET)
    return bool(exp & WET) and bool(exp & DRY)

def rated_class(cls, light_nbr, heavy_nbr):
    if cls in (LIGHT, HEAVY):
        return cls
    return HEAVY if heavy_nbr > light_nbr else LIGHT

def hull_crush(size, cls):
    for s, c, m in HULL:
        if s == size and c == cls:
            return m
    return 0

def tier_crush(tier):
    for t, _s, m, _i in TIERS:
        if t == tier:
            return m
    return 0

def hull_of(summary):
    """(hull depth, weakest class) of a summary dict {size, light, heavy, fitting, tier}."""
    best, cls = None, 0
    for c, n in ((LIGHT, summary["light"]), (HEAVY, summary["heavy"])):
        if n > 0:
            m = hull_crush(summary["size"], c)
            if best is None or m < best or (m == best and c < cls):
                best, cls = m, c
    if best is None:
        return hull_crush(summary["size"], LIGHT), LIGHT
    return best, cls

def crush_depth_of(summary):
    return max(hull_of(summary)[0], tier_crush(summary.get("tier", 0)))

def effective_crush(own, group_tier, docked, carrier_crush):
    """format group.crush."""
    lim = max(own, tier_crush(group_tier))
    return max(lim, carrier_crush) if docked else lim

def damage_applies(docked, inside):
    return not (docked and inside)

def damage_set_is_wet(summary):
    return summary["light"] + summary["heavy"] == 0

def hull_code(size, cls):
    if size not in (1, 2) or cls not in (1, 2, 3):
        return 0
    return (size - 1) * 3 + cls

def summarise(size, blocks, airtight_cells):
    """A synthetic grid: blocks = [(min, max, cls, tier, functional)], airtight_cells = set of cells in airtight rooms."""
    cls_at = {}
    for mn, mx, c, _t, _f in blocks:
        for x in range(mn[0], mx[0] + 1):
            for y in range(mn[1], mx[1] + 1):
                for z in range(mn[2], mx[2] + 1):
                    cls_at[(x, y, z)] = c
    s = {"size": size, "light": 0, "heavy": 0, "fitting": 0, "wet": 0, "tier": 0, "blocks": len(blocks)}
    for mn, mx, cls, tier, functional in blocks:
        if tier and functional:
            s["tier"] = max(s["tier"], tier)
        e, ln, hn = exposure_of(mn, mx, lambda c: cls_at.get(c, 0), lambda c: c in airtight_cells, full=cls == FITTING)
        if e & WET:
            s["wet"] += 1
        if counts(cls, e):
            s["heavy" if rated_class(cls, ln, hn) == HEAVY else "light"] += 1
            if cls == FITTING:
                s["fitting"] += 1
    return s

# =====================================================================================================================
# damage, creaks, voice, HUD
# =====================================================================================================================
def damage_per_second(depth, limit):
    if not depth > limit:
        return 0.0
    return min(D["CapMilliHpS"] / 1000.0, D["BaseMilliHpS"] / 1000.0 + D["PerMetreMilliHpS"] / 1000.0 * (depth - limit))

def k_of(size):
    return (D["KLargePermille"] if size == LARGE else D["KSmallPermille"]) / 1000.0

def per_period_se(depth, limit, size):
    return damage_per_second(depth, limit) * (D["PeriodTicks"] / 60.0) * k_of(size)

def pick(depths, limit, mx=None):
    mx = D["MaxBlocksPerPeriod"] if mx is None else mx
    idx = [i for i, d in enumerate(depths) if d > limit]
    idx.sort(key=lambda i: (-depths[i], i))
    return idx[:mx]

def creak_level(depth, limit, band=None):
    band = K["BandM"] if band is None else band
    if depth > limit:
        return 3
    if depth < limit - band:
        return 0
    if depth < limit - K["Level2M"]:
        return 1
    return 2

def creak_period(level):
    return {1: K["PeriodTicks1"], 2: K["PeriodTicks2"], 3: K["PeriodTicks3"]}.get(level, 0)

REASONS = ["none", "enter", "rise", "period"]

class CreakClock:
    def __init__(self):
        self.active, self.last_tick, self.last_level = False, 0, 0

    def step(self, tick, level):
        if level <= 0:
            self.active = False
            return 0
        if not self.active:
            why = 1
        elif level > self.last_level:
            why = 2
        elif tick >= self.last_tick + creak_period(self.last_level):
            why = 3
        else:
            return 0
        self.active, self.last_tick, self.last_level = True, tick, level
        return why

class VoiceArm:
    """format voice.rule: the crossing, then one every RepeatMs while past the limit; re-armed RearmM above it."""
    def __init__(self):
        self.armed = True
        self.last_ms = 0

    def step(self, depth, limit, now_ms):
        if self.armed and depth > limit:
            self.armed, self.last_ms = False, now_ms
            return True
        if self.armed:
            return False
        if depth < limit - V["RearmM"]:
            self.armed = True
            return False
        if depth > limit and now_ms - self.last_ms >= V["RepeatMs"]:
            self.last_ms = now_ms
            return True
        return False

def voice_volume():
    return V["VolumePermille"] / 1000.0

def effect_volume(volume, ms_since_voice):
    """format voice.mix: a creak or groan ms_since_voice after the last voice play (negative: none yet)."""
    return volume * (V["DuckPermille"] / 1000.0) if 0 <= ms_since_voice < V["DuckMs"] else volume

PLACE_TOP_CENTRE = 1

def tone(hud_state_v):
    """format hud."""
    if hud_state_v in (LIMIT, DAMAGED):
        return TONE_DANGER
    return TONE_CAUTION if hud_state_v == CREAKING else TONE_NORMAL

def seated_text(depth_m, crush_m, meter="m"):
    """format hud.sn (depth 7/8): the whole seated readout in one field; the short form above SeatedLongDigits digits."""
    m = (meter or "m").strip() or "m"
    if crush_m <= 0:
        return "%d %s" % (depth_m, m)  # no crush depth (a station): the depth only
    if len(str(depth_m)) + len(str(crush_m)) > HU["SeatedLongDigits"]:
        return "%d / %d %s" % (depth_m, crush_m, m)
    return "%d %s / %d %s" % (depth_m, m, crush_m, m)

def seated_target(compass_w):
    return compass_w * HU["FitPermille"] / 1000.0

def seated_font_size(own, preferred_w, target_w):
    """format hud.sn (depth 8): the measured font size, floored at MinFontPermille of the field's own."""
    floor = own * HU["MinFontPermille"] / 1000.0
    size = own * target_w / preferred_w if preferred_w > target_w and preferred_w > 0 else own
    return max(floor, min(own, size))

def seated_centred(centre_dx, compass_w):
    """format hud.sn (depth 9): the seated text centred on the compass (within CentreTolPermille of its width)."""
    return compass_w > 0 and abs(centre_dx) <= compass_w * HU["CentreTolPermille"] / 1000.0

def seated_text_fits(line_count, text_w, compass_w):
    """format hud.sn: the live TMP check: one line, no wider than the target (FitPermille of the compass, half a unit of slack)."""
    return line_count == 1 and text_w <= seated_target(compass_w) + 0.5

# the S16b session of (depth 7): the compass 156 canvas units wide, the field's own font 56, TMP's width per character
# and font unit (176.2 units for the 15 characters of "1603 m / 1700 m" at 22.4)
COMPASS_W, OWN_FONT, CHAR_W = 156.0, 56.0, 176.2 / 15.0 / 22.4

def to_screen_px(mn_x, mn_y, mx_x, mx_y, w, h, scale):
    """format hud.place: canvas units (origin the centre, y up) to screen pixels (top-left origin)."""
    return ((mn_x + w / 2.0) * scale, (h / 2.0 - mx_y) * scale, (mx_x + w / 2.0) * scale, (h / 2.0 - mn_y) * scale)

def damage_gate(have_session, destructible_blocks, switch_on):
    """format damage.gate: destructible blocks, whatever the game mode; de.s16 damage off overrides."""
    if not have_session:
        return GATE_NONE
    if not switch_on:
        return GATE_OFF
    return GATE_DESTRUCTIBLE if destructible_blocks else GATE_INDESTRUCTIBLE

def readout_bounds(rects, w, h):
    """format hud.place: (minX, minY, maxX, maxY, used, backdrops) of the parts no larger than MaxPartPermille of the canvas; None when none."""
    mw, mh = w * HU["MaxPartPermille"] / 1000.0, h * HU["MaxPartPermille"] / 1000.0
    used = [r_ for r_ in rects if r_[2] - r_[0] <= mw and r_[3] - r_[1] <= mh]
    if not used:
        return None
    return (min(q[0] for q in used), min(q[1] for q in used), max(q[2] for q in used), max(q[3] for q in used), len(used), len(rects) - len(used))

# (shadow, halfMoon, depthText, suffixText) far off the canvas before the first fit (moved 2,440, -1,348); depth 6 shows the same
# readout seated with the longer suffix "m / 400m" (canvas 1920 x 1080 units)
BACKDROP = (-1888.0, -1042.0, 1888.0, 1042.0)
READOUTS = {"diver": [BACKDROP, (-2540.0, 1820.0, -2340.0, 1925.0), (-2530.0, 1830.0, -2350.0, 1920.0), (-2500.0, 1840.0, -2420.0, 1890.0),
                      (-2420.0, 1840.0, -2380.0, 1890.0)],
            "seated": [BACKDROP, (-2540.0, 1820.0, -2340.0, 1925.0), (-2530.0, 1830.0, -2350.0, 1920.0), (-2500.0, 1840.0, -2420.0, 1890.0),
                       (-2420.0, 1840.0, -2280.0, 1890.0)]}
RESOLUTIONS = ((2560, 1440), (1920, 1080))

def place_readout(anchor, rects, w, h):
    """The readout's bounds after the move (format hud.place), or None."""
    b = readout_bounds(rects, w, h)
    if b is None:
        return None
    dx, dy = hud_place(anchor, b[0], b[1], b[2], b[3], w, h)
    return (b[0] + dx, b[1] + dy, b[2] + dx, b[3] + dy)

def canvas_scale(w, h, ui_scale):
    """format hud.place: Subnautica's HUD scale (uGUI_CanvasScaler.UpdateFrustum)."""
    if w <= 0 or h <= 0:
        return 1.0
    return min(w / float(HU["RefWidth"]), h / float(HU["RefHeight"])) * (ui_scale if ui_scale > 0 else 1.0)

def hud_place(anchor, mn_x, mn_y, mx_x, mx_y, w, h):
    """format hud.place: (dx, dy) in canvas units (origin the centre, y up) putting the content bounds at the anchor."""
    dx = 0.0 - (mn_x + mx_x) / 2.0            # depth 6: one anchor, the top centre
    dy = (h / 2.0 - h * HU["TopPermille"] / 1000.0) - mx_y
    if mx_x - mn_x <= w:
        if mn_x + dx < -w / 2.0:
            dx = -w / 2.0 - mn_x
        if mx_x + dx > w / 2.0:
            dx = w / 2.0 - mx_x
    if mx_y - mn_y <= h:
        if mn_y + dy < -h / 2.0:
            dy = -h / 2.0 - mn_y
        if mx_y + dy > h / 2.0:
            dy = h / 2.0 - mx_y
    return dx, dy

class VoiceRule:
    def __init__(self, cooldown_ms=None):
        self.cooldown = V["CooldownMs"] if cooldown_ms is None else cooldown_ms
        self.last = {}

    def try_fire(self, key, now_ms):
        if key in self.last and now_ms - self.last[key] < self.cooldown:
            return False
        self.last[key] = now_ms
        return True

def hud_state(depth, limit, damage_on):
    if depth < HU["MinDepthDm"] / 10.0:
        return OFF
    if depth > limit:
        return DAMAGED if damage_on else LIMIT
    if depth >= limit - K["BandM"]:
        return CREAKING
    return SAFE

def hull_pct(integrity, max_seen):
    if not max_seen > 0:
        return 100
    return int(max(0, min(100, math.ceil(100.0 * integrity / max_seen - 1e-9))))

def power_pct(stored, capacity):
    if not capacity > 0:
        return 255
    return int(max(0, min(100, math.ceil(100.0 * stored / capacity - 1e-9))))

def se_line_suppressed(c7_fresh, c7_flags, grid_mode):
    if not c7_fresh:
        return False
    return bool(c7_flags & (GRID_SHOWN if grid_mode else DIVER_SHOWN))

def se_line(c7_fresh, c7_flags, grid_mode):
    """format hud.se: none for the diver; for a seated grid the line unless Subnautica shows its readouts."""
    if not grid_mode:
        return LINE_NONE
    return LINE_SUPPRESSED if se_line_suppressed(c7_fresh, c7_flags, True) else LINE_GRID

SN_HUD_NONE, SN_HUD_DIVER, SN_HUD_GRID = 0, 1, 2

def sn_hud_mode(have, flags, hud_state_v, view_state):
    if not have or hud_state_v == OFF:
        return SN_HUD_NONE
    if view_state not in (VIEW_UNDERWATER, VIEW_SURFACING):
        return SN_HUD_NONE
    if flags & FLAG_GRID:
        return SN_HUD_GRID
    if flags & FLAG_DIVER:
        return SN_HUD_DIVER
    return SN_HUD_NONE

def breath_x10(depth):
    if depth > DV["CrushDepthM"]:
        return DV["BreathX10Crush"]
    if depth > DV["UnsafeDepthM"]:
        return DV["BreathX10Unsafe"]
    return DV["BreathX10Safe"]

# =====================================================================================================================
# C5 extension codec (SeState 0x80-0xa7) and the reader's counters
# =====================================================================================================================
WIRE_FMT = "<BBBBBBHffIIIIqBB"     # offsets 0x80.. as C5FIELDS

def wire_pack(flags, level, hull, tier, breath, state, depth, crush, creak_seq, voice_seq, damage_seq, epoch, grid, hull_pct_v=0, power_pct_v=0):
    return struct.pack(WIRE_FMT, C5D["Version"], flags, level, hull, tier, breath, state, depth, crush, creak_seq, voice_seq,
                       damage_seq, epoch, grid, hull_pct_v, power_pct_v)

def status_pack(flags):
    """The C7 S16 block (SnStatus 0xbc..0xbf)."""
    return struct.pack("<BBH", C7D["Version"], flags, 0)

def wire_unpack(b):
    if len(b) < C5D["Size"] or b[0] != C5D["Version"]:
        return None
    v = struct.unpack(WIRE_FMT, b[:C5D["Size"]])
    keys = [f[0] for f in C5FIELDS]
    return dict(zip(keys, v))

class SeqReader:
    def __init__(self):
        self.have, self.epoch, self.last, self.coalesced = False, 0, 0, 0

    def step(self, epoch, value):
        if not self.have or epoch != self.epoch:
            self.have, self.epoch, self.last = True, epoch, value
            return 0
        if value == self.last:
            return 0
        d = (value - self.last) & 0xFFFFFFFF
        self.last = value
        if d > 0x7FFFFFFF:
            return 0
        if d > 1:
            self.coalesced += d - 1
        return 1

# =====================================================================================================================
# vectors
# =====================================================================================================================
def r(x):
    return repr(float(x))

def _cells(s):
    return [tuple(int(v) for v in c.split(",")) for c in s.split(";") if c]

def exposure_cases():
    """(name, size, blocks, airtight cells): blocks as (min, max, cls, tier, functional)."""
    cases = []
    # a solid 3x3x3 light small cube: 26 outer blocks count, the centre is buried
    blocks = [((x, y, z), (x, y, z), LIGHT, 0, 1) for x in range(3) for y in range(3) for z in range(3)]
    cases.append(("solid3_light_small", SMALL, blocks, set()))
    # a hollow 4x4x4 large heavy shell with a dry 2x2x2 room inside, one light armour inner wall (counts only when wet)
    shell = [((x, y, z), (x, y, z), HEAVY, 0, 1) for x in range(4) for y in range(4) for z in range(4)
             if x in (0, 3) or y in (0, 3) or z in (0, 3)]
    room = {(x, y, z) for x in (1, 2) for y in (1, 2) for z in (1, 2)}
    inner = [((1, 1, 1), (1, 1, 1), LIGHT, 0, 1)]
    room_minus = room - {(1, 1, 1)}
    cases.append(("heavy_shell_dry_room", LARGE, shell + inner, room_minus))
    # the same with the room flooded (no airtight room): the inner light wall touches water -> light
    cases.append(("heavy_shell_flooded", LARGE, shell + inner, set()))
    # a window-like fitting in the hull between the dry room and the sea: counts (boundary)
    shell_w = [b for b in shell if b[0] != (0, 1, 1)] + [((0, 1, 1), (0, 1, 1), FITTING, 0, 1)]
    cases.append(("fitting_on_boundary", LARGE, shell_w, room))
    # a thruster-like fitting outside on the hull: wet on its sides, never dry -> does not count
    cases.append(("fitting_outside", LARGE, shell + [((4, 1, 1), (4, 1, 1), FITTING, 0, 1)], room))
    # a multi-cell fitting (1x2x1) inside the dry room: neither wet -> does not count; a functional Mk1 and a broken Mk2
    cases.append(("interior_fitting_and_tiers", LARGE, shell + [((1, 1, 2), (1, 2, 2), FITTING, 0, 1), ((2, 1, 1), (2, 1, 1), FITTING, 1, 1),
                                                                 ((2, 2, 1), (2, 2, 1), FITTING, 2, 0)], room - {(1, 1, 2), (1, 2, 2), (2, 1, 1), (2, 2, 1)}))
    # a lone small cockpit-like fitting (no armour, no room): nothing counts -> light of its size, the wet blocks are the damage set
    cases.append(("lone_fitting_small", SMALL, [((0, 0, 0), (0, 0, 0), FITTING, 0, 1)], set()))
    #  a bare craft (a 1x1x2 cockpit and a 1x1x1 battery, no armour, no room): wet damage set of 2
    cases.append(("bare_cockpit_battery", SMALL, [((0, 0, 0), (0, 0, 1), FITTING, 0, 1), ((1, 0, 0), (1, 0, 0), FITTING, 0, 1)], set()))
    #  the same window in a light shell is light (a fitting takes the class of the armour it touches)
    lshell = [((x, y, z), (x, y, z), LIGHT, 0, 1) for x in range(4) for y in range(4) for z in range(4)
              if (x in (0, 3) or y in (0, 3) or z in (0, 3)) and (x, y, z) != (0, 1, 1)] + [((0, 1, 1), (0, 1, 1), FITTING, 0, 1)]
    cases.append(("fitting_in_light_shell", LARGE, lshell, room))
    #  a door between one heavy and one light wall block and the room (a tie: light); here the heavy shell's
 # other neighbours of the door are taken out so the tie is the door's only armour contact
    door = [b for b in shell if b[0] not in ((0, 1, 1), (0, 1, 2), (0, 2, 1))] + [((0, 1, 1), (0, 1, 1), FITTING, 0, 1),
                                                                                ((0, 1, 2), (0, 1, 2), LIGHT, 0, 1)]
    cases.append(("door_tie_light", LARGE, door, room))
    # a mixed small hull: heavy cube with one light block outside
    mixed = [((x, y, z), (x, y, z), HEAVY, 0, 1) for x in range(2) for y in range(2) for z in range(2)] + [((2, 0, 0), (2, 0, 0), LIGHT, 0, 1)]
    cases.append(("mixed_small", SMALL, mixed, set()))
    return cases

def enc_blocks(blocks):
    return ";".join("%d,%d,%d:%d,%d,%d:%d:%d:%d" % (mn + mx + (c, t, f)) for mn, mx, c, t, f in blocks)

def dec_blocks(s):
    out = []
    for part in s.split(";"):
        if not part:
            continue
        a, b, c, t, f = part.split(":")
        out.append((tuple(int(v) for v in a.split(",")), tuple(int(v) for v in b.split(",")), int(c), int(t), int(f)))
    return out

def creak_sequences():
    """(name, limit, [(tick, depth)]): a descent through the band and past the limit, a bounce, a slow approach."""
    seqs = []
    L = 200.0
    seqs.append(("descent", L, [(t, 120.0 + t * (5.0 / 60.0)) for t in range(0, 60 * 30, 1)]))        # 5 m/s from 120 m
    pts = [(t, 160.0 + 0.5 * t / 60.0) for t in range(0, 600)] + [(600 + t, 165.0 - t / 60.0) for t in range(0, 900)] + \
          [(1500 + t, 150.0 + t / 30.0) for t in range(0, 1800)]
    seqs.append(("bounce", L, pts))
    seqs.append(("approach", 900.0, [(t, 860.0 + t / 120.0) for t in range(0, 60 * 50)]))
    return seqs

def voice_sequences():
    """(name, limit, [(ms, key, depth)]); the C# test (S16RulesTests.Voice_Vectors) builds the same points."""
    L = 300.0
    a = [(i * 100, 7, 280.0 + i * 0.5) for i in range(100)]            # down through the limit once (event at 4100 ms)
    a += [(10000 + i * 500, 7, 330.0) for i in range(120)]              # stays past 60 s: a repeat every 20 s
    a += [(70000 + i * 100, 7, 330.0 - i * 0.5) for i in range(100)]    # back up above limit - 10 (re-armed)
    a += [(80000 + i * 100, 7, 280.0 + i * 0.5) for i in range(100)]    # down again within the cooldown: event, no play
    a += [(90000 + i * 1000, 7, 330.0) for i in range(30)]              # stays: the repeat after the cooldown plays
    a += [(300000 + i * 100, 9, 280.0 + i * 0.5) for i in range(100)]   # another grid: its own key
    return [("cross", L, a)]

def write_vectors():
    os.makedirs(VEC_DIR, exist_ok=True)

    def w(name, header, rows):
        with open(os.path.join(VEC_DIR, name), "w", encoding="utf-8", newline="\n") as f:
            f.write("# " + header + "\n")
            for row in rows:
                f.write("\t".join(str(x) for x in row) + "\n")

    cls_rows = []
    for t, s, e in (("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", "Light"), ("CubeBlock", "SmallBlockArmorSlope", "Light"),
                    ("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", "Heavy"), ("MyObjectBuilder_CubeBlock", "LargeBlockConduitCorner", "Light"),
                    ("MyObjectBuilder_Thrust", "SmallBlockSmallThrust", "Light"), ("MyObjectBuilder_CubeBlock", "LargeArmorPanelLight", None),
                    ("MyObjectBuilder_Door", "LargeBlockGate", "Heavy"), ("MyObjectBuilder_CubeBlock", "DE_PressureHullMk2Large", "Light"),
                    ("MyObjectBuilder_CubeBlock", "smallblockarmorcorner", None), ("MyObjectBuilder_Cockpit", "SmallBlockCockpit", None)):
        cls_rows.append((t, s, e if e is not None else "-", class_of(t, s, e), tier_of(s)))
    w("classify.tsv", "typeId\tsubtypeId\tedgeType('-' none)\tclass\ttier", cls_rows)

    exp_rows = []
    for name, size, blocks, air in exposure_cases():
        s = summarise(size, blocks, air)
        hull, weak = hull_of(s)
        exp_rows.append((name, size, enc_blocks(blocks), ";".join("%d,%d,%d" % c for c in sorted(air)) or "-", s["light"], s["heavy"],
                         s["fitting"], s["tier"], s["blocks"], hull, weak, crush_depth_of(s), s["wet"], 1 if damage_set_is_wet(s) else 0))
    w("exposure.tsv", "name\tsize\tblocks(min:max:class:tier:functional;...)\tairtight cells('-' none)\tlight\theavy\tfitting(of light+heavy)\ttier\tblocks"
      "\thull\tweakest\tcrush\twet\tdamageSetIsWet", exp_rows)

    grp = []
    for own in (120, 400, 800, 1300):
        for gt in (0, 1, 2, 3):
            for docked in (0, 1):
                for carrier in (0, 500, 1700):
                    for inside in (0, 1):
                        grp.append((own, gt, docked, carrier, inside, effective_crush(own, gt, bool(docked), carrier), 1 if damage_applies(bool(docked), bool(inside)) else 0))
    w("group.tsv", "ownCrush\tgroupTier\tdocked\tcarrierCrush\tinsideCarrierRoom\teffectiveCrush\tdamageApplies", grp)

    hs = []
    for have in (0, 1):
        for flags in (0, FLAG_GRID, FLAG_DIVER, FLAG_GRID | FLAG_DAMAGE):
            for st in (OFF, SAFE, DAMAGED):
                for view in (VIEW_SPACE, VIEW_APPROACH, VIEW_UNDERWATER, VIEW_SURFACING):
                    hs.append(("mode", have, flags, st, view, sn_hud_mode(bool(have), flags, st, view)))
    for fresh in (0, 1):
        for c7 in (0, DIVER_SHOWN, GRID_SHOWN, COMPASS_OK | SEAMOTH_OK | GRID_SHOWN):
            for grid in (0, 1):
                hs.append(("suppress", fresh, c7, grid, 0, 1 if se_line_suppressed(bool(fresh), c7, bool(grid)) else 0))
                hs.append(("seline", fresh, c7, grid, 0, se_line(bool(fresh), c7, bool(grid))))
    for a, b in ((0.0, 0.0), (50.0, 100.0), (99.5, 100.0), (100.0, 100.0), (0.4, 100.0), (120.0, 100.0), (2475.0, 2500.0)):
        hs.append(("pct", r(a), r(b), hull_pct(a, b), power_pct(a, b), 0))
    for sw, sh, ui in ((2560, 1440, 1.0), (1920, 1080, 1.0), (3440, 1440, 1.0), (1280, 1024, 1.0), (2560, 1440, 0.75), (0, 0, 1.0)):
        hs.append(("scale", sw, sh, r(ui), 0, r(canvas_scale(sw, sh, ui))))
    for anchor, bounds in ((PLACE_TOP_CENTRE, (-150.0, -620.0, 150.0, -500.0)), (PLACE_TOP_CENTRE, (-60.0, 380.0, 60.0, 500.0)),
                           (PLACE_TOP_CENTRE, (300.0, -900.0, 700.0, -700.0)), (PLACE_TOP_CENTRE, (-1100.0, -50.0, 1100.0, 50.0))):
        dx, dy = hud_place(anchor, bounds[0], bounds[1], bounds[2], bounds[3], 1920.0, 1080.0)
        hs.append(("place", anchor, ",".join(r(x) for x in bounds), "1920.0,1080.0", 0, "%s,%s" % (r(dx), r(dy))))
    for vol, ms in ((1.0, -1.0), (1.0, 0.0), (0.85, 1000.0), (1.0, 3499.0), (1.0, 3500.0), (0.7, 99999.0)):
        hs.append(("mix", r(vol), r(ms), 0, 0, r(effect_volume(vol, ms))))
    for have in (0, 1):
        for destructible in (0, 1):
            for switch in (0, 1):
                hs.append(("gate", have, destructible, switch, 0, damage_gate(bool(have), bool(destructible), bool(switch))))
    for name in sorted(READOUTS):
        rects = READOUTS[name]
        b = readout_bounds(rects, 1920.0, 1080.0)
        hs.append(("bounds", name, ";".join(",".join(r(x) for x in q) for q in rects), "1920.0,1080.0", 0,
                   ",".join(r(x) for x in b[:4]) + ",%d,%d" % (b[4], b[5])))
    for name in sorted(READOUTS):
        for sw, sh in RESOLUTIONS:
            sc = canvas_scale(sw, sh, 1.0)
            q = place_readout(PLACE_TOP_CENTRE, READOUTS[name], sw / sc, sh / sc)
            hs.append(("px", name, "%d,%d" % (sw, sh), r(sc), 0, ",".join(r(x) for x in to_screen_px(q[0], q[1], q[2], q[3], sw / sc, sh / sc, sc))))
    for st in (OFF, SAFE, CREAKING, LIMIT, DAMAGED):
        hs.append(("tone", st, 0, 0, 0, tone(st)))
    for depth, crush, meter in ((10, 120, "m"), (250, 400, "m"), (1712, 1700, " m"), (0, 120, ""), (51, 120, "m"), (999, 1300, "m"), (1603, 1700, "m"), (12, 0, "m")):
        hs.append(("seated", depth, crush, meter.strip() or "-", 0, seated_text(depth, crush, meter)))
    for lines, tw, cw in ((1, 140.0, 156.0), (1, 140.9, 156.0), (1, 155.9, 156.0), (1, 176.2, 156.0), (2, 120.0, 156.0), (0, 0.0, 156.0)):
        hs.append(("fit", lines, r(tw), r(cw), 0, 1 if seated_text_fits(lines, tw, cw) else 0))
    for dx, cw in ((0.0, 156.0), (7.8, 156.0), (-7.9, 156.0), (-20.0, 156.0), (3.0, 0.0)):
        hs.append(("centre", r(dx), r(cw), 0, 0, 1 if seated_centred(dx, cw) else 0))
    for own, pw, tw in ((56.0, 120.0, 140.4), (56.0, 360.0, 140.4), (56.0, 1000.0, 140.4), (40.0, 0.0, 140.4)):
        hs.append(("font", r(own), r(pw), r(tw), 0, r(seated_font_size(own, pw, tw))))
    w("hud.tsv", "kind\ta\tb\tc\td\tresult (mode: have flags hudState viewState -> SnHudMode; suppress: fresh c7flags gridMode -> 0/1; pct: integrity max -> hullPct powerPct;"
      " scale: W H uiScale -> canvas scale; place: anchor minX,minY,maxX,maxY W,H -> dx,dy; mix: volume msSinceVoice -> effect volume;"
      " gate: haveSession destructibleBlocks switchOn -> DamageGate; bounds: name rects(;) W,H -> minX,minY,maxX,maxY,used,backdrops;"
      " seline: fresh c7flags gridMode -> SeLine; px: readout W,H scale -> x0,y0,x1,y1 screen px; tone: hudState -> Tone; seated: depth crush meter -> the seated text; fit: lineCount textW compassW -> 0/1; font: own preferredWidth target -> size; centre: dx compassW -> 0/1)", hs)

    crush_rows = []
    for size in (SMALL, LARGE):
        for light in (0, 3):
            for heavy in (0, 5):
                for fitting in (0, 1):
                    for tier in (0, 1, 2):
                        s = {"size": size, "light": light, "heavy": heavy, "fitting": fitting, "tier": tier}
                        hull, weak = hull_of(s)
                        crush_rows.append((size, light, heavy, fitting, tier, hull, weak, crush_depth_of(s), hull_code(size, weak)))
    w("crush.tsv", "size\tlight\theavy\tfitting\ttier\thull\tweakest\tcrush\thullCode", crush_rows)

    dmg = []
    for limit in (120.0, 200.0, 400.0, 800.0, 1700.0):
        for dd in (-60.0, -0.5, 0.0, 1e-9, 0.5, 1.0, 10.0, 45.5, 89.9, 90.0, 90.1, 200.0, 1234.5):
            d = limit + dd
            for size in (SMALL, LARGE):
                dmg.append((r(d), r(limit), size, r(damage_per_second(d, limit)), r(per_period_se(d, limit, size)), creak_level(d, limit),
                            hud_state(d, limit, True), hud_state(d, limit, False)))
    for d in (0.0, 0.49, 0.5, 99.9, 100.0, 100.01, 199.0, 200.0, 200.5, 3000.0):
        dmg.append((r(d), "inf", 1, "0.0", "0.0", 0, hud_state(d, float("inf"), True), breath_x10(d)))
    w("damage.tsv", "depth\tlimit\tsize\thp/s\tSE per period\tcreakLevel\thudState(damageOn)\thudState(off) | limit inf: breathX10 in the last column", dmg)

    pk = []
    for depths, limit in (([210.0, 205.0, 250.0, 199.0, 250.0, 201.0, 300.0], 200.0), ([100.0, 150.0], 200.0), ([1.0] * 3, 0.5),
                          ([float(121 + i) for i in range(12)], 120.0)):
        pk.append((",".join(r(x) for x in depths), r(limit), ",".join(str(i) for i in pick(depths, limit)) or "-"))
    w("pick.tsv", "depths\tlimit\tpicked indices", pk)

    cr = []
    for name, limit, pts in creak_sequences():
        clock = CreakClock()
        for tick, depth in pts:
            lv = creak_level(depth, limit)
            why = clock.step(tick, lv)
            if why:
                cr.append((name, tick, r(depth), r(limit), lv, REASONS[why]))
    w("creak.tsv", "sequence\ttick\tdepth\tlimit\tlevel\treason (only ticks with a creak; the C# replays the sequence)", cr)

    vo = []
    for name, limit, pts in voice_sequences():
        arms, rule = {}, VoiceRule()
        for ms, key, depth in pts:
            arm = arms.setdefault(key, VoiceArm())
            if arm.step(depth, limit, ms):
                vo.append((name, ms, key, r(depth), r(limit), 1 if rule.try_fire(key, ms) else 0))
    w("voice.tsv", "sequence\tms\tkey\tdepth\tlimit\tplayed (only event rows)", vo)

    wi = []
    for args in ((1 | 4, 2, 4, 2, 10, 2, 512.25, 900.0, 3, 1, 0, 7, 0x0144455400010211, 87, 255),
                 (2, 0, 0, 0, 15, 1, 150.0, 0.0, 0, 0, 0, 1, 0, 0, 0),
                 (1 | 8 | 16 | 32, 3, 1, 3, 10, 3, 1750.5, 1700.0, 4294967295, 2, 99, 12, -5, 100, 42)):
        wi.append(tuple(args) + (wire_pack(*args).hex(),))
    w("wire.tsv", "flags\tlevel\thull\ttier\tbreathX10\tstate\tdepth\tcrush\tcreakSeq\tvoiceSeq\tdamageSeq\tepoch\tgridId\thullPct\tpowerPct\tbytes(hex, SeState 0x80..)", wi)
    w("status.tsv", "c7flags\tbytes(hex, SnStatus 0xbc..)", [(f, status_pack(f).hex()) for f in (0, 1, 3, 15, 255)])
    return sorted(os.listdir(VEC_DIR))

def contract_block():
    if not os.path.exists(CONTRACT):
        return None
    doc = open(CONTRACT, encoding="utf-8").read().replace("\r\n", "\n")
    i = doc.find("```\n" + HEADER + "\n")
    if i < 0:
        return None
    i += 4
    j = doc.find("```", i)
    return None if j < 0 else doc[i:j]

def ladder_text():
    out = ["hull ladder (crush depth, m):"]
    for s, c, m in HULL:
        out.append("  %-5s %-10s %5d" % (SIZES[s], CLASSES[c], m))
    out.append("upgrade tiers (the best functional one per grid raises it to):")
    for t, sub, m, ing in TIERS:
        out.append("  Mk%d %-22s %5d m  alloy %s%d from %s ingots" % (t, sub + "Large/Small", m, ALLOY_PREFIX, t, ing))
    out.append("damage past the limit L (hp/s = min(%g, %g + %g * (d - L)); SE integrity/s = hp/s x k, k small 1, large 25; "
               "the %d deepest counted blocks per grid per second):" % (D["CapMilliHpS"] / 1000.0, D["BaseMilliHpS"] / 1000.0, D["PerMetreMilliHpS"] / 1000.0,
                                                                        D["MaxBlocksPerPeriod"]))
    for dd in (0.5, 10, 30, 60, 90, 150):
        out.append("  L + %5.1f m: %5.2f hp/s  small %7.2f / large %8.2f integrity per s" % (dd, damage_per_second(dd, 0.0),
                                                                                             per_period_se(dd, 0.0, SMALL), per_period_se(dd, 0.0, LARGE)))
    out.append("creaks: level 1 from L-50 (every 9 s), level 2 from L-25 (every 6 s), level 3 past L (every 3 s)")
    out.append("voice: 'Hull integrity critical' at the crossing below L and every %d s while below it, re-armed %d m above, at most once per %d s per grid;"
               " played at %g, S16's creaks and groans at %g for %g s after it" % (V["RepeatMs"] // 1000, V["RearmM"], V["CooldownMs"] // 1000, voice_volume(),
                                                                                  V["DuckPermille"] / 1000.0, V["DuckMs"] / 1000.0))
    out.append("diver: no crush depth; breathing x1 / x1.5 below 100 m / x2 below 200 m (S2b)")
    return "\n".join(out)

# =====================================================================================================================
# self-test
# =====================================================================================================================
def selftest():
    res = []

    def check(ok, what):
        res.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S16 twin self-test (tools/s16/s16_ref.py --selftest)")
    h, lines, nbytes = text_hash()
    check(PINNED_HASH == 0 or h == PINNED_HASH, "canonical text hash 0x%016x (pinned 0x%016x), %d lines, %d bytes" % (h, PINNED_HASH, lines, nbytes))
    check(CANON_LINES in (0, lines) and CANON_BYTES in (0, nbytes), "pinned line/byte counts")
    blk = contract_block()
    check(blk is None or blk == canonical_text(), "CONTRACT block equals the canonical text" + (" (block absent)" if blk is None else ""))
    check(all(ord(ch) < 128 for ch in canonical_text()), "ASCII only")
    # ladder
    check([hull_crush(s, c) for s, c, _m in HULL] == [350, 500, 800, 1200] and hull_crush(SMALL, FITTING) == 0, "the ladder 350/500/800/1200 (no fitting rung; depth 10)")
    check([tier_crush(t) for t in (0, 1, 2, 3)] == [0, 1400, 2000, 0], "tiers 1400/2000, no third (depth 11)")
    s = {"size": SMALL, "light": 0, "heavy": 4, "fitting": 0, "tier": 0}
    check(crush_depth_of(s) == 500, "heavy small 500")
    s["light"] = 1
    check(crush_depth_of(s) == 350 and hull_of(s)[1] == LIGHT, "one exposed light block makes it light (weakest)")
    s["tier"] = 2
    check(crush_depth_of(s) == 2000, "Mk2 raises a light small hull to 2000")
    check(crush_depth_of({"size": LARGE, "light": 0, "heavy": 9, "fitting": 0, "tier": 1}) == 1400, "Mk1 raises heavy large 1200 to 1400")
    check(crush_depth_of({"size": LARGE, "light": 0, "heavy": 0, "fitting": 0, "tier": 0}) == 800, "nothing counted -> light of the size")
    # classify
    check(class_of("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", "Light") == LIGHT, "light armour")
    check(class_of("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", "Heavy") == HEAVY, "heavy armour (EdgeType)")
    check(class_of("MyObjectBuilder_CubeBlock", "LargeBlockConduitCorner", "Light") == FITTING, "decor CubeBlock without Armor is a fitting")
    check(class_of("MyObjectBuilder_Thrust", "SmallBlockSmallThrust", "Light") == FITTING, "a thruster is a fitting")
    check(tier_of("DE_PressureHullMk2Small") == 2 and tier_of("DE_PressureHullMk3Large") == 0, "tier subtypes (no Mk3 since depth 11)")
    # exposure
    cases = {n: summarise(sz, b, a) for n, sz, b, a in exposure_cases()}
    check(cases["solid3_light_small"]["light"] == 26 and crush_depth_of(cases["solid3_light_small"]) == 350, "solid 3^3: 26 outer blocks count, 350 m")
    check(cases["heavy_shell_dry_room"]["light"] == 0 and crush_depth_of(cases["heavy_shell_dry_room"]) == 1200, "a dry inner light wall does not count: 1200 m")
    check(cases["heavy_shell_flooded"]["light"] == 1 and crush_depth_of(cases["heavy_shell_flooded"]) == 800, "flooded: the inner light wall counts: 800 m")
    check(cases["fitting_on_boundary"]["fitting"] == 1 and crush_depth_of(cases["fitting_on_boundary"]) == 1200, "review 3: a window in a heavy hull is rated heavy (1200)")
    check(cases["fitting_in_light_shell"]["fitting"] == 1 and crush_depth_of(cases["fitting_in_light_shell"]) == 800, "the same window in a light hull: 800")
    door = cases["door_tie_light"]
    check(door["fitting"] == 1 and crush_depth_of(door) == 800, "a door touching one heavy and one light block: the tie is light")
    check(rated_class(FITTING, 1, 2) == HEAVY and rated_class(FITTING, 2, 2) == LIGHT and rated_class(FITTING, 0, 0) == LIGHT and rated_class(HEAVY, 5, 0) == HEAVY,
          "rated class: majority heavy, tie light, none light, armour its own")
    bare = cases["bare_cockpit_battery"]
    check(bare["light"] + bare["heavy"] == 0 and bare["wet"] == 2 and damage_set_is_wet(bare) and crush_depth_of(bare) == 350,
          "review 1: a bare craft (cockpit + battery) is light small and its wet blocks are the damage set")
    check(not damage_set_is_wet(cases["solid3_light_small"]) and cases["solid3_light_small"]["wet"] == 26, "a hull's damage set is its counted blocks")
    check(cases["fitting_outside"]["fitting"] == 0 and crush_depth_of(cases["fitting_outside"]) == 1200, "a fitting outside (wet only) does not count")
    check(cases["interior_fitting_and_tiers"]["tier"] == 1 and crush_depth_of(cases["interior_fitting_and_tiers"]) == 1400, "functional Mk1 counts, broken Mk2 not")
    check(crush_depth_of(cases["lone_fitting_small"]) == 350, "a lone fitting grid is light small")
    check(cases["mixed_small"]["light"] == 1 and crush_depth_of(cases["mixed_small"]) == 350, "mixed: the light block decides")
    # damage (depth 3: twice the old curve at every depth, 8 blocks a period)
    check(damage_per_second(200.0, 200.0) == 0.0 and damage_per_second(200.0001, 200.0) > 10.0, "no damage at the limit, 10 hp/s just past")
    check(abs(damage_per_second(210.0, 200.0) - 20.0) < 1e-12 and damage_per_second(290.0, 200.0) == 100.0 and damage_per_second(400.0, 200.0) == 100.0,
          "+1 per m, cap 100 from 90 m past")
    check(per_period_se(290.0, 200.0, LARGE) == 2500.0 and per_period_se(290.0, 200.0, SMALL) == 100.0, "k large 25, small 1")
    check(pick([210.0, 205.0, 250.0, 199.0, 250.0, 201.0, 300.0], 200.0) == [6, 2, 4, 0, 1, 5], "up to the 8 deepest past the limit")
    check(len(pick([float(121 + i) for i in range(12)], 120.0)) == 8, "at most 8 a period")
    # group and dock
    check(effective_crush(200, 2, False, 0) == 2000 and effective_crush(900, 0, False, 0) == 900, "the mechanical group's best tier raises a subgrid")
    check(effective_crush(200, 0, True, 1700) == 1700 and effective_crush(1700, 0, True, 500) == 1700, "docked: never below the carrier, never lowered by it")
    check(not damage_applies(True, True) and damage_applies(True, False) and damage_applies(False, True), "docked inside the carrier's airtight room: no damage")
    # HUD readouts (HUD A, B)
    check(hull_pct(2475.0, 2500.0) == 99 and hull_pct(0.0, 0.0) == 100 and hull_pct(120.0, 100.0) == 100 and power_pct(1.0, 0.0) == 255, "hullPct / powerPct")
    check(sn_hud_mode(True, FLAG_DIVER, SAFE, VIEW_UNDERWATER) == SN_HUD_DIVER and sn_hud_mode(True, FLAG_GRID, DAMAGED, VIEW_SURFACING) == SN_HUD_GRID, "SN HUD modes")
    check(sn_hud_mode(True, FLAG_GRID, SAFE, VIEW_SPACE) == SN_HUD_NONE and sn_hud_mode(True, FLAG_DIVER, OFF, VIEW_UNDERWATER) == SN_HUD_NONE, "SN HUD only on its own picture, not when off")
    check(se_line_suppressed(True, GRID_SHOWN, True) and not se_line_suppressed(True, GRID_SHOWN, False) and not se_line_suppressed(False, GRID_SHOWN, True),
          "SE hides its line only for the mode Subnautica shows, on a fresh record")
    check(all(se_line(f, c7, False) == LINE_NONE for f in (False, True) for c7 in (0, DIVER_SHOWN, GRID_SHOWN)), "hud.se: never a diver line (owner: 'useless')")
    check(se_line(False, 0, True) == LINE_GRID and se_line(True, GRID_SHOWN, True) == LINE_SUPPRESSED and se_line(True, DIVER_SHOWN, True) == LINE_GRID,
          "hud.se: the grid line is the fallback while Subnautica's readouts are not shown")
    # creaks
    check([creak_level(d, 200.0) for d in (149.9, 150.0, 174.9, 175.0, 200.0, 200.1)] == [0, 1, 1, 2, 2, 3], "creak levels")
    c = CreakClock()
    seq = [c.step(t, lv) for t, lv in ((0, 0), (10, 1), (11, 1), (549, 1), (550, 1), (600, 2), (959, 2), (960, 2), (1000, 3), (1180, 3), (1181, 0), (1200, 1))]
    check(seq == [0, 1, 0, 0, 3, 2, 0, 3, 2, 3, 0, 1], "cadence: enter, period 540, rise, period 360, rise, period 180, leave, enter (%s)" % seq)
    # voice
    a, vr = VoiceArm(), VoiceRule()
    ev = [a.step(d, 300.0, ms) for d, ms in ((290, 0), (301, 1000), (302, 2000), (295, 3000), (289, 4000), (301, 5000))]
    check(ev == [False, True, False, False, False, True], "arm: one event per crossing, re-armed 10 m above")
    a = VoiceArm()
    ev = [a.step(d, 300.0, ms) for d, ms in ((310, 0), (320, 19999), (330, 20000), (330, 39999), (330, 40000), (299, 50000), (330, 60000), (330, 79999))]
    check(ev == [True, False, True, False, True, False, True, False], "repeat every 20 s while past the limit; a dip above it without re-arming keeps the repeat clock")
    check(vr.try_fire(1, 0) and not vr.try_fire(1, 19999) and vr.try_fire(1, 20000) and vr.try_fire(2, 1), "cooldown 20 s per grid")
    check(abs(voice_volume() - 1.6) < 1e-12 and effect_volume(1.0, 0.0) == 0.25 and effect_volume(1.0, 3500.0) == 1.0 and effect_volume(0.7, -1.0) == 0.7,
          "voice.mix: the voice at 1.6, S16's own sounds at 0.25 for 3.5 s after it")

    check(abs(canvas_scale(2560, 1440, 1.0) - 4.0 / 3.0) < 1e-12 and canvas_scale(1920, 1080, 1.0) == 1.0 and abs(canvas_scale(3440, 1440, 1.0) - 4.0 / 3.0) < 1e-12,
          "canvas scale = Subnautica's: 2560x1440 -> 1.333, ultrawide by the height")
    dx, dy = hud_place(PLACE_TOP_CENTRE, -150.0, -620.0, 150.0, -500.0, 1920.0, 1080.0)
    check(abs(dx) < 1e-9 and abs((-500.0 + dy) - (540.0 - 64.8)) < 1e-9, "a compass at the bottom moves to 6 % below the top edge")
    dx, dy = hud_place(PLACE_TOP_CENTRE, -1100.0, -50.0, 1100.0, 50.0, 1920.0, 1080.0)
    check(abs(dx) < 1e-9, "wider than the canvas: centred, not clamped")
    # depth 6 shows the compass's player readout seated too)
    for name in ("diver", "seated"):
        for sw, sh in RESOLUTIONS:
            sc = canvas_scale(sw, sh, 1.0)
            q = place_readout(PLACE_TOP_CENTRE, READOUTS[name], sw / sc, sh / sc)
            x0, y0, x1, y1 = to_screen_px(q[0], q[1], q[2], q[3], sw / sc, sh / sc, sc)
            check(0 <= x0 and x1 <= sw and 0 <= y0 and y1 <= sh and abs(y0 - sh * HU["TopPermille"] / 1000.0) < 1e-6 and abs((x0 + x1) / 2.0 - sw / 2.0) < 1e-6,
                  "%s readout at %dx%d: (%.0f, %.0f)-(%.0f, %.0f) px, top-centre 6 %% below the top edge, never clipped" % (name, sw, sh, x0, y0, x1, y1))
    # the state colours stay)
    check(seated_text(10, 120) == "10 m / 120 m" and seated_text(250, 400, " m") == "250 m / 400 m", "seated: one field '10 m / 120 m' (the Cyclops helm's form)")
    check(seated_text(1603, 1700) == "1603 / 1700 m" and seated_text(999, 1300) == "999 / 1300 m", "depth 8: the short form above 6 digits ('1603 / 1700 m')")
    check(seated_text(12, 0) == "12 m" and seated_text(1603, -1) == "1603 m", "no crush depth (a station): the depth only")
    check(seated_centred(0.0, 156.0) and seated_centred(7.8, 156.0) and not seated_centred(-7.9, 156.0) and not seated_centred(0.0, 0.0),
          "depth 9: centred on the compass within 5 % of its width (the arc's left offset is red)")
    check(seated_text_fits(1, 140.0, 156.0) and not seated_text_fits(2, 100.0, 156.0) and not seated_text_fits(1, 155.9, 156.0) and not seated_text_fits(1, 176.2, 156.0),
          "the live check: one line, no wider than 90 % of the compass (the 00:22 rows: 155.9 and 176.2 of 156 are red)")
    # the 00:22 session's measures: every readout fits with the margin at the measured font, above the floor
    worst = []
    for depth, crush in ((51, 120), (999, 999), (1603, 1700), (1760, 1700), (9999, 1700), (1699, 1700)):
        text = seated_text(depth, crush)
        target = seated_target(COMPASS_W)
        size = seated_font_size(OWN_FONT, len(text) * CHAR_W * OWN_FONT, target)
        width = len(text) * CHAR_W * size
        worst.append((text, round(size, 1), round(width, 1)))
        check(seated_text_fits(1, width, COMPASS_W) and size >= OWN_FONT * HU["MinFontPermille"] / 1000.0,
              "%r at font %.1f: %.1f of %.1f units (target %.1f)" % (text, size, width, COMPASS_W, target))
    check([tone(st) for st in (OFF, SAFE, CREAKING, LIMIT, DAMAGED)] == [TONE_NORMAL, TONE_NORMAL, TONE_CAUTION, TONE_DANGER, TONE_DANGER], "tones: safe, creaking, past the limit")
    whole = BACKDROP
    dxb, dyb = hud_place(PLACE_TOP_CENTRE, whole[0], -1560.0, whole[2], whole[3], 1920.0, 1080.0)
    check(not (-540.0 <= -1460.0 + dyb <= 540.0), "the depth 3 rule (all graphics, the backdrop included) left a low readout off screen")
    check(readout_bounds([BACKDROP], 1920.0, 1080.0) is None, "a backdrop alone is no readout")

    check(damage_gate(True, True, True) == GATE_DESTRUCTIBLE and damage_gate(True, False, True) == GATE_INDESTRUCTIBLE and damage_gate(True, True, False) == GATE_OFF
          and damage_gate(False, True, True) == GATE_NONE, "damage.gate: destructible blocks, whatever the mode; de.s16 damage off overrides")
    # HUD
    check([hud_state(d, 200.0, True) for d in (0.4, 10, 149.9, 150, 200, 200.5)] == [OFF, SAFE, SAFE, CREAKING, CREAKING, DAMAGED], "HUD states")
    check(hud_state(250, 200.0, False) == LIMIT, "Limit without damage (Creative)")
    check([breath_x10(d) for d in (50, 100, 100.1, 200, 200.1)] == [10, 10, 15, 15, 20], "diver x1 / x1.5 / x2")
    # wire
    b = wire_pack(5, 2, 4, 2, 10, 2, 512.25, 900.0, 3, 1, 0, 7, 0x0144455400010211, 87, 255)
    u = wire_unpack(b)
    check(len(b) == 0x2A and u["depthM"] == 512.25 and u["gridId"] == 0x0144455400010211 and u["hudState"] == 2 and u["hullPct"] == 87 and u["powerPct"] == 255,
          "C5 pack/unpack (42 bytes)")
    check(wire_unpack(b"\0" * 0x2A) is None, "version 0 = absent")
    check(C5FIELDS[-1][1] + C5FIELDS[-1][2] == 0x80 + 0x2A, "the field table fills 0x80..0xa9")
    check(C7FIELDS[0][1] == 0x8C + 0x30 and C7FIELDS[-1][1] + C7FIELDS[-1][2] == 0xC0 and status_pack(GRID_SHOWN) == b"\x01\x08\x00\x00",
          "the C7 block 0xbc..0xbf after S8a's 0x8c..0xbb")
    sr = SeqReader()
    check([sr.step(1, v) for v in (5, 5, 6, 9, 9)] == [0, 0, 1, 1, 0] and sr.coalesced == 2, "seq reader: baseline, one per increase, coalesced")
    check(sr.step(2, 9) == 0 and sr.step(2, 10) == 1, "a new epoch rebaselines")
    # vectors current
    import tempfile
    import shutil
    global VEC_DIR
    keep = VEC_DIR
    tmp = tempfile.mkdtemp(prefix="de-s16-vec-")
    try:
        VEC_DIR = tmp
        names = write_vectors()
        same = True
        for n in names:
            p = os.path.join(keep, n)
            if not os.path.exists(p) or open(p, encoding="utf-8").read() != open(os.path.join(tmp, n), encoding="utf-8").read():
                same = False
                print("    vector file differs or missing: %s" % n)
        check(same, "tests/s16/vectors current (%d files)" % len(names))
    finally:
        VEC_DIR = keep
        shutil.rmtree(tmp, ignore_errors=True)
    ok = all(res)
    print("S16 TWIN SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(res), len(res)))
    return 0 if ok else 1

def main(argv):
    if "--text" in argv:
        sys.stdout.write(canonical_text())
        return 0
    if "--pin" in argv:
        h, n, b = text_hash()
        print("0x%016x %d lines %d bytes" % (h, n, b))
        return 0
    if "--vectors" in argv:
        for n in write_vectors():
            print("wrote", os.path.join(VEC_DIR, n))
        return 0
    if "--ladder" in argv:
        print(ladder_text())
        return 0
    return selftest()

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
