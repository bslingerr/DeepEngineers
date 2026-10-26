"""S12 conventions, the Python twin of the Wire interiors-v2 rules (src/DeepEngineers.Wire/S12Conventions.cs, S12Rules.cs):
the canonical text and its hash (`ext s12 interiors 3`; CONTRACT §5.4 "S12 conventions"), the oxygen-volume test of the
air rule (format air.volume) and its hysteresis (air.hold), the camera above a flood plane (air.water), the breach's fill
time (flood.fill, Torricelli), the flood track (flood.breach, flood.drain), the vent edges (vent.event), the look rules
(look.when, look.preset's bounce light) and the C6 block codec (c6.s12). Shares no code with the C#; the vectors it writes
under tests/s12/vectors/ are read by the C# tests (S12Tests in tests/DeepEngineers.Wire.Tests).

Usage (repository root; stdlib only):
 python -I tools/s12/s12_ref.py --selftest every rule's hand checks, the pin, the CONTRACT block, vectors current
 python -I tools/s12/s12_ref.py --vectors (re)writes tests/s12/vectors/{depth,fill,track,wire}.tsv
 python -I tools/s12/s12_ref.py --text prints the canonical text
 python -I tools/s12/s12_ref.py --pin prints hash, lines, bytes
 python -I tools/s12/s12_ref.py --tables prints the preset and the tables as written
"""
import math
import os
import random
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
VEC_DIR = os.path.join(ROOT, "tests", "s12", "vectors")
CONTRACT = os.path.join(ROOT, "docs", "CONTRACT.md")

# =====================================================================================================================
# the tables (CONTRACT §5.4 "S12 conventions"; the same numbers as S12Conventions.cs)
# =====================================================================================================================
HEADER = "ext s12 interiors 4"
PINNED_HASH = 0x9aa269decc02e106
CANON_LINES, CANON_BYTES = 109, 10496

AIR = [("HysteresisPermille", 40), ("SnapshotMaxCells", 262144), ("WaterEnterMm", 100), ("WaterExitMm", 100)]
A = dict(AIR)
LOOK = [("AmbientPermille", 450), ("SpecPermille", 600), ("TintRgb", 0xB8C8DE), ("CausticsPermille", 0), ("SunShafts", 0),
        ("BouncePermille", 150), ("BounceRgb", 0xFFE6CC), ("BounceRangeCells", 4), ("BounceMaxIntensityPermille", 800),
        ("BounceHeightMm", 300), ("RefTolPermille", 120), ("ExteriorTolPermille", 20), ("DarkerPermille", 40),
        ("WindowTolPermille", 40), ("LineTolPermille", 60), ("LineMinStepPermille", 40)]
L = dict(LOOK)
FLOOD = [("DischargePermille", 600), ("GravityMmS2", 9810), ("MinFillMs", 2000), ("MaxFillMs", 60000), ("MinDepthMm", 500),
         ("HoleCellsMax", 4), ("FullPermille", 990), ("DryPermille", 10), ("RangeM", 60), ("OxygenStartPermille", 20), ("RowTicks", 6),
         ("DrainMs", 20000), ("DryEnvMinPermille", 50), ("RepairDebounceMs", 2000), ("ReBreachQuietMs", 5000)]
F = dict(FLOOD)
VENT = [("BubblesPerS", 40), ("BurstBubbles", 60), ("SplashBubbles", 80), ("CaptureMinPermille", 30)]
V = dict(VENT)
C6 = [("Offset", 0x800), ("HeaderSize", 0x20), ("RowSize", 0x80), ("MaxRows", 8), ("MaxBoxes", 6)]
C6D = dict(C6)
C6D["Size"] = C6D["HeaderSize"] + C6D["MaxRows"] * C6D["RowSize"]
C6D["Version"] = 1
C6FLAGS = [("Active", 1), ("AirRule", 2), ("Look", 4)]
ROW_KINDS = ["none", "flood", "vent"]
KIND_NONE, KIND_FLOOD, KIND_VENT = 0, 1, 2
FLOOD_STATES = ["none", "rising", "full", "draining"]
ST_NONE, ST_RISING, ST_FULL, ST_DRAINING = 0, 1, 2, 3
FLOOD_SOURCES = ["none", "breach", "oxygen"]
SRC_NONE, SRC_BREACH, SRC_OXYGEN = 0, 1, 2
VENT_STATES = ["none", "pressurizing", "depressurizing"]
VS_NONE, VS_PRESS, VS_DEPRESS = 0, 1, 2
UP_AXES = [("PosX", 1), ("NegX", 2), ("PosY", 3), ("NegY", 4), ("PosZ", 5), ("NegZ", 6)]
HEADER_FIELDS = [("seq", "0x00", 4, "u32"), ("version", "0x04", 1, "u8"), ("flags", "0x05", 1, "u8"), ("count", "0x06", 1, "u8"),
                 ("reserved", "0x07", 1, "u8"), ("worldEpoch", "0x08", 4, "u32"), ("floodSeq", "0x0c", 4, "u32"), ("ventSeq", "0x10", 4, "u32"),
                 ("tick", "0x14", 8, "u64"), ("reserved2", "0x1c", 4, "u32")]
ROW_FIELDS = [("gridId", "0x00", 8, "i64"), ("trackId", "0x08", 4, "u32"), ("kind", "0x0c", 1, "u8"), ("state", "0x0d", 1, "u8"),
              ("source", "0x0e", 1, "u8"), ("boxCount", "0x0f", 1, "u8"), ("level", "0x10", 4, "f32"), ("ratePerS", "0x14", 4, "f32"),
              ("startSeq", "0x18", 4, "u32"), ("gridSize", "0x1c", 1, "u8"), ("upAxis", "0x1d", 1, "u8"), ("flags", "0x1e", 2, "u16"),
              ("point", "0x20", 12, "f32x3"), ("dir", "0x2c", 12, "f32x3"), ("boxes", "0x38", 72, "i16x6x6")]
FORMATS = [
    "air.volume the probe point p (grid-local, metres), its cell c = round(p / g) and f = p / g - c; the distance to the face of c in direction s e_a is g (1/2 - s f_a); when c is a cell of an airtight room the depth d = min over the faces whose neighbour cell is not of the same room of that distance, g / 2 with no such face (the air reaches the room cells' faces, where SE's airtight faces are: MyGridGasSystem.IsAirtightBlock; a window's, a door's or a slope's cell that SE counts as room is air up to its sealed face); else d = -(min over the faces whose neighbour is a cell of an airtight room of that distance), -g / 2 with none; the room cells are MyGridGasSystem.GetOxygenRoomForCubeGridPosition (IsAirtight) when the gas system is not processing, else format air.hold's",
    "air.hold per probe point and grid: in stays true while d > -h and becomes true when d > +h, h = g x HysteresisPermille / 1000; one flip per crossing of a room's boundary; while the gas system processes, d comes from the grid's room snapshot (InteriorRooms: GetRooms, each airtight MyOxygenRoom.Blocks) taken after the gas system's last OnProcessingDataComplete, else (no snapshot, or an older one) the probe point keeps its previous answer for that grid",
    "air.camera C1 InRoom (Subnautica's inside-walkable picture, SE's audio muffle) is the camera's answer in first person and for the spectator; in third person (the camera controller is the local character or its seat, not in first person) it also needs the local character's eye in a room (its swim-gate answer of this tick or the last two): a character outside the hull never gets the dry picture",
    "air.flood a room that is not airtight is water: while a breach flood rises or is full (format flood.breach) the room gives no air to the camera or to any character, whatever its drawn plane shows (SE's own environment oxygen there is the water's); only an airtight room with a flood row has air, above its plane (format air.water)",
    "air.water a probe point in an airtight room with a flood row (format flood.plane) has air only above the plane, with a latch per probe point and grid: water once its height above the room's water floor along upAxis < level x the water height - WaterEnterMm / 1000, air again once > level x the water height + WaterExitMm / 1000 (S2b's swim entry and grounded exit: SwimThresholds.SwimEnterEyeY, SwimExitGroundedEyeY)",
    "c6.s12 C6 region +0x800, own seqlock, written with every C6 while S12 is active: header (version, flags Active|AirRule|Look, count <= MaxRows, worldEpoch, floodSeq and ventSeq summed over the rows' starts, tick), then count rows of RowSize: one per flooding room (kind flood) and one per venting Air Vent (kind vent) within RangeM of the camera, nearest first; version 0 = absent",
    "flood.breach a room tracked while airtight whose watched cell (its snapshot's first cell) lies in a room that exists and is not airtight on a completed gas system (SE sets IsAirtight false and OxygenAmount 0 at once and merges the cells: MyGridGasSystem.RemoveBlock; a door that opens: IsAirtightBlock's PressurizedClosed) starts a breach flood (startSeq + 1) when its box centre is under the sea; no room at the watched cell (a block placed there: MyGridGasSystem.AddBlock nulls it) decides nothing, and the watched cell is picked again from the next snapshot's room over the track's cells; a repaired room drains once it stayed airtight RepairDebounceMs, a breach within ReBreachQuietMs of the drain's start goes on as the same flood (no new start), a later one rises again (startSeq + 1); a draining room's boxes follow its airtight room, and of two active tracks over the same cell only the one with the newer geometry draws it (one plane per cell)",
    "flood.drain the water an airtight room's oxygen gives is w = clamp((D - OxygenLevel) / D, 0, 1), D = MyOxygenRoom.EnvironmentOxygen when it is >= DryEnvMinPermille / 1000 (a room SE rebuilds on a breathable planet starts at it: MyGridGasSystem.CreateAirtightRoom), else 1; after the repair the level moves toward w no faster than a full room in DrainMs and never above the level at the repair; an airtight room under water whose vent depressurizes it floods with level = w once w >= OxygenStartPermille / 1000 (source oxygen); a row ends when level <= DryPermille / 1000 while draining",
    "flood.fill a breach rises at level(t) = min(1, t / T), T = clamp(V / Q, MinFillMs, MaxFillMs), V = the room's cells x g^3, Q = DischargePermille / 1000 x A x sqrt(2 x GravityMmS2 / 1000 x max(depth, MinDepthMm / 1000)), A = min(holes, HoleCellsMax) x g^2 (holes >= 1), depth = the room box centre's depth below the sea (m); rising becomes full at FullPermille / 1000",
    "flood.plane a flood row's boxes are the room's water cells (its cells less the cells whose block has a face on the room's boundary: windows, doors, slopes; MyCubeGrid.GetCubeBlock) decomposed into boxes (the lowest remaining cell in z, y, x order: its run along x, grown along y, then z), the MaxBoxes largest by cells, never their bounding box; Subnautica draws one water plane per box (the box's cross-section normal to upAxis at the box floor + level x the height of the boxes' union, under the mirrored grid's Entity; Unity-local = SE-local with z negated), with BaseWaterPlane's own material (its private waterRender's material, read from a loaded BaseWaterPlane) or a transparent fallback; the level eased toward the row's (ratePerS between records); one splash per startSeq increment (Subnautica's Vehicle.splashSound and SplashBubbles on the room floor under the row's point, through S7's effect player and its caps)",
    "flood.surface inside an airtight room with a flood row the room's plane is the character's water surface: S2b's swim model takes the eye's height above the plane (the plane's SN y at the eye) instead of above the sea (its entry below -0.1 m, exit above +0.8 m or +0.1 m grounded) and SE walks above it; when the row ends the room is dry and the swim gate's airtight room releases the swim latch at once (SwimExit.Room)",
    "look.preset caustics: WaterscapeVolume.causticsAmount x CausticsPermille / 1000; sun shafts: WaterSunShaftsOnCamera.enabled = SunShafts != 0; the interior chunks' sky: a copy of MarmoSkies' BaseInterior sky with DiffIntensity x AmbientPermille / 1000, SpecIntensity x SpecPermille / 1000 and its SH coefficients x TintRgb / 255 per channel; a floor bounce point light (no shadows) BounceHeightMm above the room floor under the camera, colour BounceRgb, intensity min(BounceMaxIntensityPermille / 1000, BouncePermille / 1000 x the brightest mirrored lamp within BounceRangeCells cells), range BounceRangeCells x g; each value restored when the look ends",
    'look.when the preset applies while de.s12 look is on, the camera is in an airtight room (C1 InRoom, the S12 air rule) and the camera is under the sea (SN y < 0); the exterior is untouched',
    "vent.block SE's own Air Vent carries the effect (MyAirVent is the only IMyGasBlock with CanPressurizeRoom: MyAirVent.cs:257, MyGasTank.cs:203 false; SE builds no airtight room without one: MyGridGasSystem.ShouldPressurize); no world-mod block",
    "vent.event an Air Vent within RangeM of the camera vents while SE plays its own vent effect (MyAirVent.m_isPlayingVentEffect, set by CheckForVentEffect from VentToRoom/DrainFromRoom) or while de.s12 vent drives it (dev): one row (kind vent, state Pressurizing or Depressurizing from IsDepressurizing, point = the vent's centre, dir = its forward; its room is the room at its vent dummy: MyAirVent.GetOxygenBlock), startSeq + 1 at each start; Subnautica bubbles BurstBubbles at a start and BubblesPerS while it vents (S7p's per-frame bubble budget), no sound of its own (SE's PressurizeSound/DepressurizeSound play on SE's side)",
]

def canonical_text():
    out = [HEADER]

    def c(group, name, v):
        out.append("const %s %s %d" % (group, name, v))
    for n, v in AIR:
        c("Air", n, v)
    for n, v in LOOK:
        c("Look", n, v)
    for n, v in FLOOD:
        c("Flood", n, v)
    for n, v in VENT:
        c("Vent", n, v)
    for n, v in C6:
        c("C6", n, v)
    c("C6", "Size", C6D["Size"])
    c("C6", "Version", C6D["Version"])
    for n, v in C6FLAGS:
        c("C6Flags", n, v)
    for i, n in enumerate(ROW_KINDS):
        c("RowKind", n, i)
    for i, n in enumerate(FLOOD_STATES):
        c("FloodState", n, i)
    for i, n in enumerate(FLOOD_SOURCES):
        c("FloodSource", n, i)
    for i, n in enumerate(VENT_STATES):
        c("VentState", n, i)
    for n, v in UP_AXES:
        c("UpAxis", n, v)
    for n, o, s, t in HEADER_FIELDS:
        out.append("field s12 header %s %s %d %s" % (n, o, s, t))
    for n, o, s, t in ROW_FIELDS:
        out.append("field s12 row %s %s %d %s" % (n, o, s, t))
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
# rules
# =====================================================================================================================
NEIGH = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)]

def cell_of(t, g):
    """round(t / g), halves away from zero (Wire InteriorRules.CellOf)."""
    q = t / g
    return int(math.floor(abs(q) + 0.5)) * (1 if q >= 0 else -1)

def _face(i, f):
    s = 1.0 if i % 2 == 0 else -1.0
    d = 0.5 - s * f[i // 2]
    return d if d > 0 else 0.0

def depth(p, g, room, seal=None):
    """format air.volume: (signed depth m, room key, cell, cell in room); room(x, y, z) -> key > 0 or 0; seal(x, y, z, dir) -> the
 cell's block seals its face dir (0 +x, 1 -x, 2 +y, 3 -y, 4 +z, 5 -z): the air ends at the cell's mid-plane."""
    c = tuple(cell_of(p[k], g) for k in range(3))
    f = tuple(p[k] / g - c[k] for k in range(3))
    own = room(*c)
    if own > 0:
        best = 0.5
        for i, n in enumerate(NEIGH):
            if room(c[0] + n[0], c[1] + n[1], c[2] + n[2]) == own:
                continue
            d = _face(i, f)
            if seal is not None and seal(c[0], c[1], c[2], i):
                d -= 0.5
            if d < best:
                best = d
        return best * g, own, c, True
    near, near_room = 0.5, 0
    for i, n in enumerate(NEIGH):
        q = (c[0] + n[0], c[1] + n[1], c[2] + n[2])
        k = room(*q)
        if k <= 0:
            continue
        d = _face(i, f)
        if seal is not None and seal(q[0], q[1], q[2], i ^ 1):
            d += 0.5
        if near_room == 0 or d < near:
            near, near_room = d, k
    return -near * g, near_room, c, False

def camera_in_room(camera_in, third_person, character_in):
    """format air.camera."""
    return bool(camera_in and (not third_person or character_in))

def hysteresis(g):
    return g * A["HysteresisPermille"] / 1000.0

def hold(was_in, depth_m, h):
    return depth_m > -h if was_in else depth_m > h

def wet(was_wet, height_above_floor, level, box_height):
    """format air.water: the latch (water below the plane - WaterEnterMm, air again above it + WaterExitMm)."""
    if not level > 0 or height_above_floor != height_above_floor:
        return False
    plane = level * box_height
    if was_wet:
        return height_above_floor < plane + A["WaterExitMm"] / 1000.0
    return height_above_floor < plane - A["WaterEnterMm"] / 1000.0

def decompose(cells):
    """format flood.plane: cells (x, y, z) into boxes (x0, y0, z0, x1, y1, z1): the lowest remaining cell in z, y, x order, its
 run along x, grown along y, then z (S12Boxes.Decompose)."""
    left = set(cells)
    boxes = []
    for c in sorted(left, key=lambda c: (c[2], c[1], c[0])):
        if c not in left:
            continue
        x0, y0, z0 = c
        x1 = x0
        while (x1 + 1, y0, z0) in left:
            x1 += 1
        y1 = y0
        while all((x, y1 + 1, z0) in left for x in range(x0, x1 + 1)):
            y1 += 1
        z1 = z0
        while all((x, y, z1 + 1) in left for y in range(y0, y1 + 1) for x in range(x0, x1 + 1)):
            z1 += 1
        for z in range(z0, z1 + 1):
            for y in range(y0, y1 + 1):
                for x in range(x0, x1 + 1):
                    left.discard((x, y, z))
        boxes.append((x0, y0, z0, x1, y1, z1))
    return boxes

def box_cells(b):
    return (b[3] - b[0] + 1) * (b[4] - b[1] + 1) * (b[5] - b[2] + 1)

def for_row(boxes):
    """format flood.plane: the MaxBoxes largest (by cells, then the lowest corner z, y, x), never their bounding box."""
    return sorted(boxes, key=lambda b: (-box_cells(b), b[2], b[1], b[0]))[:dict(C6)["MaxBoxes"]]

def water_cells(room_cells, has_block):
    """format flood.plane: the room's cells less the skin cells (a block with a face neighbour neither in the room nor holding a block)."""
    rc = set(room_cells)
    out = set()
    for c in rc:
        skin = False
        if has_block(c):
            for n in NEIGH:
                q = (c[0] + n[0], c[1] + n[1], c[2] + n[2])
                if q not in rc and not has_block(q):
                    skin = True
                    break
        if not skin:
            out.add(c)
    return out

def fill_ms(cells, g, holes, depth_m):
    if cells <= 0 or not g > 0:
        return float(F["MinFillMs"])
    v = cells * g * g * g
    h = max(1, min(F["HoleCellsMax"], holes))
    a = h * g * g
    d = max(F["MinDepthMm"] / 1000.0, 0.0 if depth_m != depth_m else depth_m)
    q = F["DischargePermille"] / 1000.0 * a * math.sqrt(2.0 * F["GravityMmS2"] / 1000.0 * d)
    t = v / q * 1000.0 if q > 0 else float(F["MaxFillMs"])
    return max(float(F["MinFillMs"]), min(float(F["MaxFillMs"]), t))

def level_at(elapsed, fill):
    if not fill > 0:
        return 1.0
    x = elapsed / fill
    return 0.0 if x <= 0 else 1.0 if x >= 1 else x

def water(o2, env):
    """format flood.drain: the water an airtight room's oxygen gives (D = the environment oxygen from DryEnvMinPermille up, else 1)."""
    if o2 != o2:
        return 0.0
    env = 0.0 if env != env else env
    dry = min(env, 1.0) if env * 1000.0 >= F["DryEnvMinPermille"] else 1.0
    x = (dry - o2) / dry
    return 0.0 if x <= 0 else 1.0 if x >= 1 else x

class FloodTrack:
    """formats flood.breach, flood.drain, flood.fill (S12FloodTrack)."""

    def __init__(self, seen_airtight=True):
        self.state, self.source, self.level, self.rate, self.seq, self.fill, self.ended = ST_NONE, SRC_NONE, 0.0, 0.0, 0, 0.0, False
        self.drain = float(F["DrainMs"])
        self.repair_ms, self.quiet_ms = float(F["RepairDebounceMs"]), float(F["ReBreachQuietMs"])
        self._repair_since = -1.0
        self._drain_start = float("-inf")
        self._seen = seen_airtight
        self._rise_start = self._rise_from = self._cap = 0.0
        self._prev_level = 0.0
        self._prev_ms = 0.0
        self._last_step = 0.0

    def step(self, now, processing, known, airtight, o2, env, under_sea, vent_out):
        dt = now - self._last_step if self._last_step > 0 and now > self._last_step else 0.0
        self._last_step = now
        if processing or not known:
            self._advance(now)
            self._rate(now)
            return
        full, dry, ostart = F["FullPermille"], F["DryPermille"], F["OxygenStartPermille"]
        w = water(o2, env)
        if self.state == ST_NONE:
            if airtight:
                self._seen = True
                if under_sea and vent_out and w * 1000.0 >= ostart:
                    self._start(now, SRC_OXYGEN)
                    self.level = w
            elif self._seen and under_sea:
                self._start(now, SRC_BREACH)
                self._rise_from = 0.0
                self.level = 0.0
        elif self.state in (ST_RISING, ST_FULL):
            if self.source == SRC_BREACH:
                if airtight and self._repair_since < 0:
                    self._repair_since = now
                if not airtight:
                    self._repair_since = -1.0
                if airtight and now - self._repair_since >= self.repair_ms:
                    self._repair_since = -1.0
                    self._drain_start = now
                    self._cap = self.level
                    self.state = ST_DRAINING
                    self.level = self._drain_step(w, 0.0)
                    self._end_if_dry()
                else:
                    self._advance(now)
            else:
                if not airtight:
                    self._rebreach(now)
                elif w < self.level - 1e-6:
                    self._drain_start = now
                    self._cap = self.level
                    self.state = ST_DRAINING
                    self.level = self._drain_step(w, 0.0)
                    self._end_if_dry()
                else:
                    self.level = w
                    self.state = ST_FULL if w * 1000.0 >= full else ST_RISING
        elif self.state == ST_DRAINING:
            if not airtight:
                if self.source == SRC_BREACH and self.quiet_ms > 0 and now - self._drain_start < self.quiet_ms:
                    self._rise_from = self.level        # the same flood goes on (no new start): a door cycling again
                    self.state = ST_RISING
                    self._rise_start = now
                    self._repair_since = -1.0
                else:
                    self._rebreach(now)
            elif vent_out and w > self.level + 1e-6:
                self.level = w
                self._cap = max(self._cap, self.level)
                self.source = SRC_OXYGEN
                self.state = ST_RISING
            else:
                self.level = self._drain_step(w, dt)
                self._end_if_dry()
        self._rate(now)

    def _end_if_dry(self):
        if self.state == ST_DRAINING and self.level * 1000.0 <= F["DryPermille"]:
            self.level = 0.0
            self.ended = True
            self.state = ST_NONE

    def _drain_step(self, w, dt):
        floor = self.level - dt / self.drain if self.drain > 0 else float("-inf")
        lv = min(self._cap, max(w, floor))
        return lv if lv > 0 else 0.0

    def _start(self, now, source):
        self.source, self.state = source, ST_RISING
        self.seq += 1
        self._rise_start = now
        self.ended = False

    def _rebreach(self, now):
        self._rise_from = self.level
        self.source, self.state = SRC_BREACH, ST_RISING
        self.seq += 1
        self._rise_start = now
        self._repair_since = -1.0

    def _advance(self, now):
        if self.source != SRC_BREACH or self.state not in (ST_RISING, ST_FULL):
            return
        fill = self.fill if self.fill > 0 else float(F["MinFillMs"])
        lv = level_at(now - self._rise_start + self._rise_from * fill, fill)
        self.level = lv
        self.state = ST_FULL if lv * 1000.0 >= F["FullPermille"] else ST_RISING
        if self.state == ST_FULL:
            self.level = 1.0

    def _rate(self, now):
        if self._prev_ms > 0 and now > self._prev_ms:
            self.rate = (self.level - self._prev_level) / ((now - self._prev_ms) / 1000.0)
        else:
            self.rate = 0.0
        if self.state in (ST_FULL, ST_NONE):
            self.rate = 0.0
        self._prev_ms = now
        self._prev_level = self.level

class VentEdge:
    def __init__(self):
        self.on, self.seq, self.state = False, 0, VS_NONE

    def step(self, venting, depressurizing):
        start = venting and not self.on
        self.on = venting
        self.state = (VS_DEPRESS if depressurizing else VS_PRESS) if venting else VS_NONE
        if start:
            self.seq += 1
        return start

def look_applies(look_on, in_room, cam_sn_y):
    return bool(look_on and in_room and cam_sn_y < 0)

def bounce_intensity(brightest):
    if not brightest > 0:
        return 0.0
    v = L["BouncePermille"] / 1000.0 * brightest
    return min(v, L["BounceMaxIntensityPermille"] / 1000.0)

def channel(rgb, c):
    return ((rgb >> (16 if c == 0 else 8 if c == 1 else 0)) & 0xFF) / 255.0

def up_axis_of(ux, uy, uz):
    ax, ay, az = abs(ux), abs(uy), abs(uz)
    if ax >= ay and ax >= az:
        return 1 if ux >= 0 else 2
    if ay >= az:
        return 3 if uy >= 0 else 4
    return 5 if uz >= 0 else 6

def floor_top(box, g, up):
    """(floor, top) metres along the up axis of a cell box (minx, miny, minz, maxx, maxy, maxz)."""
    ax = 0 if up in (1, 2) else 2 if up in (5, 6) else 1
    lo, hi = box[ax], box[ax + 3]
    a, c = (lo - 0.5) * g, (hi + 0.5) * g
    return (-c, -a) if up in (2, 4, 6) else (a, c)

def height(p, up):
    return {1: p[0], 2: -p[0], 3: p[1], 4: -p[1], 5: p[2], 6: -p[2]}.get(up, p[1])

# =====================================================================================================================
# the C6 block codec (format c6.s12)
# =====================================================================================================================

def encode(header, rows):
    """header: dict(version, flags, worldEpoch, floodSeq, ventSeq, tick); rows: list of dicts (row fields). -> bytes (Size)."""
    size = C6D["Size"]
    buf = bytearray(size)
    n = max(0, min(C6D["MaxRows"], len(rows)))
    buf[0x04] = header.get("version", 1)
    buf[0x05] = header.get("flags", 0)
    buf[0x06] = n
    struct.pack_into("<IIIQ", buf, 0x08, header.get("worldEpoch", 0), header.get("floodSeq", 0), header.get("ventSeq", 0), header.get("tick", 0))
    for i in range(n):
        r = rows[i]
        o = C6D["HeaderSize"] + i * C6D["RowSize"]
        boxes = list(r.get("boxes") or [])[:C6D["MaxBoxes"]]
        struct.pack_into("<qIBBBBffIBBH", buf, o, r.get("gridId", 0), r.get("trackId", 0), r.get("kind", 0), r.get("state", 0), r.get("source", 0),
                         len(boxes), r.get("level", 0.0), r.get("ratePerS", 0.0), r.get("startSeq", 0), r.get("gridSize", 0), r.get("upAxis", 0), r.get("flags", 0))
        struct.pack_into("<ffffff", buf, o + 0x20, *(list(r.get("point", (0, 0, 0))) + list(r.get("dir", (0, 0, 0)))))
        for k, b in enumerate(boxes):
            struct.pack_into("<hhhhhh", buf, o + 0x38 + k * 12, *[max(-32768, min(32767, int(v))) for v in b])
    return bytes(buf)

def decode(buf):
    h = {"version": buf[0x04], "flags": buf[0x05], "count": buf[0x06]}
    h["worldEpoch"], h["floodSeq"], h["ventSeq"], h["tick"] = struct.unpack_from("<IIIQ", buf, 0x08)
    rows = []
    if h["version"] != C6D["Version"]:
        return h, rows
    for i in range(min(h["count"], C6D["MaxRows"])):
        o = C6D["HeaderSize"] + i * C6D["RowSize"]
        g, tid, kind, st, src, bc, lv, rate, seq, gs, up, fl = struct.unpack_from("<qIBBBBffIBBH", buf, o)
        bc = min(bc, C6D["MaxBoxes"])
        p = struct.unpack_from("<ffffff", buf, o + 0x20)
        boxes = [struct.unpack_from("<hhhhhh", buf, o + 0x38 + k * 12) for k in range(bc)]
        rows.append({"gridId": g, "trackId": tid, "kind": kind, "state": st, "source": src, "level": lv, "ratePerS": rate, "startSeq": seq,
                     "gridSize": gs, "upAxis": up, "flags": fl, "point": p[:3], "dir": p[3:], "boxes": boxes})
    return h, rows

# =====================================================================================================================
# vectors (read by tests/DeepEngineers.Wire.Tests/S12Tests.cs)
# =====================================================================================================================

def _room_fn(spec):
    """spec: list of (box(6), key) -> room(x, y, z)."""
    def room(x, y, z):
        for b, k in spec:
            if b[0] <= x <= b[3] and b[1] <= y <= b[4] and b[2] <= z <= b[5]:
                return k
        return 0
    return room

def _seal_fn(seals):
    """seals: {(x, y, z): dirmask} -> seal(x, y, z, dir)."""
    def seal(x, y, z, d):
        return bool(seals.get((x, y, z), 0) >> d & 1)
    return seal

# a slope in the east wall (cell 4, 2, 2) of the 3^3 room: its full faces (Front, Bottom: CubeBlocks_Armor.sbc mount points)
# toward +x (outside) and -y, its open back toward the room, so SE counts its cell as room (MyGridGasSystem.GetRoomBlocks)
SLOPE_ROOM = [((1, 1, 1, 3, 3, 3), 1), ((4, 2, 2, 4, 2, 2), 1)]
SLOPE_SEALS = {(4, 2, 2): (1 << 0) | (1 << 3)}
ROOM_SPECS = [
    ("cube", [((1, 1, 1, 3, 3, 3), 1)], {}),                                       # the S7/S16 fixture's 3^3 interior
    ("ell", [((1, 1, 1, 3, 2, 3), 1), ((4, 1, 1, 6, 2, 1), 1)], {}),            # an L-shaped room
    ("two", [((1, 1, 1, 2, 2, 2), 1), ((4, 1, 1, 5, 2, 2), 2)], {}),            # two rooms, one wall between
    ("slope", SLOPE_ROOM, SLOPE_SEALS),                                          # the slope's cell sealed at +x and -y
]

def spec_text(spec):
    return ";".join("%d,%d,%d,%d,%d,%d:%d" % (b + (k,)) for b, k in spec)

def seal_text(seals):
    return ";".join("%d,%d,%d:%d" % (c + (m,)) for c, m in sorted(seals.items())) or "-"

def gen_depth(seed=12):
    rng = random.Random(seed)
    out = ["# g\troom spec (box:key;...)\tseals (x,y,z:dirmask;... or -)\tpx\tpy\tpz\tdepth\troom\tcx,cy,cz\tinRoom"]
    for g in (2.5, 0.5):
        for _name, spec, seals in ROOM_SPECS:
            rf, sf = _room_fn(spec), (_seal_fn(seals) if seals else None)
            pts = [tuple(rng.uniform(-0.6, 7.6) * g for _k in range(3)) for _ in range(120)]
            # face crossings: along x through the east face at y = z = 2 (cell centres), every 1 cm
            pts += [((3.5 + i * 0.01 / g) * g, 2.0 * g, 2.0 * g) for i in range(-30, 31)]
            if seals:
                # through the slope's cell: along x at y 2 - 0.24 cells (its sealed lower half), and along y at x 4 - 0.3 cells
                pts += [((3.5 + i * 0.02) * g, 1.76 * g, 2.0 * g) for i in range(0, 61)]
                pts += [(3.7 * g, (1.5 + i * 0.02) * g, 2.0 * g) for i in range(0, 51)]
            for p in pts:
                d, k, c, inr = depth(p, g, rf, sf)
                out.append("%r\t%s\t%s\t%r\t%r\t%r\t%.12f\t%d\t%d,%d,%d\t%d" % (g, spec_text(spec), seal_text(seals), p[0], p[1], p[2], d, k, c[0], c[1], c[2], 1 if inr else 0))
    return "\n".join(out) + "\n"

def gen_fill():
    out = ["# cells\tg\tholes\tdepth\tfillMs"]
    for cells in (1, 8, 27, 125, 1000):
        for g in (2.5, 0.5):
            for holes in (0, 1, 2, 9):
                for d in (0.0, 0.3, 5.0, 12.0, 100.0, 1000.0):
                    out.append("%d\t%r\t%d\t%r\t%.9f" % (cells, g, holes, d, fill_ms(cells, g, holes, d)))
    return "\n".join(out) + "\n"

# scenario steps: (t ms, processing, known, airtight, oxygen, env, underSea, ventOut); env 0.9: DE_4546B is breathable (a room SE
# rebuilds starts at its environment oxygen, MyGridGasSystem.CreateAirtightRoom), env 0: a planet or space without oxygen
E = 0.9
SCENARIOS = {
    "breach-repair": (8000.0, [(0, 0, 1, 1, 1.0, E, 1, 0), (100, 1, 1, 1, 1.0, E, 1, 0), (200, 0, 1, 0, 0.0, E, 1, 0)] +
                      [(200 + 500 * i, 0, 1, 0, 0.0, E, 1, 0) for i in range(1, 20)] +
                      [(10500, 1, 1, 0, 0.0, E, 1, 0), (10600, 0, 1, 1, E, E, 1, 0)] +
                      [(10600 + 1000 * i, 0, 1, 1, E, E, 1, 0) for i in range(1, 23)]),
    "repair-no-air": (8000.0, [(0, 0, 1, 1, 1.0, 0.0, 1, 0), (100, 0, 1, 0, 0.0, 0.0, 1, 0), (9000, 0, 1, 0, 0.0, 0.0, 1, 0), (9100, 0, 1, 1, 0.0, 0.0, 1, 0)] +
                     [(9100 + 1000 * i, 0, 1, 1, min(1.0, 0.1 * i), 0.0, 1, 0) for i in range(1, 14)]),
    "repair-midway": (10000.0, [(0, 0, 1, 1, 1.0, E, 1, 0), (100, 0, 1, 0, 0.0, E, 1, 0), (2100, 0, 1, 0, 0.0, E, 1, 0), (4100, 0, 1, 0, 0.0, E, 1, 0),
                                (4200, 0, 1, 1, E, E, 1, 0), (5200, 0, 1, 1, E, E, 1, 0), (7200, 0, 1, 1, E, E, 1, 0), (12200, 0, 1, 1, E, E, 1, 0)]),
    "above-sea": (5000.0, [(0, 0, 1, 1, 1.0, E, 0, 0), (100, 0, 1, 0, 0.0, E, 0, 0), (1000, 0, 1, 0, 0.0, E, 0, 0)]),
    "null-cell": (5000.0, [(0, 0, 1, 1, E, E, 1, 0), (100, 0, 0, 0, 0.0, 0.0, 1, 0), (1000, 0, 0, 0, 0.0, 0.0, 1, 0), (1100, 0, 1, 1, E, E, 1, 0)]),
    "rebreach": (4000.0, [(0, 0, 1, 1, 1.0, E, 1, 0), (100, 0, 1, 0, 0.0, E, 1, 0), (2100, 0, 1, 0, 0.0, E, 1, 0), (2200, 0, 1, 1, E, E, 1, 0),
                          (3200, 0, 1, 1, E, E, 1, 0), (3300, 0, 1, 0, 0.0, E, 1, 0), (4300, 0, 1, 0, 0.0, E, 1, 0), (6300, 0, 1, 0, 0.0, E, 1, 0)]),
    "vent-out": (5000.0, [(0, 0, 1, 1, E, E, 1, 0), (100, 0, 1, 1, 0.89, E, 1, 1), (200, 0, 1, 1, 0.85, E, 1, 1), (400, 0, 1, 1, 0.5, E, 1, 1), (600, 0, 1, 1, 0.2, E, 1, 1),
                          (800, 0, 1, 1, 0.0, E, 1, 1), (900, 0, 1, 1, 0.0, E, 1, 0), (1900, 0, 1, 1, 0.4, E, 1, 0), (3900, 0, 1, 1, E, E, 1, 0)] +
                         [(3900 + 1000 * i, 0, 1, 1, E, E, 1, 0) for i in range(1, 20)]),
    # a door cycling (SE flips the room's airtightness every 500 ms), then shut, then one more cycle soon after the drain
    "door-cycle": (6000.0, [(0, 0, 1, 1, E, E, 1, 0), (100, 0, 1, 0, 0.0, E, 1, 0)] +
                   [(100 + 500 * i, 0, 1, i % 2, (E if i % 2 else 0.0), E, 1, 0) for i in range(1, 10)] +
                   [(5100 + 500 * i, 0, 1, 1, E, E, 1, 0) for i in range(0, 8)] +
                   [(9100, 0, 1, 0, 0.0, E, 1, 0), (9600, 0, 1, 1, E, E, 1, 0)] + [(9600 + 500 * i, 0, 1, 1, E, E, 1, 0) for i in range(1, 50)]),
    "processing-hold": (3000.0, [(0, 0, 1, 1, 1.0, E, 1, 0), (100, 1, 1, 0, 0.0, E, 1, 0), (200, 0, 1, 0, 0.0, E, 1, 0), (900, 1, 1, 0, 0.0, E, 1, 0),
                                 (1700, 1, 1, 1, 0.0, E, 1, 0), (3500, 0, 1, 0, 0.0, E, 1, 0)]),
}

# scenario -> (RepairDebounceMs, ReBreachQuietMs); the others run with 0, 0 (round 2's flip, their checks unchanged)
DEBOUNCED = {"door-cycle": (float(F["RepairDebounceMs"]), float(F["ReBreachQuietMs"]))}

def run_scenario(fill, steps, drain=None, debounce=(0.0, 0.0)):
    t = FloodTrack(True)
    t.fill = fill
    t.repair_ms, t.quiet_ms = debounce
    if drain is not None:
        t.drain = drain
    out = []
    for s in steps:
        t.step(float(s[0]), bool(s[1]), bool(s[2]), bool(s[3]), float(s[4]), float(s[5]), bool(s[6]), bool(s[7]))
        out.append((t.state, t.source, t.level, t.seq, t.ended))
    return out

def gen_track():
    out = ["# scenario\tfillMs\tt\tprocessing\tknown\tairtight\toxygen\tenv\tunderSea\tventOut\tstate\tsource\tlevel\tstartSeq\tended\trepairMs\tquietMs"]
    for name in sorted(SCENARIOS):
        fill, steps = SCENARIOS[name]
        deb = DEBOUNCED.get(name, (0.0, 0.0))
        for s, r in zip(steps, run_scenario(fill, steps, debounce=deb)):
            out.append("%s\t%r\t%r\t%d\t%d\t%d\t%r\t%r\t%d\t%d\t%d\t%d\t%.9f\t%d\t%d\t%r\t%r" % (name, fill, float(s[0]), s[1], s[2], s[3], float(s[4]), float(s[5]), s[6], s[7],
                                                                                                r[0], r[1], r[2], r[3], 1 if r[4] else 0, deb[0], deb[1]))
    return "\n".join(out) + "\n"

def sample_block():
    h = {"version": 1, "flags": 7, "worldEpoch": 3, "floodSeq": 2, "ventSeq": 5, "tick": 123456789012}
    rows = [{"gridId": 142823253881049946, "trackId": 1, "kind": KIND_FLOOD, "state": ST_RISING, "source": SRC_BREACH, "level": 0.375, "ratePerS": 0.125,
             "startSeq": 1, "gridSize": 2, "upAxis": 3, "flags": 0, "point": (5.0, 2.5, -7.5), "dir": (0.0, 0.0, -1.0),
             "boxes": [(1, 1, 1, 3, 3, 3)]},
            {"gridId": -77, "trackId": 9, "kind": KIND_VENT, "state": VS_PRESS, "source": 0, "level": 0.0, "ratePerS": 0.0, "startSeq": 4,
             "gridSize": 1, "upAxis": 6, "flags": 1, "point": (0.25, -0.5, 1.0), "dir": (1.0, 0.0, 0.0),
             "boxes": [(-1, 0, 0, 2, 2, 2), (3, 0, 0, 3, 1, 4), (32767, -32768, 0, 1, 1, 1), (0, 0, 0, 0, 0, 0), (5, 5, 5, 6, 6, 6), (7, 7, 7, 7, 7, 7)]}]
    return h, rows

def gen_wire():
    h, rows = sample_block()
    b = encode(h, rows)
    return "# the sample block (s12_ref.sample_block) encoded, hex\n" + b.hex() + "\n"

VECTORS = {"depth.tsv": gen_depth, "fill.tsv": gen_fill, "track.tsv": gen_track, "wire.tsv": gen_wire}

def write_vectors():
    os.makedirs(VEC_DIR, exist_ok=True)
    for name, fn in VECTORS.items():
        with open(os.path.join(VEC_DIR, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(fn())
    print("S12 vectors written to %s (%s)" % (VEC_DIR, ", ".join(sorted(VECTORS))))

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

def tables_text():
    out = ["S12 interior look preset (look.preset; applied in an airtight room under water):"]
    out.append("  interior sky: diffuse x %.2f, specular x %.2f, SH tint #%06X (r %.3f g %.3f b %.3f)" % (
        L["AmbientPermille"] / 1000.0, L["SpecPermille"] / 1000.0, L["TintRgb"], channel(L["TintRgb"], 0), channel(L["TintRgb"], 1), channel(L["TintRgb"], 2)))
    out.append("  caustics x %.2f, sun shafts %s" % (L["CausticsPermille"] / 1000.0, "on" if L["SunShafts"] else "off"))
    out.append("  floor bounce light: %.2f x the brightest lamp within %d cells (max %.2f), colour #%06X, %d mm above the floor" % (
        L["BouncePermille"] / 1000.0, L["BounceRangeCells"], L["BounceMaxIntensityPermille"] / 1000.0, L["BounceRgb"], L["BounceHeightMm"]))
    out.append("  rows: interior vs reference <= %.3f mean abs RGB; exterior on vs off <= %.3f; on darker than off by >= %.3f luma" % (
        L["RefTolPermille"] / 1000.0, L["ExteriorTolPermille"] / 1000.0, L["DarkerPermille"] / 1000.0))
    out.append("air rule: hysteresis %.3f m large / %.3f m small; a flood plane wets %d mm under it, dries %d mm over it" % (hysteresis(2.5), hysteresis(0.5), A["WaterEnterMm"], A["WaterExitMm"]))
    out.append("breach fill time (Torricelli, Cd %.2f, one hole):" % (F["DischargePermille"] / 1000.0))
    for cells, g, d in ((27, 2.5, 10.0), (27, 2.5, 100.0), (125, 2.5, 10.0), (27, 0.5, 10.0), (8, 2.5, 0.0)):
        out.append("  %4d cells of %.1f m at %6.1f m: %6.2f s" % (cells, g, d, fill_ms(cells, g, 1, d) / 1000.0))
    out.append("drain: level = min(level at repair, 1 - SE's room oxygen); vent: %d bubbles at a start, %d/s while venting; splash burst %d" % (
        V["BurstBubbles"], V["BubblesPerS"], V["SplashBubbles"]))
    return "\n".join(out)

# =====================================================================================================================
# self-test
# =====================================================================================================================

def selftest():
    res = []

    def check(ok, what):
        res.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S12 twin self-test (tools/s12/s12_ref.py --selftest)")
    h, n, b = text_hash()
    check(all(ord(ch) < 128 for ch in canonical_text()), "the canonical text is ASCII (%d lines, %d bytes)" % (n, b))
    check(h == PINNED_HASH and n == CANON_LINES and b == CANON_BYTES, "pin 0x%016x %d lines %d bytes (pinned 0x%016x %d %d)" % (h, n, b, PINNED_HASH, CANON_LINES, CANON_BYTES))
    blk = contract_block()
    check(blk is not None and blk == canonical_text(), "CONTRACT block equals the canonical text" + (" (block absent)" if blk is None else ""))
    check(C6D["Offset"] + C6D["Size"] <= 0x1000 and C6D["Offset"] >= 0x740, "the C6 block (+0x%x, %d B) lies past S15's events block and inside C6's 4 KiB" % (C6D["Offset"], C6D["Size"]))
    # air.volume, by hand: the 3^3 cube of cells 1..3, g 2.5
    rf = _room_fn(ROOM_SPECS[0][1])
    g = 2.5
    d, k, c, inr = depth((5.0, 5.0, 5.0), g, rf)
    check(abs(d - 1.25) < 1e-12 and k == 1 and inr, "the room's centre cell: depth g/2 (no boundary face), room 1")
    d, k, c, inr = depth((3 * g + 1.0, 2 * g, 2 * g), g, rf)
    check(abs(d - 0.25) < 1e-12 and inr, "1.0 m east of cell 3's centre: 0.25 m from the east face")
    d, k, c, inr = depth((3 * g + 1.5, 2 * g, 2 * g), g, rf)
    check(abs(d + 0.25) < 1e-12 and not inr and k == 1, "0.25 m past the east face (in the wall cell): -0.25 m")
    d, k, c, inr = depth((5 * g + 0.5, 2 * g, 2 * g), g, rf)
    check(d == -1.25 and k == 0, "outside the wall, no room cell next door: -g/2 (2.5 m outside: far)")
    h_ = hysteresis(g)
    check(abs(h_ - 0.1) < 1e-12 and abs(hysteresis(0.5) - 0.02) < 1e-12, "hysteresis 0.10 m large, 0.02 m small")
    # air.hold: a crossing with jitter flips once
    was, flips = True, 0
    for i in range(0, 400):
        x = 3 * g + 0.5 + i * 0.01 + 0.04 * math.sin(i * 1.7)     # walking out with +-4 cm jitter
        now = hold(was, depth((x, 2 * g, 2 * g), g, rf)[0], h_)
        flips += now != was
        was = now
    check(flips == 1 and not was, "walking out through the east wall with +-4 cm jitter: exactly one flip (%d)" % flips)
    was, flips = False, 0
    for i in range(0, 400):
        x = 3 * g + 1.25 + 0.03 * math.sin(i * 2.3)               # loitering on the face, +-3 cm
        now = hold(was, depth((x, 2 * g, 2 * g), g, rf)[0], h_)
        flips += now != was
        was = now
    check(flips == 0, "loitering on the face outside with +-3 cm jitter: no flip")
    # the S7 rule (the raw cell) flips on every jitter crossing the cell boundary (S12.M1's signature)
    s7_flips, prev = 0, None
    for i in range(0, 400):
        x = 3 * g + 1.25 + 0.03 * math.sin(i * 2.3)
        now = rf(cell_of(x, g), 2, 2) > 0
        s7_flips += prev is not None and now != prev
        prev = now
    check(s7_flips > 10, "the raw-cell rule (S7, S12.M1) flips on the same jitter (%d flips)" % s7_flips)
    check(not wet(False, 1.80, 0.25, 7.5) and wet(False, 1.70, 0.25, 7.5) and wet(True, 1.95, 0.25, 7.5) and not wet(True, 2.0, 0.25, 7.5)
          and not wet(True, 0.5, 0.0, 7.5), "air.water: the latch (0.1 m under to get wet, 0.1 m over to dry; no plane: dry)")
    cells = [(x, y, 0) for x in range(3) for y in range(2)] + [(0, 0, 1)]
    d = decompose(cells)
    check(d == [(0, 0, 0, 2, 1, 0), (0, 0, 1, 0, 0, 1)] and sum(box_cells(b) for b in d) == 7, "flood.plane: the decomposition (S12Boxes.Decompose's hand case)")
    many = [(i, 0, 0, i, 1, 1) for i in range(9)]
    check(for_row(many) == many[:6] and for_row(many + [(20, 0, 0, 22, 1, 1)])[0] == (20, 0, 0, 22, 1, 1), "flood.plane: the 6 largest, never the bounding box")
    interior = [(x, y, z) for x in range(1, 4) for y in range(1, 4) for z in range(-3, 0)]

    def blk(c):
        x, y, z = c
        shell = 0 <= x <= 4 and 0 <= y <= 4 and -4 <= z <= 0 and not (1 <= x <= 3 and 1 <= y <= 3 and -3 <= z <= -1)
        return shell or c == (2, 1, -2)
    wc = water_cells(interior + [(4, 1, -2)], blk)
    check(len(wc) == 27 and (4, 1, -2) not in wc and (2, 1, -2) in wc, "flood.plane: the window's cell is skin, the seat's is water (S12Water's hand case)")
    t = fill_ms(27, 2.5, 1, 10.0)
    q = 0.6 * 6.25 * math.sqrt(2 * 9.81 * 10.0)
    check(abs(t - 27 * 15.625 / q * 1000.0) < 1e-6 and 2000 <= t <= 60000, "a 3^3 large room through one hole at 10 m fills in %.2f s (Torricelli)" % (t / 1000.0))
    check(fill_ms(1, 0.5, 1, 1000) == 2000.0 and fill_ms(1000, 2.5, 1, 0.0) == 60000.0, "the fill time is clamped to [2, 60] s")
    # the track scenarios
    r = run_scenario(*SCENARIOS["breach-repair"])
    st = SCENARIOS["breach-repair"][1]
    states = [x[0] for x in r]
    check(r[2][0] == ST_RISING and r[2][3] == 1 and r[1][0] == ST_NONE, "breach: rising from the first completed non-airtight tick, startSeq 1")
    check(ST_FULL in states and r[states.index(ST_FULL)][2] == 1.0, "the breach fills (full at level 1)")
    lv = [x[2] for x in r]
    check(all(lv[k] <= lv[k + 1] + 1e-12 for k in range(1, states.index(ST_FULL))), "the breach level never falls while rising")
    rep = [k for k, s_ in enumerate(st) if s_[0] == 10600][0]
    check(r[rep][0] == ST_DRAINING and abs(r[rep][2] - 1.0) < 1e-9, "the repair on a breathable planet (the room rebuilt at its environment oxygen 0.9): no drop at the weld")
    k10 = [k for k, s_ in enumerate(st) if s_[0] == 20600][0]
    check(abs(r[k10][2] - 0.5) < 1e-9, "it drains at DrainMs (half after 10 s: %.3f)" % r[k10][2])
    check(r[-1][4] and r[-1][3] == 1, "it ends dry, still one start (no puddle left)")
    check(abs(water(0.9, 0.9)) < 1e-12 and abs(water(0.45, 0.9) - 0.5) < 1e-12 and abs(water(0.3, 0.0) - 0.7) < 1e-12 and water(0.95, 0.9) == 0.0,
          "format flood.drain's water: dry at the environment oxygen, 1 - O2 without one")
    r = run_scenario(*SCENARIOS["repair-no-air"])
    lv = [x[2] for x in r]
    check(r[3][0] == ST_DRAINING and abs(lv[3] - 1.0) < 1e-9 and all(lv[k + 1] <= lv[k] + 1e-12 and lv[k] - lv[k + 1] <= 0.05 + 1e-9 for k in range(3, len(lv) - 1)),
          "without environment oxygen the repaired room stays full and drains with its oxygen, never faster than DrainMs")
    r = run_scenario(*SCENARIOS["repair-midway"])
    check(r[4][0] == ST_DRAINING and abs(r[4][2] - r[3][2]) < 1e-9 and abs(r[5][2] - (r[4][2] - 0.05)) < 1e-9 and r[-1][4],
          "repaired mid-fill: drains from its level at DrainMs and ends")
    r = run_scenario(*SCENARIOS["repair-midway"], drain=0.0)
    check(r[4][2] == 0.0 and r[4][4], "with no drain time (S12.M3) the level drops at the weld (and the track ends)")
    r = run_scenario(*SCENARIOS["above-sea"])
    check(all(x[0] == ST_NONE and x[3] == 0 for x in r), "a breach above the sea floods nothing")
    r = run_scenario(*SCENARIOS["null-cell"])
    check(all(x[0] == ST_NONE and x[3] == 0 for x in r), "no room at the watched cell (a block placed there) floods nothing")
    r = run_scenario(*SCENARIOS["rebreach"])
    check(r[5][3] == 2 and r[5][0] == ST_RISING and r[-1][0] == ST_FULL, "a breach while draining rises again (startSeq 2) and fills")
    r = run_scenario(*SCENARIOS["vent-out"])
    check(r[1][3] == 0 and r[2][3] == 1 and r[2][1] == SRC_OXYGEN and abs(r[4][2] - (0.9 - 0.2) / 0.9) < 1e-9 and r[-1][4],
          "a vent depressurizing under water floods by the oxygen (start at w >= 0.02) and drains back")
    fill, st = SCENARIOS["door-cycle"]
    r = run_scenario(fill, st, debounce=DEBOUNCED["door-cycle"])
    drains = sum(1 for k in range(1, len(r)) if r[k][0] == ST_DRAINING and r[k - 1][0] != ST_DRAINING)
    t_drain = [s_[0] for s_, x in zip(st, r) if x[0] == ST_DRAINING]
    check(max(x[3] for x in r) == 1 and r[-1][4] and t_drain and t_drain[0] >= 4600 + 2000,
          "a door cycling: one start (startSeq 1), the drain only after 2 s airtight (at %s ms), ended" % (t_drain[0] if t_drain else "-"))
    check(drains == 2 and max(x[3] for x in r) == 1, "a breach 2 s after the drain started goes on as the same flood (no new start), drains again once")
    r0 = run_scenario(fill, st, debounce=(0.0, 0.0))
    check(max(x[3] for x in r0) > 1, "round 2's flip (S12.M12) restarts the flood %d times on the same door" % max(x[3] for x in r0))
    r = run_scenario(*SCENARIOS["processing-hold"])
    check(r[1][0] == ST_NONE and r[2][0] == ST_RISING and r[3][0] == ST_RISING and r[4][0] in (ST_RISING, ST_FULL) and r[3][2] > r[2][2],
          "while the gas system processes nothing new is decided; a rising breach keeps rising on time")
    # air.volume with a sealing block: the slope's lower half and outer half are outside the air
    rs, ss = _room_fn(SLOPE_ROOM), _seal_fn(SLOPE_SEALS)
    d_in = depth((3.75 * g, 2.3 * g, 2 * g), g, rs, ss)[0]
    d_solid = depth((4.3 * g, 1.8 * g, 2 * g), g, rs, ss)[0]
    d_raw = depth((4.3 * g, 1.8 * g, 2 * g), g, rs)[0]
    check(d_in > 0 and d_solid < -hysteresis(g) and d_raw > hysteresis(g), "inside the slope's cell: air in its open upper inner part (%.2f m), none in its solid wedge (%.2f m; %.2f m without the seal rule)" % (d_in, d_solid, d_raw))
    check(camera_in_room(True, False, False) and not camera_in_room(True, True, False) and camera_in_room(True, True, True) and not camera_in_room(False, True, True),
          "format air.camera: in third person the character's eye must be in a room too")
    v = VentEdge()
    starts = [v.step(on, dep) for on, dep in ((0, 0), (1, 0), (1, 0), (0, 0), (1, 1), (1, 1), (0, 0))]
    check(starts == [False, True, False, False, True, False, False] and v.seq == 2, "vent.event: one start per off -> on")
    check(look_applies(True, True, -5) and not look_applies(True, True, 5) and not look_applies(False, True, -5) and not look_applies(True, False, -5), "look.when")
    check(abs(bounce_intensity(2.0) - 0.3) < 1e-12 and bounce_intensity(100) == 0.8 and bounce_intensity(0) == 0.0, "the bounce light: 0.15 x the lamp, max 0.8")
    check(up_axis_of(0.1, 0.9, 0.2) == 3 and up_axis_of(0, 0, -1) == 6 and up_axis_of(-1, 0.5, 0) == 2, "the up axis code")
    check(floor_top((1, 1, 1, 3, 3, 3), 2.5, 3) == (1.25, 8.75) and floor_top((1, 1, 1, 3, 3, 3), 2.5, 4) == (-8.75, -1.25), "the box floor/top along the up axis")
    hb, rows = sample_block()
    blob = encode(hb, rows)
    h2, rows2 = decode(blob)
    ok = len(blob) == C6D["Size"] and h2["count"] == 2 and rows2[0]["gridId"] == rows[0]["gridId"] and rows2[1]["boxes"][2] == (32767, -32768, 0, 1, 1, 1)
    ok = ok and abs(rows2[0]["level"] - 0.375) < 1e-7 and rows2[1]["upAxis"] == 6 and h2["tick"] == 123456789012
    check(ok, "the C6 codec round-trips the sample block (%d B)" % len(blob))
    for name, fn in VECTORS.items():
        p = os.path.join(VEC_DIR, name)
        cur = open(p, encoding="utf-8").read() if os.path.exists(p) else None
        check(cur == fn(), "vectors %s current" % name)
    ok = all(res)
    print("S12 TWIN SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(res), len(res)))
    return 0 if ok else 1

def main(argv):
    if not argv or argv[0] == "--selftest":
        return selftest()
    if argv[0] == "--vectors":
        write_vectors()
        return 0
    if argv[0] == "--text":
        sys.stdout.write(canonical_text())
        return 0
    if argv[0] == "--pin":
        h, n, b = text_hash()
        print("0x%016x %d lines %d bytes" % (h, n, b))
        return 0
    if argv[0] == "--tables":
        print(tables_text())
        return 0
    print(__doc__)
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
