"""S11 conventions, the Python twin of the Wire creature and hazard rules (src/DeepEngineers.Wire/S11Conventions.cs,
S11Rules.cs): the canonical text and its hash (`ext s11 creatures 1`; CONTRACT §5.4 "S11 conventions"), the weapon-class
rule against a creature's own maxHealth (A), the explosion yield code, the per-tick explosion groups and the picture's
chain filter (A, stacked warheads), the grab size class, hold and push rules (C), the hazard table with its per-second
cap (D), the heal and oxygen rules (E) and the C8 payload checks of the S11 units. Shares no code with the C#; the
vectors it writes under tests/s11/vectors/ are read by the C# tests (S11RulesTests in tests/DeepEngineers.Wire.Tests).

Usage (repository root; stdlib only):
 python -I tools/s11/s11_ref.py --selftest every rule's hand checks, the pin, the CONTRACT block, vectors current
 python -I tools/s11/s11_ref.py --vectors (re)writes tests/s11/vectors/{danger,blast,chain,grab,hazard,records}.tsv
 python -I tools/s11/s11_ref.py --text prints the canonical text
 python -I tools/s11/s11_ref.py --pin prints hash, lines, bytes
"""
import math
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
VEC_DIR = os.path.join(ROOT, "tests", "s11", "vectors")
CONTRACT = os.path.join(ROOT, "docs", "CONTRACT.md")

# =====================================================================================================================
# the tables (CONTRACT §5.4 "S11 conventions"; the same numbers as S11Conventions.cs)
# =====================================================================================================================
HEADER = "ext s11 creatures 5"
PINNED_HASH = 0xa10efc0920e7f8e8
CANON_LINES, CANON_BYTES = 133, 12020

UNIT = [("HazardHp", 17), ("HealHp", 18), ("OxygenS", 19), ("EmpS", 20), ("Grab", 21), ("Push", 22), ("MissileHit", 23)]
U = dict(UNIT)
FLAGS = [("ViaSampler", 256), ("ViaSection", 512)]
CLASSES = ["", "Handheld", "SmallGrid", "LargeGrid", "Explosion"]
TIERS = ["", "Small", "Large", "Leviathan"]
TIER_LARGE_MIN, TIER_LEVIATHAN_MIN = 200, 2000
WEAPON = [  # (class, tier, mult per mille, floor per mille)
    (1, 1, 1000, 0), (1, 2, 1000, 0), (1, 3, 1000, 500),
    (2, 1, 1000, 0), (2, 2, 1000, 0), (2, 3, 1000, 250),
    (3, 1, 1000, 0), (3, 2, 1000, 0), (3, 3, 1000, 0),
    (4, 1, 1000, 0), (4, 2, 1000, 0), (4, 3, 1000, 0),
]
BLAST = [("YieldUnitSe", 10), ("JoinPadM", 2), ("ChainWindowMs", 600), ("VisualRadiusCapM", 200), ("DamageRadiusCapM", 100),
         ("MaxGroupsPerTick", 16), ("ClassShift", 8)]
B = dict(BLAST)
GRAB = [  # creature, TechType, attach field, dps field, maxVolumeM3, maxEdgeDm, holdMinMs, holdRandMs, cooldownMs, dpsPermille, intervalMs, blendMs, release
    ("ReaperLeviathan", 2540, "seamothAttachPoint", "seamothDamagePerSecond", 400, 150, 8000, 5000, 10000, 1000, 1000, 1000, "Electrical,Poison"),
    ("SeaDragon", 2553, "exosuitAttachPoint", "exosuitDamagePerSecond", 2900, 310, 3000, 0, 10000, 1000, 1000, 250, "none"),
]
#: creature, TechType, releasePermille, angerPermille, extendMs, fleeMs (format rage.hold)
RAGE = [("ReaperLeviathan", 2540, 300, 1500, 4000, 10000), ("SeaDragon", 2553, 350, 1500, 2000, 10000)]
PUSH = [("ReaperLeviathan", 2540, 1000, 600), ("GhostLeviathan", 2562, 1000, 1000), ("GhostLeviathanJuvenile", 2565, 1000, 480), ("SeaDragon", 2553, 1000, 1000)]
SCALE = [("ReaperLeviathan", 2540, 2500, 11000), ("GhostLeviathan", 2562, 2500, 14000), ("GhostLeviathanJuvenile", 2565, 1800, 7000), ("SeaDragon", 2553, 2500, 5800)]
HOLD = [("OmegaMilli", 6000), ("ZetaPermille", 1000), ("MaxAccelCmS2", 15000), ("AngOmegaMilli", 4000), ("MaxAngAccelMilli", 12000),
        ("TimeoutMs", 1000), ("RecordIntervalMs", 20), ("SnapCm", 100), ("ServoKpMilli", 10000), ("ServoKRotMilli", 8000),
        ("MaxAngVelMilli", 6000), ("LeadMaxMs", 100), ("MaxMouthSpeedCmS", 15000), ("ReleaseSpeedCmS", 1000)]
H = dict(HOLD)
GRAB_PHASE = [("Start", 1), ("Hold", 2), ("Release", 3)]
HAZARD = [  # name, SN type, SE type, k per mille, cap per s, effect
    ("Heat", 1, "DeepHeat", 1000, 50, "heat"),
    ("Poison", 5, "DeepPoison", 1000, 30, "poison"),
    ("Acid", 6, "DeepAcid", 1000, 30, "acid"),
    ("Cold", 10, "DeepCold", 1000, 30, "cold"),
    ("Fire", 14, "DeepFire", 1000, 50, "fire"),
    ("Smoke", 16, "DeepSmoke", 1000, 10, "smoke"),
]
HEAL = [("KPermille", 1000), ("CapPerS", 100), ("MaxRecordHp", 500)]
OXYGEN = [("TankS", 45), ("BaseRefillPerS", 30), ("MaxRecordS", 120)]
OXSRC = [("Bubble", 1), ("Area", 2), ("Base", 3), ("Other", 4)]
EMP = [("SecondsPermille", 1000), ("MinS", 1), ("MaxS", 30)]
LAVA = [("ProbeIntervalMs", 100), ("HeatMilliHpPerProbe", 2500), ("ReachCm", 40)]
LIMITS = [("MaxHazardHp", 500), ("MaxPushNs", 10000000), ("MaxEmpS", 60)]
C7 = [("Offset", 0xC0), ("Size", 4), ("Version", 1)]
C7FLAGS = [("DamageFxOk", 1), ("DamageFxShown", 2)]
HUD = [("HitFadeMs", 800), ("FullHitHp", 10), ("MinAlphaPermille", 200), ("MaxAlphaPermille", 800)]
HU = dict(HUD)
DEBRIS = [("MinBlocks", 8), ("MinVolumeM3", 8)]
DB = dict(DEBRIS)
SURFACE = [("MarginCm", 200)]
SF = dict(SURFACE)
MISSILE = [("HitRadiusCm", 100), ("BackCastCm", 3000), ("SlugPermille", 20), ("SlugRadiusCm", 300), ("AttributionMs", 1500), ("BackCastMs", 100), ("HitBody", 0), ("HitHolder", 1)]
MS = dict(MISSILE)
FORMATS = [
    "blast.damage hp = yield * mult(Explosion, tier) * max(0, (R - d) / R), d = the distance to the nearest point of the creature's own colliders in the overlap (Collider.ClosestPoint; a non-convex mesh collider by its bounds; its LiveMixin transform without one; 0 for the creature a missile struck, missile.hit), R = min(cluster R, DamageRadiusCapM)",
    "blast.group join when |c - C| <= max(R, r) + JoinPadM; R = cbrt(sum r^3); C = yield-weighted centre; yield = sum",
    "c8.s11 unit HazardHp|HealHp: amount SN_HP, kind 2 character; OxygenS: amount SN seconds, snDamageType = OxygenSource; EmpS: amount seconds, kind 1 grid",
    "c8.s11grab unit Grab: snDamageType = GrabPhase, snWorldHit = attach point, attackDirSn = attach forward, localHit = attach up, dealerInstanceId = grab key, amount = planned hold s (start) else 1",
    "c8.s11push unit Push: amount = impulse N*s, attackDirSn = push direction (SN world), snWorldHit = bite point",
    "danger.applied min(base * mult, max(0, health - floor * maxHealth)); tier by LiveMixin.maxHealth read at run time",
    "event.class Explosion: subtype bits 8-11 = the WeaponClass of the origin (a character Handheld, a small grid SmallGrid, a warhead, a large grid or none Explosion); 0 = absent (Explosion); groups never mix classes",
    "event.yield Explosion: extra = clamp(round(yield / YieldUnitSe), 1, 65535); 0 = absent (yield = intensity * 1000)",
    "hold.force (S11.M12 only, the pre-kinematic rule) m*(clamp(w^2*(xt - x) + 2*z*w*(vt - v), aMax) - g) at the centre of mass, x and v = the bite anchor and its velocity, aMax = MaxAccelCmS2 / 100 * max(1, scale); -F_buoy*(1 - holdShare) at the centre of buoyancy",
    "hold.kinematic every SE tick: xt = the last attach point (the jaw centre: seamothAttachPoint / exosuitAttachPoint, where Subnautica holds a Seamoth or an Exosuit) + its velocity (between the last two records by Subnautica's emit times snTimeSec, else their arrival; over MaxMouthSpeedCmS none) * min(age, LeadMaxMs); the anchor = the grid's centre of bounds (PositionComp.LocalAABB centre, grid-local); the target xb = a + (xt - a) * f, a = the anchor at the start, f = min(1, held / blendMs of the grab row), vb = its rate; the orientation = attach * slerp(relative-at-start, aligned, f), aligned = the grid's longest box axis on the attach forward and its shortest on the attach up (signs nearest the start); the error = |xb - anchor|; over SnapCm the grid's matrix is set (anchor on xb); then the anchor's velocity = vb + (xb - anchor) * ServoKpMilli / 1000, the angular velocity = clamp(rotation vector of (target * current^-1) * ServoKRotMilli / 1000, MaxAngVelMilli / 1000), both set on the root grid (no force: SE's physics owns the rest); a grid with mechanical subgrids is never held (Subgrids); at any release the root grid's linear speed is capped at ReleaseSpeedCmS, its angular velocity zeroed and, at a surface release, its upward component removed; while it holds, Subnautica plays the creature's animation at 1 / its scale (the jaw swings at the unscaled creature's speed)",
    "oxygen.litres SE litres = seconds / TankS * suit OxygenCapacity, capped at the capacity",
    "push.dv min(impulse / grid mass, MaxDvCmS / 100) along the push direction at the bite point; impulse = mass * scale^3 * closing * factor",
    "scale.creature root localScale = factor * one (absolute; Creature.Start resets a non-growing creature to one first); BiteMult (its own value, not the factor) on Normal hits on SE grids, bites and hold damage",
    "debris.grid a grid is debris (never hunted, bitten, grabbed or pushed: no EcoTarget, no bite, no grab start, no push) when blocks < MinBlocks or its box volume < MinVolumeM3 m3; SE counts MyCubeGrid.BlocksCount, Subnautica estimates blocks = sum of its skin box volumes / block edge^3 (0.5 m small, 2.5 m large; unknown: the volume alone); the character is never debris",
    "c7.s11 SnStatus 0xc0-0xc3 written by Subnautica every frame: version, flags (DamageFxOk, DamageFxShown), 2 reserved; version 0 = absent (SE keeps its own overlay)",
    "hud.blood SE blood alpha: the game's own while dead; the per-hit pulse = clamp(drop / FullHitHp, Min, Max) * (1 - dt / HitFadeMs) after the last health drop, 0 from HitFadeMs on (0 under Subnautica's picture with a fresh c7.s11 DamageFxOk); the game's low-health floor (health ratio <= 0.2: (0.2 - ratio) / 0.2 + 0.3) kept: max(pulse, floor)",
    "c8.s11missile unit MissileHit: kind 1, targetEntityId = rootEntityId = the missile's SE entity id, snWorldHit = the hit point, attackDirSn = the flight direction, dealerTechType and dealerInstanceId = the struck creature, amount 1, snDamageType = HitBody 0 or HitHolder 1 (the missile left the grid that creature holds)",
    "missile.hit Subnautica sweeps every mirrored missile (GridAdd Missile) each frame from its last pose to this one (Physics.DefaultRaycastLayers, triggers ignored; its first pose also by an overlap of HitRadiusCm and a cast of BackCastCm along -forward, its second by a cast of speed * BackCastMs behind the first): the nearest collider of a live creature (LiveMixin + Creature) before any other solid (the ghost Player passes) is a hit, one MissileHit record per missile; a holder's jaw within the held grid's reach of the first flight is a HitHolder; SE refuses a hit more than 1 m behind the missile's launch point (MyMissile.Origin, Behind); a HitBody moves the missile to the hit point and explodes it (MyMissile.Explode: its own Explosion row); a HitHolder explodes it where it is with its damage set to 0 (the player's hull untouched) and raises one synthetic Explosion row of its damage at the hit point; a health-pool slug (HasHealthPool) raises one synthetic Explosion row of yield HealthPool * SlugPermille / 1000, radius SlugRadiusCm / 100, its launcher's class; the struck creature takes that blast at d = 0 (its hit within AttributionMs and JoinPadM of the centre)",
    "rage.hold during a hold the SN_HP the holder takes from SE weapons (danger.applied) accumulates; at ReleasePermille / 1000 * maxHealth it lets go (a release record) and flees for FleeMs (Scared + 1, Aggression held at 0, LastTarget cleared, swims away kept under the surface.release follow depth, no grab), once per hold; the first lower hit makes it angrier, once per hold: hold damage * AngerPermille / 1000, the planned hold + ExtendMs, Aggression + 1; retuned live (de.s11 rage), never the pin",
    "surface.release depths scale with the bodies: a creature's top is the higher of its root and its head (the attach point) plus its girth (half its thinnest collider-bounds extent); while it holds a grid it lets go when its top or the held grid's comes within MarginCm / 100 m of the surface (Subnautica: SN y = the altitude above the Water Mod sea sphere; SE: the Water Mod GetDepth at the top of the grid's bounds; SE then drops that grab key's later hold records); no grab starts there; a creature that holds, flees or hunts an SE grid section (LastTarget) is steered no higher than -(its girth + MarginCm / 100) m at SplineFollowing.GoTo (the last door of SwimTo, Attack and Overshoot), and at every physics step (Locomotion.ManagedFixedUpdate) loses its upward speed while its top is within MarginCm / 100 m of the surface; nothing of S11 ever drives it upward",
    "fx.sn under Subnautica's picture a hazard record or a bite on the character plays DamageFX.AddHudDamage(hp / 100, point, type) at most every 250 ms per type; its splatters move under the S16 overlay canvas; leaving the picture clears them",
]
SN_TYPES = ["Normal", "Heat", "Pressure", "Puncture", "Collide", "Poison", "Acid", "Explosive", "Electrical", "Drill", "Cold",
            "Starve", "Radiation", "LaserCutter", "Fire", "Undefined", "Smoke"]

def canonical_text():
    out = [HEADER]

    def c(group, name, v):
        out.append("const %s %s %d" % (group, name, v))

    for n, v in UNIT:
        c("S11Unit", n, v)
    for n, v in FLAGS:
        c("S11Flags", n, v)
    for i in range(1, 5):
        c("WeaponClass", CLASSES[i], i)
    for i in range(1, 4):
        c("CreatureTier", TIERS[i], i)
    c("CreatureTierMinHp", "Large", TIER_LARGE_MIN)
    c("CreatureTierMinHp", "Leviathan", TIER_LEVIATHAN_MIN)
    for cl, t, m, f in WEAPON:
        out.append("weapon %s %s multPermille %d floorPermille %d" % (CLASSES[cl], TIERS[t], m, f))
    for n, v in BLAST:
        c("Blast", n, v)
    for g in GRAB:
        out.append("grab %s %d attach %s dps %s maxVolumeM3 %d maxEdgeDm %d holdMinMs %d holdRandMs %d cooldownMs %d dpsPermille %d intervalMs %d blendMs %d release %s" % g)
    for p in PUSH:
        out.append("push %s %d factorPermille %d maxDvCmS %d" % p)
    for sc in SCALE:
        out.append("scale %s %d factorPermille %d biteMultPermille %d" % sc)
    for rg in RAGE:
        out.append("rage %s %d releasePermille %d angerPermille %d extendMs %d fleeMs %d" % rg)
    for n, v in HOLD:
        c("Hold", n, v)
    for n, v in GRAB_PHASE:
        c("GrabPhase", n, v)
    for h in HAZARD:
        out.append("hazard %s %d %s kPermille %d capPerS %d effect %s" % h)
    for n, v in HEAL:
        c("Heal", n, v)
    for n, v in OXYGEN:
        c("Oxygen", n, v)
    for n, v in OXSRC:
        c("OxygenSource", n, v)
    for n, v in EMP:
        c("Emp", n, v)
    for n, v in LAVA:
        c("Lava", n, v)
    for n, v in LIMITS:
        c("Limits", n, v)
    for n, v in C7:
        c("C7", n, v)
    for n, v in C7FLAGS:
        c("C7Flags", n, v)
    for n, v in HUD:
        c("Hud", n, v)
    for n, v in DEBRIS:
        c("Debris", n, v)
    for n, v in SURFACE:
        c("Surface", n, v)
    for n, v in MISSILE:
        c("Missile", n, v)
    for f in sorted(FORMATS):
        out.append("format " + f)
    return "".join(line + "\n" for line in out)

def blood_alpha(dt_ms, drop_hp):
    """format hud.blood: SE's blood overlay alpha dt_ms after a health drop of drop_hp (0 without one)"""
    if not (drop_hp > 0) or dt_ms != dt_ms or dt_ms < 0 or dt_ms >= HU["HitFadeMs"]:
        return 0.0
    a = max(HU["MinAlphaPermille"] / 1000.0, min(HU["MaxAlphaPermille"] / 1000.0, drop_hp / HU["FullHitHp"]))
    return a * (1.0 - dt_ms / HU["HitFadeMs"])

def is_debris(blocks, volume):
    """format debris.grid: a remnant (blocks NaN = unknown: the volume alone)"""
    if volume != volume or volume < DB["MinVolumeM3"]:
        return True
    if blocks == blocks and blocks + 1e-6 < DB["MinBlocks"]:
        return True
    return False

def estimate_blocks(occupied, edge):
    if not (edge > 0) or occupied != occupied:
        return float("nan")
    return max(0.0, occupied) / (edge ** 3)

def low_health_floor(ratio, critical=0.2):

    if ratio != ratio or not (critical > 0) or ratio > critical:
        return 0.0
    return max(0.0, min(1.0, critical - ratio)) / critical + 0.3

def blood_combine(pulse, floor, sn):
    p = 0.0 if (sn or pulse != pulse) else max(0.0, pulse)
    f = 0.0 if floor != floor else max(0.0, floor)
    return max(p, f)

def sn_paints(sn_picture, c7_fresh, c7_flags):
    return bool(sn_picture and c7_fresh and (c7_flags & 1))

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
# A: danger model
# =====================================================================================================================
def tier_of(max_health):
    if max_health != max_health:
        return 1
    if max_health >= TIER_LEVIATHAN_MIN:
        return 3
    if max_health >= TIER_LARGE_MIN:
        return 2
    return 1

def applied(base, health, max_health, mult, floor):
    if not (base > 0) or math.isinf(base) or health != health or max_health != max_health:
        return 0.0
    want = base * (mult / 1000.0)
    if not (want > 0):
        return 0.0
    room = health - (floor / 1000.0) * max_health
    if not (room > 0):
        return 0.0
    return want if want < room else room

def weapon_row(cl, t):
    for r in WEAPON:
        if r[0] == cl and r[1] == t:
            return r
    return (cl, t, 1000, 0)

def applied_class(cl, base, health, max_health):
    r = weapon_row(cl, tier_of(max_health))
    return applied(base, health, max_health, r[2], r[3])

# =====================================================================================================================
# A: explosions
# =====================================================================================================================
def yield_code(y):
    if not (y > 0) or math.isinf(y):
        return 0
    v = math.floor(y / B["YieldUnitSe"] + 0.5)
    return int(max(1, min(65535, v)))

def yield_of(extra, intensity):
    if extra != 0:
        return extra * float(B["YieldUnitSe"])
    if not (intensity > 0) or math.isinf(intensity):
        return 0.0
    return min(1.0, intensity) * 1000.0

def class_bits(cl):
    return (cl << B["ClassShift"]) & 0xFFFF if 1 <= cl <= 4 else 0

def class_of_subtype(sub):
    c = (sub >> B["ClassShift"]) & 0xF
    return c if 1 <= c <= 4 else 4

def class_of_origin(character, small_grid):
    return 1 if character else (2 if small_grid else 4)

def cbrt(x):
    return math.pow(x, 1.0 / 3.0) if x > 0 else 0.0

def joins(g, e):
    dx, dy, dz = e["x"] - g["x"], e["y"] - g["y"], e["z"] - g["z"]
    lim = max(g["r"], e["r"]) + B["JoinPadM"]
    return dx * dx + dy * dy + dz * dz <= lim * lim

def blast(x, y, z, r, yld, t=0):
    return dict(x=x, y=y, z=z, r=r, yld=yld, s3=r * r * r if r > 0 else 0.0, n=1, t=t)

def merge(g, e):
    y0, y1 = g["yld"], e["yld"]
    w = y0 + y1
    if w > 0:
        g["x"] = (g["x"] * y0 + e["x"] * y1) / w
        g["y"] = (g["y"] * y0 + e["y"] * y1) / w
        g["z"] = (g["z"] * y0 + e["z"] * y1) / w
    else:
        n0, n1 = g["n"], e["n"]
        n = n0 + n1
        g["x"] = (g["x"] * n0 + e["x"] * n1) / n
        g["y"] = (g["y"] * n0 + e["y"] * n1) / n
        g["z"] = (g["z"] * n0 + e["z"] * n1) / n
    g["yld"] = w
    g["s3"] += e["s3"]
    g["r"] = cbrt(g["s3"])
    g["n"] += e["n"]
    if e["t"] < g["t"]:
        g["t"] = e["t"]

def group(batch, max_groups):
    groups = []
    if max_groups < 1:
        max_groups = 1
    for e in batch:
        at = -1
        for k, g in enumerate(groups):
            if joins(g, e):
                at = k
                break
        if at < 0 and len(groups) < max_groups:
            groups.append(dict(e))
            continue
        if at < 0:
            best = float("inf")
            for k, g in enumerate(groups):
                dx, dy, dz = e["x"] - g["x"], e["y"] - g["y"], e["z"] - g["z"]
                d = dx * dx + dy * dy + dz * dz
                if d < best:
                    best, at = d, k
        merge(groups[at], e)
    return groups

def damage_hp(yld, radius, distance, mult):
    if not (yld > 0) or not (radius > 0) or distance != distance or math.isinf(yld):
        return 0.0
    r = min(radius, B["DamageRadiusCapM"])
    d = max(0.0, distance)
    f = (r - d) / r
    if not (f > 0):
        return 0.0
    return yld * (mult / 1000.0) * f

def visual_radius(s3):
    return min(cbrt(s3), B["VisualRadiusCapM"])

class Chain:
    def __init__(self):
        self.open = []  # [sum, t0, absorbed]
        self.window = B["ChainWindowMs"]

    def admit(self, b, now):
        for c in self.open:
            if now >= c[1] + self.window:
                continue
            if not joins(c[0], b):
                continue
            merge(c[0], b)
            c[2] += 1
            return False
        self.open.append([dict(b), now, 0])
        return True

    def close(self, now):
        out = []
        for i in range(len(self.open) - 1, -1, -1):
            c = self.open[i]
            if now < c[1] + self.window:
                continue
            del self.open[i]
            if c[2] < 1:
                continue
            s = dict(c[0])
            s["r"] = visual_radius(s["s3"])
            s["t"] = now
            out.append(s)
        return out

# =====================================================================================================================
# C: grabs and pushes
# =====================================================================================================================
def grab_row(name):
    for g in GRAB:
        if g[0].lower() == name.lower():
            return g
    return None

def push_row(name):
    for p in PUSH:
        if p[0].lower() == name.lower():
            return p
    return None

def size_fits(g, sx, sy, sz):
    if not (sx >= 0 and sy >= 0 and sz >= 0) or math.isinf(sx) or math.isinf(sy) or math.isinf(sz):
        return False
    if g[4] <= 0:
        return False
    vol = sx * sy * sz
    edge = max(sx, sy, sz)
    return vol <= g[4] + 1e-9 and edge <= g[5] / 10.0 + 1e-9

def hold_seconds(g, rnd):
    if rnd != rnd:
        rnd = 0.0
    rnd = max(0.0, min(1.0, rnd))
    return (g[6] + rnd * g[7]) / 1000.0

def hold_damage(g, dps):
    if not (dps > 0) or math.isinf(dps):
        return 0.0
    return dps * (g[10] / 1000.0) * (g[9] / 1000.0)

def rage_row(name):
    for r in RAGE:
        if r[0] == name:
            return r
    return None

def blend_factor(blend_ms, held_ms):
    """format hold.kinematic: min(1, held / blendMs); 1 without a blend"""
    if not (blend_ms > 0):
        return 1.0
    if held_ms != held_ms or held_ms <= 0:
        return 0.0
    return min(1.0, held_ms / blend_ms)

def blend_target(a, xt, f):
    if f != f:
        f = 1.0
    f = max(0.0, min(1.0, f))
    return v_add(a, v_mul(v_sub(xt, a), f))

def blend_velocity(a, xt, vt, f, blend_ms):
    if f != f:
        f = 1.0
    f = max(0.0, min(1.0, f))
    v = v_mul(vt, f)
    if f < 1.0 and blend_ms > 0:
        v = v_add(v, v_mul(v_sub(xt, a), 1000.0 / blend_ms))
    return v

def top_releases(top_depth_m):
    """format surface.release: a body's top within MarginCm of the surface"""
    if top_depth_m != top_depth_m:
        return False
    return top_depth_m < SF["MarginCm"] / 100.0

def follow_y(y, half_h):
    h = 0.0 if half_h != half_h or half_h < 0 else half_h
    cap = -(h + SF["MarginCm"] / 100.0)
    if y != y:
        return cap
    return y if y < cap else cap

def release_velocity(v, up, surface):
    """format hold.kinematic: at most ReleaseSpeedCmS, no upward component at a surface release"""
    if any(c != c for c in v):
        return (0.0, 0.0, 0.0)
    if surface:
        u = v[0] * up[0] + v[1] * up[1] + v[2] * up[2]
        if u > 0:
            v = v_sub(v, v_mul(up, u))
    l, cap = v_len(v), H["ReleaseSpeedCmS"] / 100.0
    return v_mul(v, cap / l) if l > cap else v

def back_cast_m(speed):
    b = MS["BackCastCm"] / 100.0
    if not (speed > 0) or math.isinf(speed):
        return b
    return max(b, speed * MS["BackCastMs"] / 1000.0)

def behind(hit, origin, flight):
    l = v_len(flight)
    if not (l > 1e-9):
        return False
    d = v_sub(hit, origin)
    return (d[0] * flight[0] + d[1] * flight[1] + d[2] * flight[2]) / l < -1.0

class HoldRage:
    """format rage.hold: one hold's book; outcomes 0 none, 1 rage, 2 release (each once per hold)"""

    def __init__(self):
        self.taken, self.raged, self.released, self.hits = 0.0, False, False, 0

    def hit(self, applied, max_health, row):
        if self.released or not (applied > 0) or math.isinf(applied):
            return 0
        self.hits += 1
        self.taken += applied
        if max_health > 0 and row[2] > 0 and self.taken >= row[2] / 1000.0 * max_health - 1e-9:
            self.released = True
            return 2
        if not self.raged:
            self.raged = True
            return 1
        return 0

    def damage_mult(self, row):
        return row[3] / 1000.0 if self.raged and row[3] > 0 else 1.0

def slug_yield(pool):
    if not (pool > 0) or math.isinf(pool):
        return 0.0
    return pool * (MS["SlugPermille"] / 1000.0)

def attributed(age_ms, dist_m):
    if age_ms != age_ms or dist_m != dist_m or age_ms < 0:
        return False
    return age_ms <= MS["AttributionMs"] and dist_m <= B["JoinPadM"]

def first_creature(kinds):
    for i, k in enumerate(kinds):
        if k == 1:
            return i
        if k == 2:
            return -1
    return -1

def releases_on(g, sn_type):
    if not g[12] or g[12] == "none":
        return False
    for n in g[12].split(","):
        n = n.strip()
        if n in SN_TYPES and SN_TYPES.index(n) == sn_type:
            return True
    return False

def v_add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])

def v_sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])

def v_mul(a, k):
    return (a[0] * k, a[1] * k, a[2] * k)

def v_len(a):
    return math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])

def hold_accel(xt, x, vt, v, omega=H["OmegaMilli"] / 1000.0, zeta=H["ZetaPermille"] / 1000.0, amax=H["MaxAccelCmS2"] / 100.0):
    a = v_add(v_mul(v_sub(xt, x), omega * omega), v_mul(v_sub(vt, v), 2.0 * zeta * omega))
    l = v_len(a)
    if l != l:
        return (0.0, 0.0, 0.0)
    if not (l > amax) or not (amax > 0):
        return a
    return v_mul(a, amax / l)

def max_hold_accel(scale):
    f = 1.0 if scale != scale else max(1.0, scale)
    return H["MaxAccelCmS2"] / 100.0 * f

def q_mul(a, b):  # (x, y, z, w), the Wire Quat product
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)

def q_norm(q):
    l = math.sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3])
    return (q[0] / l, q[1] / l, q[2] / l, q[3] / l) if l > 0 else (0.0, 0.0, 0.0, 1.0)

def ang_accel(qt, q, w, omega=H["AngOmegaMilli"] / 1000.0, zeta=H["ZetaPermille"] / 1000.0, amax=H["MaxAngAccelMilli"] / 1000.0):
    e = q_norm(q_mul(qt, (-q[0], -q[1], -q[2], q[3])))
    if e[3] < 0:
        e = (-e[0], -e[1], -e[2], -e[3])
    s = math.sqrt(e[0] * e[0] + e[1] * e[1] + e[2] * e[2])
    rot = (0.0, 0.0, 0.0)
    if s > 1e-12:
        ang = 2.0 * math.atan2(s, e[3])
        rot = (e[0] / s * ang, e[1] / s * ang, e[2] / s * ang)
    a = v_sub(v_mul(rot, omega * omega), v_mul(w, 2.0 * zeta * omega))
    l = v_len(a)
    if l != l:
        return (0.0, 0.0, 0.0)
    if not (l > amax) or not (amax > 0):
        return a
    return v_mul(a, amax / l)

def rotation_vector(qt, q):
    e = q_norm(q_mul(qt, (-q[0], -q[1], -q[2], q[3])))
    if e[3] < 0:
        e = (-e[0], -e[1], -e[2], -e[3])
    s = math.sqrt(e[0] * e[0] + e[1] * e[1] + e[2] * e[2])
    if not (s > 1e-12):
        return (0.0, 0.0, 0.0)
    ang = 2.0 * math.atan2(s, e[3])
    return (e[0] / s * ang, e[1] / s * ang, e[2] / s * ang)

def target_now(target, vel, age_ms):
    lead = 0.0 if age_ms != age_ms else max(0.0, min(H["LeadMaxMs"], age_ms)) / 1000.0
    return v_add(target, v_mul(vel, lead))

def servo_velocity(xt, x, vt):
    return v_add(vt, v_mul(v_sub(xt, x), H["ServoKpMilli"] / 1000.0))

def servo_angular(qt, q):
    w = v_mul(rotation_vector(qt, q), H["ServoKRotMilli"] / 1000.0)
    l = v_len(w)
    cap = H["MaxAngVelMilli"] / 1000.0
    if l != l:
        return (0.0, 0.0, 0.0)
    return v_mul(w, cap / l) if l > cap else w

def snaps(err_m):
    return err_m > H["SnapCm"] / 100.0

def buoyancy_share(hold_cancels, factor):
    if not hold_cancels or factor != factor:
        return 1.0
    return 1.0 - max(0.0, min(1.0, factor))

def effective_mass(mass, scale):
    if not (mass > 0) or not (scale > 0) or math.isinf(scale):
        return mass if mass > 0 else 0.0
    return mass * scale * scale * scale

def push_impulse(p, mass, closing):
    if not (mass > 0) or closing != closing or math.isinf(mass):
        return 0.0
    j = mass * max(0.0, closing) * (p[2] / 1000.0)
    return min(j, 10000000.0)

def push_dv(p, j, grid_mass):
    if not (j > 0) or not (grid_mass > 0):
        return 0.0
    return min(j / grid_mass, p[3] / 100.0)

# =====================================================================================================================
# D/E: hazards, heal, oxygen, EMP
# =====================================================================================================================
def hazard_row(sn_type):
    for h in HAZARD:
        if h[1] == sn_type:
            return h
    return None

def hazard_forwarded(sn_type):
    h = hazard_row(sn_type)
    return h is not None and h[3] > 0

def hazard_se(sn_type, hp):
    h = hazard_row(sn_type)
    if h is None or not (hp > 0):
        return 0.0
    return hp * (h[3] / 1000.0)

def heat_per_second(temp, min_temp, base):
    if not (min_temp > 0) or temp != temp or not (temp >= min_temp):
        return 0.0
    return temp / min_temp * base

class RateWindow:
    def __init__(self):
        self.start, self.used, self.started = 0, 0.0, False

    def grant(self, now, want, cap):
        if not (want > 0):
            return 0.0
        if not self.started or now - self.start >= 1000 or now < self.start:
            self.start, self.used, self.started = now, 0.0, True
        room = cap - self.used
        if not (room > 0):
            return 0.0
        g = want if want < room else room
        self.used += g
        return g

def se_heal(hp, cur, mx):
    if not (hp > 0) or cur != cur or mx != mx:
        return 0.0
    want = hp * (dict(HEAL)["KPermille"] / 1000.0)
    room = mx - cur
    if not (room > 0):
        return 0.0
    return want if want < room else room

def litres(seconds, cap):
    if not (seconds > 0) or not (cap > 0):
        return 0.0
    return seconds / dict(OXYGEN)["TankS"] * cap

def emp_seconds(t):
    e = dict(EMP)
    if not (t > 0) or math.isinf(t):
        return float(e["MinS"])
    s = t * (e["SecondsPermille"] / 1000.0)
    return max(float(e["MinS"]), min(float(e["MaxS"]), s))

# --------------------------------------------------------------------------------------------------- C8 payload checks
REJECT = dict(None_=0, Unit=1, Amount=2, Kind=3, Type=4, Vector=5, Entity=6, NotForwarded=7)

def f32(v):
    return struct.unpack("<f", struct.pack("<f", v))[0]

def _finite(v):
    return all(not (math.isnan(c) or math.isinf(c)) for c in v)

def _unitish(v):
    if not _finite(v):
        return False
    l = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    return 0.5 < l < 1.5

def check_payload(r):
    """r: dict unit, amount, kind, type, local, world, dir, inst. Returns the S11Reject value."""
    u, a = r["unit"], f32(r["amount"])
    if not (17 <= u <= 23):
        return 1
    if math.isnan(a) or math.isinf(a) or not (a > 0):
        return 2
    if not _finite(r["world"]) or not _finite(r["local"]) or not _finite(r["dir"]):
        return 5
    k, t = r["kind"], r["type"]
    if u == 17:
        if a > 500:
            return 2
        if k != 2:
            return 3
        if not hazard_forwarded(t):
            return 7
        return 0
    if u == 18:
        if a > 500:
            return 2
        return 3 if k != 2 else 0
    if u == 19:
        if a > 120:
            return 2
        if k != 2:
            return 3
        if t < 1 or t > 4:
            return 4
        return 0
    if u == 20:
        if a > 60:
            return 2
        return 3 if k != 1 else 0
    if u == 21:
        if k != 1:
            return 3
        if t < 1 or t > 3:
            return 4
        if r["inst"] == 0:
            return 6
        if t != 3 and (not _unitish(r["dir"]) or not _unitish(r["local"])):
            return 5
        if a > 600:
            return 2
        return 0
    if u == 22:
        if a > 10000000:
            return 2
        if k != 1:
            return 3
        if not _unitish(r["dir"]):
            return 5
        return 0
    if u == 23:
        if a > 10:
            return 2
        if r["type"] not in (0, 1):
            return 4
        if k != 1:
            return 3
        tg, rt = r.get("target", 1), r.get("root", 1)
        if tg == 0 or tg != rt:
            return 6
        if not _unitish(r["dir"]):
            return 5
        return 0
    return 1

# =====================================================================================================================
# vectors
# =====================================================================================================================
class Lcg:
    def __init__(self, seed):
        self.s = seed & 0xFFFFFFFFFFFFFFFF

    def next(self):
        self.s = (self.s * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
        return (self.s >> 11) / float(1 << 53)

    def uni(self, a, b):
        return a + (b - a) * self.next()

def R(v):
    return repr(float(v))

def vec_s(v):
    return ",".join(R(c) for c in v)

def write_vectors(dirpath=VEC_DIR):
    os.makedirs(dirpath, exist_ok=True)
    files = {}
    rnd = Lcg(1111)

    # danger.tsv: class base health maxHealth -> tier applied
    rows = ["# class\tbase\thealth\tmaxHealth\ttier\tapplied (S11Danger, default table)"]
    hand = [(1, 30, 5000, 5000), (1, 30, 2510, 5000), (1, 30, 2500, 5000), (1, 30, 2000, 5000), (1, 30, 100, 100), (1, 30, 20, 100),
            (2, 33, 1300, 5000), (2, 33, 1250, 5000), (3, 33, 10, 5000), (3, 33, 40, 5000), (4, 15000, 5000, 5000), (4, 0, 100, 100),
            (1, 30, 300, 300), (2, 33, 199, 199), (3, 1e9, 8000, 8000), (1, 30, 4000, 8000), (1, 30, 4001, 8000)]
    for _ in range(80):
        mh = rnd.uni(5, 9000)
        hand.append((1 + int(rnd.next() * 4), rnd.uni(0, 2000), rnd.uni(0, mh), mh))
    for cl, base, hp, mh in hand:
        rows.append("%d\t%s\t%s\t%s\t%d\t%s" % (cl, R(base), R(hp), R(mh), tier_of(mh), R(applied_class(cl, base, hp, mh))))
    files["danger.tsv"] = rows

    # blast.tsv: yield codes, damage, groups
    rows = ["# code\tyield\textra / yieldof\textra\tintensity\tyield / damage\tyield\tradius\tdistance\tmult\thp / group\tmaxGroups\tbatch\t-> groups"]
    for y in [0, -5, 4, 5, 14.9, 15, 500, 15000, 655345, 655355, 1e9, float("inf")]:
        rows.append("code\t%s\t%d" % (R(y), yield_code(y)))
    for cl in range(0, 6):
        rows.append("cls	%d	%d" % (cl, class_bits(cl)))
    for sub in [0, 5, 0x105, 0x205, 0x305, 0x405, 0x505, 0xF05, 0x1405]:
        rows.append("clsof	%d	%d" % (sub, class_of_subtype(sub)))
    for ch, sg in [(True, False), (True, True), (False, True), (False, False)]:
        rows.append("origin	%d	%d	%d" % (1 if ch else 0, 1 if sg else 0, class_of_origin(ch, sg)))
    for extra, inten in [(0, 0.5), (0, 1.5), (0, 0.0), (1500, 1.0), (1, 0.0), (65535, 0.2)]:
        rows.append("yieldof\t%d\t%s\t%s" % (extra, R(f32(inten)), R(yield_of(extra, f32(inten)))))
    for y, r, d, m in [(15000, 22.4415, 0, 1000), (15000, 22.4415, 10, 1000), (15000, 22.4415, 22.5, 1000), (500, 5, 1, 1000),
                       (150000, 48.35, 30, 1000), (1e6, 300, 50, 1000), (1000, 4, -2, 500)]:
        rows.append("damage\t%s\t%s\t%s\t%d\t%s" % (R(y), R(r), R(d), m, R(damage_hp(y, r, d, m))))
    cases = []
    # five stacked large warheads in one tick, one far missile
    cases.append(([blast(0, 0, 0, 22.4415, 15000)] * 5 + [blast(300, 0, 0, 5, 500)], 16))
    # a row of warheads 3 m apart (each joins the growing group)
    cases.append(([blast(3.0 * i, 0, 0, 4.4883, 15000) for i in range(10)], 16))
    # more groups than allowed: the nearest group takes the rest
    cases.append(([blast(100.0 * i, 0, 0, 5, 500) for i in range(6)], 3))
    for _ in range(12):
        n = 1 + int(rnd.next() * 12)
        b = [blast(rnd.uni(-60, 60), rnd.uni(-30, 0), rnd.uni(-60, 60), rnd.uni(1, 25), rnd.uni(0, 20000)) for _ in range(n)]
        cases.append((b, 1 + int(rnd.next() * 6)))
    for batch, mg in cases:
        g = group(batch, mg)
        bs = ";".join("%s,%s,%s,%s,%s" % (R(e["x"]), R(e["y"]), R(e["z"]), R(e["r"]), R(e["yld"])) for e in batch)
        gs = ";".join("%s,%s,%s,%s,%s,%s,%d" % (R(e["x"]), R(e["y"]), R(e["z"]), R(e["r"]), R(e["yld"]), R(e["s3"]), e["n"]) for e in g)
        rows.append("group\t%d\t%s\t%s" % (mg, bs, gs))
    files["blast.tsv"] = rows

    # chain.tsv: a sequence of (t, blast) admits and closes; per step: played flag or escalations
    rows = ["# case\tt\top\tx,y,z,r,yield\t-> result (admit: 1 play / 0 absorbed; close: escalated x,y,z,r,yield,n;...)"]
    seqs = []
    # five warheads chaining over 450 ms at the same spot, then a lone missile later
    seqs.append([(0, blast(0, -20, 0, 22.4415, 15000)), (120, blast(1, -20, 0, 22.4415, 15000)), (260, blast(0, -21, 1, 22.4415, 15000)),
                 (380, blast(-1, -20, 0, 22.4415, 15000)), (450, blast(0, -20, -1, 22.4415, 15000)), (700, None), (2000, blast(0, -20, 0, 5, 500)),
                 (2700, None)])
    # two places at once never merge
    seqs.append([(0, blast(0, 0, 0, 5, 500)), (10, blast(200, 0, 0, 5, 500)), (20, blast(1, 0, 0, 5, 500)), (700, None)])
    for _ in range(6):
        seq, t = [], 0
        for _ in range(10):
            t += int(rnd.uni(0, 300))
            if rnd.next() < 0.2:
                seq.append((t, None))
            else:
                seq.append((t, blast(rnd.uni(-30, 30), -10, rnd.uni(-30, 30), rnd.uni(2, 23), rnd.uni(100, 15000))))
        seq.append((t + 1000, None))
        seqs.append(seq)
    for ci, seq in enumerate(seqs):
        ch = Chain()
        for t, b in seq:
            if b is None:
                out = ch.close(t)
                rows.append("%d\t%d\tclose\t\t%s" % (ci, t, ";".join("%s,%s,%s,%s,%s,%d" % (R(e["x"]), R(e["y"]), R(e["z"]), R(e["r"]), R(e["yld"]), e["n"]) for e in out)))
            else:
                ch.close(t)  # the player closes before it admits (S11BlastChain usage)
                p = ch.admit(b, t)
                rows.append("%d\t%d\tadmit\t%s,%s,%s,%s,%s\t%d" % (ci, t, R(b["x"]), R(b["y"]), R(b["z"]), R(b["r"]), R(b["yld"]), 1 if p else 0))
    files["chain.tsv"] = rows

    # grab.tsv
    rows = ["# fits creature sx sy sz -> 0/1 | hold creature rand -> s | holddmg creature dps -> hp | release creature type -> 0/1 | accel ... | ang ... | share ... | push ..."]
    for name, sx, sy, sz in [("ReaperLeviathan", 3, 2, 5), ("ReaperLeviathan", 3, 2.5, 7.5), ("ReaperLeviathan", 4, 4, 4), ("ReaperLeviathan", 1, 1, 8),
                             ("ReaperLeviathan", 2.5, 2.5, 2.5), ("ReaperLeviathan", 5, 5, 5), ("SeaDragon", 5, 5, 5), ("SeaDragon", 7.5, 5, 7.5),
                             ("SeaDragon", 12, 2, 2), ("SeaDragon", 12.5, 1, 1), ("ReaperLeviathan", 0, 0, 0), ("ReaperLeviathan", -1, 1, 1)]:
        rows.append("fits\t%s\t%s\t%s\t%s\t%d" % (name, R(sx), R(sy), R(sz), 1 if size_fits(grab_row(name), sx, sy, sz) else 0))
    for name, sx, sy, sz in [("ReaperLeviathan", 5.7, 4.1, 9.7), ("ReaperLeviathan", 15, 2, 2), ("ReaperLeviathan", 15.1, 1, 1), ("SeaDragon", 25, 6, 10)]:
        rows.append("fits\t%s\t%s\t%s\t%s\t%d" % (name, R(sx), R(sy), R(sz), 1 if size_fits(grab_row(name), sx, sy, sz) else 0))
    # 5 m, 1 large block 2.5 m cube, his ship 150 blocks 227 m3)
    for b, v in [(4, 0.5), (1, 15.625), (150, 226.9), (8, 8), (7, 100), (8, 7.9), (float("nan"), 15.625), (float("nan"), 7), (477, 5000), (9, 1.125)]:
        rows.append("debris\t%s\t%s\t%d" % (R(b), R(v), 1 if is_debris(b, v) else 0))
    for occ, edge in [(0.5, 0.5), (15.625, 2.5), (100, 0.5), (0, 2.5), (10, 0)]:
        rows.append("estblocks\t%s\t%s\t%s" % (R(occ), R(edge), R(estimate_blocks(occ, edge))))
    for m, sc in [(2050, 2.5), (2050, 1.0), (0, 2.0), (3000, 0.0), (1000, 1.5)]:
        rows.append("effmass\t%s\t%s\t%s" % (R(m), R(sc), R(effective_mass(m, sc))))
    for name, r in [("ReaperLeviathan", 0), ("ReaperLeviathan", 0.5), ("ReaperLeviathan", 1), ("ReaperLeviathan", 2), ("SeaDragon", 0.7)]:
        rows.append("hold\t%s\t%s\t%s" % (name, R(r), R(hold_seconds(grab_row(name), r))))
    for name, d in [("ReaperLeviathan", 15), ("SeaDragon", 15), ("ReaperLeviathan", 0), ("ReaperLeviathan", -3)]:
        rows.append("holddmg\t%s\t%s\t%s" % (name, R(d), R(hold_damage(grab_row(name), d))))
    for name in ("ReaperLeviathan", "SeaDragon"):
        for t in range(17):
            rows.append("release\t%s\t%d\t%d" % (name, t, 1 if releases_on(grab_row(name), t) else 0))
    for _ in range(30):
        xt = (rnd.uni(-20, 20), rnd.uni(-20, 20), rnd.uni(-20, 20))
        x = (rnd.uni(-20, 20), rnd.uni(-20, 20), rnd.uni(-20, 20))
        vt = (rnd.uni(-5, 5), rnd.uni(-5, 5), rnd.uni(-5, 5))
        v = (rnd.uni(-5, 5), rnd.uni(-5, 5), rnd.uni(-5, 5))
        rows.append("accel\t%s\t%s\t%s\t%s\t%s" % (vec_s(xt), vec_s(x), vec_s(vt), vec_s(v), vec_s(hold_accel(xt, x, vt, v))))
    for _ in range(30):
        qt = q_norm((rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1)))
        q = q_norm((rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1)))
        w = (rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1))
        rows.append("ang\t%s\t%s\t%s\t%s" % (",".join(R(c) for c in qt), ",".join(R(c) for c in q), vec_s(w), vec_s(ang_accel(qt, q, w))))
    # S11b 20261024-000956: the kinematic hold
    for _ in range(12):
        xt = (rnd.uni(-20, 20), rnd.uni(-20, 20), rnd.uni(-20, 20))
        x = (rnd.uni(-20, 20), rnd.uni(-20, 20), rnd.uni(-20, 20))
        vt = (rnd.uni(-40, 40), rnd.uni(-40, 40), rnd.uni(-40, 40))
        rows.append("servo\t%s\t%s\t%s\t%s" % (vec_s(xt), vec_s(x), vec_s(vt), vec_s(servo_velocity(xt, x, vt))))
    for _ in range(12):
        qt = q_norm((rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1)))
        q = q_norm((rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1), rnd.uni(-1, 1)))
        rows.append("servoang\t%s\t%s\t%s" % (",".join(R(c) for c in qt), ",".join(R(c) for c in q), vec_s(servo_angular(qt, q))))
    for age in (0, 25, 50, 100, 500, -5):
        rows.append("lead\t%s\t%s\t%s\t%s" % (vec_s((1, 2, 3)), vec_s((40, 0, -10)), R(age), vec_s(target_now((1, 2, 3), (40, 0, -10), age))))
    for e in (0.5, 1.0, 1.0001, 27.0):
        rows.append("snap\t%s\t%d" % (R(e), 1 if snaps(e) else 0))
    for sc in (3.0, 2.5, 1.8, 1.0, 0.5, float("nan")):
        rows.append("amax\t%s\t%s" % (R(sc), R(max_hold_accel(sc))))
    for hc, f in [(False, 1.0), (True, 1.0), (True, 0.5), (True, 2.0), (True, -1.0)]:
        rows.append("share\t%d\t%s\t%s" % (1 if hc else 0, R(f), R(buoyancy_share(hc, f))))
    for name, m, s, gm in [("ReaperLeviathan", 4000, 10, 20000), ("ReaperLeviathan", 4000, 10, 2000), ("GhostLeviathan", 8000, -3, 5000),
                           ("SeaDragon", 1e9, 1e9, 1), ("GhostLeviathanJuvenile", 2500, 6, 30000)]:
        p = push_row(name)
        j = push_impulse(p, m, s)
        rows.append("push\t%s\t%s\t%s\t%s\t%s\t%s" % (name, R(m), R(s), R(gm), R(j), R(push_dv(p, j, gm))))
    #: the jaw centre (blend), the surface, the rage book, missiles
    for bm, held in [(1000, 0), (1000, 250), (1000, 999), (1000, 1000), (1000, 5000), (250, 125), (0, 10), (1000, -5)]:
        rows.append("blend\t%s\t%s\t%s" % (R(bm), R(held), R(blend_factor(bm, held))))
    for _ in range(8):
        a = (rnd.uni(-20, 20), rnd.uni(-20, 20), rnd.uni(-20, 20))
        xt = (rnd.uni(-20, 20), rnd.uni(-20, 20), rnd.uni(-20, 20))
        vt = (rnd.uni(-40, 40), rnd.uni(-40, 40), rnd.uni(-40, 40))
        f = rnd.next() if rnd.next() < 0.7 else 1.0
        bm = 1000.0 if rnd.next() < 0.8 else 250.0
        rows.append("blendxt\t%s\t%s\t%s\t%s\t%s\t%s\t%s" % (vec_s(a), vec_s(xt), vec_s(vt), R(f), R(bm), vec_s(blend_target(a, xt, f)), vec_s(blend_velocity(a, xt, vt, f, bm))))
    for d in (5.0, 2.0, 1.9999, 0.5, -3.0, float("nan")):
        rows.append("surf\t%s\t%d" % (R(d), 1 if top_releases(d) else 0))
    for y, h in ((-20.0, 4.5), (-5.0, 0.0), (-4.99, 3.0), (0.0, 8.2), (12.0, float("nan")), (-30.0, 12.0)):
        rows.append("followy\t%s\t%s\t%s" % (R(y), R(h), R(follow_y(y, h))))
    for v, up, sf in (((30.0, 40.0, 0.0), (0.0, 1.0, 0.0), True), ((30.0, 40.0, 0.0), (0.0, 1.0, 0.0), False), ((3.0, -4.0, 0.0), (0.0, 1.0, 0.0), True),
                      ((0.0, 0.0, 150.0), (0.6, 0.8, 0.0), True), ((1.0, 2.0, 2.0), (0.0, 1.0, 0.0), True)):
        rows.append("relv\t%s\t%s\t%d\t%s" % (vec_s(v), vec_s(up), 1 if sf else 0, vec_s(release_velocity(v, up, sf))))
    for sp in (0.0, 200.0, 300.0, 2000.0, float("inf")):
        rows.append("backcast\t%s\t%s" % (R(sp), R(back_cast_m(sp))))
    for hit, o, f in (((0, 0, 10), (0, 0, 0), (0, 0, 1)), ((0, 0, -0.5), (0, 0, 0), (0, 0, 1)), ((0, 0, -1.5), (0, 0, 0), (0, 0, 2)), ((5, 0, -3), (0, 0, 0), (0, 0, 0))):
        rows.append("behind\t%s\t%s\t%s\t%d" % (vec_s(hit), vec_s(o), vec_s(f), 1 if behind(hit, o, f) else 0))
    for name, mh, hits in [("ReaperLeviathan", 5000.0, [500.0, 500.0]), ("ReaperLeviathan", 5000.0, [100.0, 100.0, 100.0]), ("ReaperLeviathan", 5000.0, [750.0, 50.0]),
                           ("ReaperLeviathan", 5000.0, [0.0, -5.0, 749.99]), ("SeaDragon", 5000.0, [300.0, 300.0, 300.0, 300.0]), ("ReaperLeviathan", 0.0, [900.0, 900.0])]:
        book, row = HoldRage(), rage_row(name)
        outs = []
        for h in hits:
            o = book.hit(h, mh, row)
            outs.append("%d:%s" % (o, R(book.damage_mult(row))))
        rows.append("rage\t%s\t%s\t%s\t%s" % (name, R(mh), ",".join(R(h) for h in hits), ";".join(outs)))
    for pool in (50000.0, 8000.0, 17000.0, 0.0, -3.0, 4000.0):
        rows.append("slug\t%s\t%s" % (R(pool), R(slug_yield(pool))))
    for age, dist in [(0, 0), (1500, 2), (1501, 0), (200, 2.01), (-1, 0)]:
        rows.append("attr\t%s\t%s\t%d" % (R(age), R(dist), 1 if attributed(age, dist) else 0))
    for kinds in ([1], [0, 1], [0, 2, 1], [2, 1], [], [0, 0], [0, 0, 1, 2]):
        rows.append("first\t%s\t%d" % (",".join(str(k) for k in kinds) or "-", first_creature(kinds)))
    files["grab.tsv"] = rows

    # hazard.tsv
    rows = ["# fwd type -> 0/1 | se type hp -> se | heat temp min base -> hp/s | rate case (t want cap -> granted)... | heal hp cur max -> se | litres s cap -> L | emp t -> s"]
    for t in range(17):
        rows.append("fwd\t%d\t%d" % (t, 1 if hazard_forwarded(t) else 0))
    for t, hp in [(1, 2.5), (6, 10), (12, 10), (0, 30), (8, 15), (14, 0.0), (16, 0.2)]:
        rows.append("se\t%d\t%s\t%s" % (t, R(hp), R(hazard_se(t, hp))))
    for temp, mn, b in [(70, 49, 0.2), (49, 49, 0.2), (48.9, 49, 0.2), (400, 49, 0.2), (100, 0, 0.2)]:
        rows.append("heat\t%s\t%s\t%s\t%s" % (R(temp), R(mn), R(b), R(heat_per_second(temp, mn, b))))
    for ci in range(4):
        w = RateWindow()
        t = 0
        steps = []
        for _ in range(12):
            t += int(rnd.uni(0, 400))
            want = rnd.uni(0, 25)
            steps.append("%d,%s,%s" % (t, R(want), R(w.grant(t, want, 30.0))))
        rows.append("rate\t%d\t30.0\t%s" % (ci, ";".join(steps)))
    for hp, cur, mx in [(50, 40, 100), (50, 80, 100), (50, 100, 100), (0, 10, 100), (10, 120, 150)]:
        rows.append("heal\t%s\t%s\t%s\t%s" % (R(hp), R(cur), R(mx), R(se_heal(hp, cur, mx))))
    for s, cap in [(10, 100), (45, 100), (0.2, 100), (30, 2.3), (0, 100)]:
        rows.append("litres\t%s\t%s\t%s" % (R(s), R(cap), R(litres(s, cap))))
    for t in [2, 0, -1, 0.5, 45, 100]:
        rows.append("emp\t%s\t%s" % (R(t), R(emp_seconds(t))))
    # pass 5: SE's blood overlay (format hud.blood)
    for dt, hp in [(0, 0.2), (0, 2), (0, 10), (0, 55), (400, 0.2), (400, 10), (799, 10), (800, 10), (5000, 80), (0, 0), (-1, 10), (100, -3)]:
        rows.append("blood\t%s\t%s\t%s" % (R(dt), R(hp), R(blood_alpha(dt, hp))))
    for ratio in (1.0, 0.5, 0.2, 0.19, 0.1, 0.0):
        rows.append("lowhp\t%s\t%s" % (R(ratio), R(low_health_floor(ratio))))
    for pulse, ratio, sn in [(0.8, 1.0, 0), (0.8, 1.0, 1), (0.2, 0.1, 0), (0.2, 0.1, 1), (0.0, 0.15, 1), (0.9, 0.19, 0)]:
        fl = low_health_floor(ratio)
        rows.append("bloodmix\t%s\t%s\t%d\t%s" % (R(pulse), R(fl), sn, R(blood_combine(pulse, fl, sn))))
    for pic, fresh, flags in [(1, 1, 1), (1, 1, 3), (1, 1, 2), (1, 0, 1), (0, 1, 1), (0, 0, 0)]:
        rows.append("snpaints\t%d\t%d\t%d\t%d" % (pic, fresh, flags, 1 if sn_paints(pic, fresh, flags) else 0))
    files["hazard.tsv"] = rows

    # records.tsv: payload checks
    rows = ["# unit\tamount\tkind\ttype\tlocal\tworld\tdir\tinst\t-> S11Reject\ttarget\troot"]
    nan = float("nan")
    recs = [
        dict(unit=17, amount=2.5, kind=2, type=1, local=(0, 0, 0), world=(1, 2, 3), dir=(0, 0, 0), inst=0),
        dict(unit=17, amount=2.5, kind=2, type=0, local=(0, 0, 0), world=(1, 2, 3), dir=(0, 0, 0), inst=0),
        dict(unit=17, amount=2.5, kind=1, type=1, local=(0, 0, 0), world=(1, 2, 3), dir=(0, 0, 0), inst=0),
        dict(unit=17, amount=501, kind=2, type=1, local=(0, 0, 0), world=(1, 2, 3), dir=(0, 0, 0), inst=0),
        dict(unit=17, amount=0, kind=2, type=1, local=(0, 0, 0), world=(1, 2, 3), dir=(0, 0, 0), inst=0),
        dict(unit=18, amount=50, kind=2, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=18, amount=50, kind=1, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=19, amount=10, kind=2, type=1, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=19, amount=10, kind=2, type=5, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=19, amount=121, kind=2, type=2, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=20, amount=2, kind=1, type=0, local=(1, 1, 1), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=20, amount=61, kind=1, type=0, local=(1, 1, 1), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=21, amount=9.5, kind=1, type=1, local=(0, 1, 0), world=(0, 0, 0), dir=(0, 0, 1), inst=77),
        dict(unit=21, amount=1, kind=1, type=2, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 1), inst=77),
        dict(unit=21, amount=1, kind=1, type=3, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=77),
        dict(unit=21, amount=1, kind=1, type=2, local=(0, 1, 0), world=(0, 0, 0), dir=(0, 0, 1), inst=0),
        dict(unit=21, amount=1, kind=1, type=4, local=(0, 1, 0), world=(0, 0, 0), dir=(0, 0, 1), inst=7),
        dict(unit=22, amount=40000, kind=1, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(1, 0, 0), inst=0),
        dict(unit=22, amount=40000, kind=1, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=22, amount=4e7, kind=1, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(1, 0, 0), inst=0),
        dict(unit=1, amount=30, kind=2, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=23, amount=30, kind=2, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=23, amount=1, kind=1, type=0, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 1), inst=99, target=123456, root=123456),
        dict(unit=23, amount=1, kind=2, type=0, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 1), inst=99, target=123456, root=123456),
        dict(unit=23, amount=1, kind=1, type=0, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 1), inst=99, target=0, root=0),
        dict(unit=23, amount=1, kind=1, type=0, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 1), inst=99, target=5, root=6),
        dict(unit=23, amount=1, kind=1, type=0, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 0), inst=99, target=5, root=5),
        dict(unit=23, amount=11, kind=1, type=0, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 1), inst=99, target=5, root=5),
        dict(unit=24, amount=1, kind=1, type=0, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 1), inst=99, target=5, root=5),
        dict(unit=23, amount=1, kind=1, type=1, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 1), inst=99, target=5, root=5),
        dict(unit=23, amount=1, kind=1, type=2, local=(0, 0, 0), world=(5, -20, 30), dir=(0, 0, 1), inst=99, target=5, root=5),
        dict(unit=17, amount=nan, kind=2, type=1, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
        dict(unit=17, amount=3, kind=2, type=1, local=(nan, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0),
    ]
    for r in recs:
        rows.append("%d\t%s\t%d\t%d\t%s\t%s\t%s\t%d\t%d\t%d\t%d" % (r["unit"], R(f32(r["amount"])), r["kind"], r["type"], vec_s(r["local"]), vec_s(r["world"]),
                                                               vec_s(r["dir"]), r["inst"], check_payload(r), r.get("target", 1), r.get("root", 1)))
    files["records.tsv"] = rows

    for name, lines in files.items():
        with open(os.path.join(dirpath, name), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")
    return sorted(files)

# =====================================================================================================================
# selftest
# =====================================================================================================================
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

def selftest():
    fails = []

    def ok(cond, what):
        if not cond:
            fails.append(what)

    h, lines, nbytes = text_hash()
    ok(h == PINNED_HASH, "pin: hash 0x%016x != pinned 0x%016x" % (h, PINNED_HASH))
    ok(lines == CANON_LINES and nbytes == CANON_BYTES, "pin: %d lines %d bytes != %d/%d" % (lines, nbytes, CANON_LINES, CANON_BYTES))
    blk = contract_block()
    ok(blk == canonical_text(), "CONTRACT 5.4 'S11 conventions' block differs from the twin's text")
    # A: a handheld weapon never takes a leviathan below half its health; a warhead kills it
    hp = 5000.0
    shots = 0
    while shots < 10000:
        d = applied_class(1, 30.0, hp, 5000.0)
        if d <= 0:
            break
        hp -= d
        shots += 1
    ok(abs(hp - 2500.0) < 1e-9, "A: handheld floor: reaper stopped at %r (want 2500)" % hp)
    ok(applied_class(4, damage_hp(15000, 22.4415, 5.0, 1000), 5000.0, 5000.0) == 5000.0, "A: one warhead at 5 m kills a reaper")
    ok(applied_class(3, 33.0, 10.0, 5000.0) == 10.0, "A: a large turret finishes a leviathan")
    ok(applied_class(2, 33.0, 1260.0, 5000.0) == 10.0, "A: a small grid turret stops at a quarter")
    ok(tier_of(199.9) == 1 and tier_of(200) == 2 and tier_of(2000) == 3, "A: tiers")
    # A: stacked warheads: five in one tick act as five yields, the radius grows by cbrt(5)
    g = group([blast(0, 0, 0, 22.4415, 15000)] * 5, 16)
    ok(len(g) == 1 and g[0]["yld"] == 75000.0 and g[0]["n"] == 5, "A: five warheads -> one group of 75,000")
    ok(abs(g[0]["r"] - 22.4415 * 5 ** (1.0 / 3.0)) < 1e-9, "A: cluster radius = r * cbrt(5)")
    ok(yield_code(15000) == 1500 and yield_of(1500, 1.0) == 15000.0 and yield_of(0, 0.5) == 500.0, "A: yield code")
    ok(class_of_subtype(class_bits(1) | 5) == 1 and class_of_subtype(5) == 4, "A: an explosion's class in the subtype (0 = Explosion)")
    ok(applied_class(class_of_origin(True, False), damage_hp(20000, 30, 1, 1000), 5000.0, 5000.0) == 2500.0, "A: a handheld rocket stops at the floor")
    ch = Chain()
    ok(ch.admit(blast(0, 0, 0, 22, 15000), 0) is True, "A: chain first row plays")
    ok(ch.admit(blast(1, 0, 0, 22, 15000), 300) is False, "A: chain second row absorbed")
    esc = ch.close(600)
    ok(len(esc) == 1 and esc[0]["yld"] == 30000.0 and esc[0]["n"] == 2, "A: chain escalates once with the sum")
    ok(ch.admit(blast(0, 0, 0, 22, 15000), 700) is True, "A: a later row opens a new chain")
    # C
    ok(size_fits(grab_row("ReaperLeviathan"), 5.7, 4.1, 9.7) and size_fits(grab_row("ReaperLeviathan"), 12.5, 12.5, 2.5) and not size_fits(grab_row("ReaperLeviathan"), 8, 8, 8)
       and not size_fits(grab_row("ReaperLeviathan"), 15.5, 2, 2), "C: the x2.5 reaper's size class 400 m3 / 15 m (the owner's small ship and a 5 x 5 large wall fit)")
    ok(size_fits(grab_row("SeaDragon"), 12.5, 12.5, 12.5) and not size_fits(grab_row("SeaDragon"), 15, 15, 15) and not size_fits(grab_row("SeaDragon"), 35, 2.5, 2.5),
       "C: sea dragon size class 2,900 m3 / 31 m")
    ok(is_debris(4, 0.5) and is_debris(1, 15.625) and not is_debris(150, 226.9) and not is_debris(25, 390.6), "debris: the session's fragments are debris, his ship and a 5 x 5 wall are not")
    ok(abs(blood_alpha(0, 0.2) - 0.2) < 1e-12 and abs(blood_alpha(400, 10) - 0.4) < 1e-12 and blood_alpha(800, 80) == 0.0, "hud.blood: a 0.2 HP tick is faint, every drop is gone after 800 ms")
    ok(not hazard_forwarded(12), "D: radiation is off (owner 2026-10-23)")
    ok(abs(effective_mass(2050, 2.5) - 2050 * 15.625) < 1e-9, "C: a 2.5 x reaper pushes with 15.6 x its mass")
    ok(hold_seconds(grab_row("ReaperLeviathan"), 0.5) == 10.5, "C: reaper hold 8 + 0.5*5 s")
    ok(releases_on(grab_row("ReaperLeviathan"), 8) and releases_on(grab_row("ReaperLeviathan"), 5) and not releases_on(grab_row("ReaperLeviathan"), 0),
       "C: reaper lets go on Electrical and Poison")
    a = hold_accel((10, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0))
    ok(abs(v_len(a) - 150.0) < 1e-9, "C: hold acceleration capped at 150 m/s^2")
    #: the jaw centre, the surface, the rage book, missiles
    ok(blend_factor(1000, 500) == 0.5 and blend_factor(1000, 2000) == 1.0 and blend_factor(0, 0) == 1.0, "C: the blend onto the jaw centre (1 s for the reaper)")
    ok(blend_target((0, 0, 0), (10, 0, 0), 0.5) == (5.0, 0.0, 0.0), "C: the blended target")
    ok(grab_row("ReaperLeviathan")[11] == 1000 and grab_row("SeaDragon")[11] == 250, "C: blend 1 s reaper (ReaperLeviathan.cs:161), 0.25 s dragon (SeaDragon.cs:133)")
    ok(top_releases(1.9) and not top_releases(2.0) and follow_y(3.0, 8.0) == -10.0 and follow_y(-30.0, 8.0) == -30.0, "surface: a top within 2 m lets go; a x3 reaper (half height 8 m) is steered 10 m deep")
    ok(release_velocity((30.0, 40.0, 0.0), (0.0, 1.0, 0.0), True) == (10.0, 0.0, 0.0) and abs(v_len(release_velocity((0, 0, 150.0), (0, 1, 0), False)) - 10.0) < 1e-12,
       "release: at most 10 m/s, never upward at the surface")
    ok(back_cast_m(2000.0) == 200.0 and back_cast_m(200.0) == 30.0 and behind((0, 0, -2), (0, 0, 0), (0, 0, 1)) and not behind((0, 0, 3), (0, 0, 0), (0, 0, 1)), "missile: the rail back-cast, the launch point")
    bk = HoldRage()
    rr = rage_row("ReaperLeviathan")
    ok([bk.hit(500, 5000, rr), bk.hit(500, 5000, rr), bk.hit(500, 5000, rr), bk.hit(900, 5000, rr)] == [1, 0, 2, 0] and bk.damage_mult(rr) == 1.5,
       "rage: one rocket angers once, the third (1,500 = 30 %) lets go once, nothing after")
    bk = HoldRage()
    ok(bk.hit(1000, 5000, rr) == 1 and bk.hit(500, 5000, rr) == 2, "rage (owner 2026-10-24): a two-rocket salvo (1,000) angers, the third rocket lets go")
    bk = HoldRage()
    ok(bk.hit(1500, 5000, rr) == 2 and not bk.raged, "rage: a three-rocket salvo (1,500) lets go at once, no rage")
    ok(slug_yield(50000) == 1000.0 and slug_yield(8000) == 160.0 and slug_yield(0) == 0.0, "missile: a large railgun slug 1,000 (review: 5,000 killed a reaper), a small one 160")
    ok(first_creature([0, 1]) == 1 and first_creature([2, 1]) == -1, "missile: the first creature before any other solid")
    ok(check_payload(dict(unit=23, amount=1, kind=1, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 1), inst=9, target=5, root=5)) == 0, "records: missile hit ok")
    # D/E
    ok(hazard_forwarded(1) and not hazard_forwarded(12) and not hazard_forwarded(0) and not hazard_forwarded(2), "D: hazard set")
    w = RateWindow()
    ok(w.grant(0, 25, 30) == 25 and w.grant(500, 25, 30) == 5 and w.grant(999, 1, 30) == 0 and w.grant(1000, 7, 30) == 7, "D: per-second cap")
    ok(abs(litres(45, 100) - 100) < 1e-12 and se_heal(50, 80, 100) == 20, "E: litres and heal")
    ok(emp_seconds(2) == 2 and emp_seconds(100) == 30, "D: EMP seconds")
    ok(check_payload(dict(unit=17, amount=2.5, kind=2, type=1, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0)) == 0, "records: hazard ok")
    ok(check_payload(dict(unit=17, amount=2.5, kind=2, type=0, local=(0, 0, 0), world=(0, 0, 0), dir=(0, 0, 0), inst=0)) == 7, "records: Normal is S6's")
    # vectors current
    import tempfile
    with tempfile.TemporaryDirectory() as td:
        names = write_vectors(td)
        for n in names:
            want = open(os.path.join(td, n), encoding="utf-8").read()
            p = os.path.join(VEC_DIR, n)
            have = open(p, encoding="utf-8").read() if os.path.exists(p) else None
            ok(have == want, "vectors: %s is not current (python -I tools/s11/s11_ref.py --vectors)" % n)
    for f in fails:
        print("FAIL " + f)
    print("S11 REF SELFTEST %s (%d checks failed)" % ("PASS" if not fails else "FAIL", len(fails)))
    return 0 if not fails else 1

def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if argv[0] == "--text":
        sys.stdout.write(canonical_text())
        return 0
    if argv[0] == "--pin":
        h, lines, nbytes = text_hash()
        print("0x%016x %d lines %d bytes" % (h, lines, nbytes))
        return 0
    if argv[0] == "--vectors":
        for n in write_vectors():
            print("wrote tests/s11/vectors/" + n)
        return 0
    if argv[0] == "--selftest":
        return selftest()
    print(__doc__)
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
