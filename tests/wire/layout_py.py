"""Independent Python layout of the DeepEngineers wire, layout 1.0.

Written BY HAND from docs/CONTRACT.md §5.2 (region map), §5.4 (record layouts) and §6 (protocol identity, layout-hash
grammar). It is never generated from the C# code: the C# `Layout.cs` and this file are two readings of the same
contract, and `compare` checks that both canonical texts are byte-identical (ROADMAP S0.N1, CONTRACT O4).

Where CONTRACT is silent, this file follows the conventions written down for layout 1.0 (reported with the S0 core
work and to be added to CONTRACT §5.2/§6):
- region ids: channel × 16 + part (C0 0x00, ring control 0x01, C1 0x10 … C11a-d 0xB0-0xB3, C12 0xC0);
- region kinds: 1 Header, 2 RingControl, 3 Seqlock, 4 SeqlockTable, 5 SeqlockPair, 6 RecordRing, 7 ByteRing;
- region recordSize: the region's main record (byte rings: 8, the message header); version 1 everywhere;
- record names: the C# struct names (SectionHeader, RegionEntry, RingControlEntry, SeFrame, SeBone,...);
- pads are fields named `pad`: typed u32/u16 where CONTRACT says "u32 pad"/"u16 pad", u8[n] for a pad range;
- C2 `ω` is spelled `omega`; C10 paths are `pathLength` (u16) + `path` (u8[n], the rest of the 256-byte record);
- text: ids, kinds, record sizes and field sizes in decimal; offsets and region sizes as lower-case hex without
 leading zeros; every line ends with "\\n"; fields sorted ordinally by record name, then by offset;
- six blocks in this order: `region` lines (id order); `ring <index> <region id>
 0x<head> 0x<tail>` (index order); `table <region id> <name> 0x<offset> <count> <stride> <record>` (region id, then
 offset); `message <region id> <type> <record>` (region id, then type); `const <group> <name> <value>` (group, then
 value, then name); `field` lines.

Stdlib only (Python 3.10). Run with `python -I`:
 python -I tests/wire/layout_py.py selftest # internal consistency + hand-typed CONTRACT spot checks
 python -I tests/wire/layout_py.py text [--out F] # the canonical layout-hash text
 python -I tests/wire/layout_py.py hash # FNV-1a 64 of the text
 python -I tests/wire/layout_py.py compare [FIXTURE] # diff against the C# fixture (default: the Wire.Tests one)
 python -I tests/wire/layout_py.py table # JSON offset table (for cross-language checks)
"""
from __future__ import annotations

import collections
import difflib
import json
import os
import struct
import sys

# ----------------------------------------------------------------------------------------------------------------
# Protocol identity (CONTRACT §6)
# ----------------------------------------------------------------------------------------------------------------

LAYOUT_MAJOR = 1
LAYOUT_MINOR = 0
MAGIC = 0x50454544                 # the four bytes "DEEP" read as a little-endian u32
MAGIC_BYTES = b"DEEP"
BYTE_ORDER_MARK = 0x01020304
HEADER_SIZE = 0x1000
SECTION_SIZE = 0x05000000          # 80 MiB
WIRE_VERSION = 1                   # WIRE_VERSION in tools/worldmap/wire_ref.py
SITE_VERSION = 1                   # SITE_VERSION in tools/worldmap/site_v1.py
DAMAGE_SCALE_VERSION = 1           # CONTRACT §4.1
SITE_HASH_V1 = 0x53AB8E77D68103AB  # CONTRACT §3 (FNV-1a 64 of site_v1.record canonical JSON)
RS_EXPECTED = 60000.00390625       # CONTRACT §3 water.RsExpected (float32)

SECTION_NAME = "Local\\DeepEngineers_v1"
MUTEX_SN_SUFFIX = "_sn"
MUTEX_SE_SUFFIX = "_se"
LAUNCHER_SUFFIX = "_launcher"
MUTEX_SN = SECTION_NAME + MUTEX_SN_SUFFIX               # held by the SN plugin while in bridge mode
MUTEX_SE = SECTION_NAME + MUTEX_SE_SUFFIX               # held by the SE plugin while linked
LAUNCHER_OBJECT = SECTION_NAME + LAUNCHER_SUFFIX        # bridge token held by the launcher until ReadyIdle
BRIDGE_ENV = "DEEPENGINEERS_BRIDGE"                    # bridge token: DEEPENGINEERS_BRIDGE=1

SDDL_TEMPLATE = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{sid})S:(ML;;NW;;;ME)"

FNV64_OFFSET = 0xCBF29CE484222325
FNV64_PRIME = 0x100000001B3
FNV32_OFFSET = 0x811C9DC5
FNV32_PRIME = 0x01000193

# Value pinned in CONTRACT §6 for layout 1.0 (typed here by hand from the CONTRACT line).
PINNED_LAYOUT_HASH_1_0 = 0x4F3A35F6FC1EAF31
CANONICAL_LINES_1_0 = 506

REGION_TABLE_OFFSET = 0x100
REGION_ENTRY_SIZE = 32
RING_CONTROL_OFFSET = 0x1000
RING_STRIDE = 0x80
RING_TAIL_DELTA = 0x40

def sddl_for(sid: str) -> str:
    if not sid:
        raise ValueError("a user SID string is required")
    return SDDL_TEMPLATE.format(sid=sid)

def mutex_names(section_name: str = SECTION_NAME):
    """(sn mutex, se mutex, launcher object) for a section name (tests use their own section name)."""
    return (section_name + MUTEX_SN_SUFFIX, section_name + MUTEX_SE_SUFFIX, section_name + LAUNCHER_SUFFIX)

# ----------------------------------------------------------------------------------------------------------------
# Lifecycle numbers (CONTRACT §5.1, §6, §7; ARCHITECTURE §5.14)
# ----------------------------------------------------------------------------------------------------------------

STALL_PLAY_S = 3.0          # heartbeat older than 3 s in play -> PeerStalled
STALL_LOADING_S = 120.0     # while either side reports a loading phase
SEQLOCK_MAX_TRIES = 16
C8_OVERFLOW_CAPACITY = 4096
SE_OPEN_RETRY_S = 1.0       # SE retries the open once per second...
SE_OPEN_RETRY_SLOW_S = 5.0  #... every 5 s after the first minute
LINK_WAIT_LOG_S = 10.0      # at most one DE.Link WAIT per 10 s (S0.N7)
READY_WAIT_S = 180.0        # launcher ready wait
SN_QUIT_AFTER_SE_EXIT_S = 10.0
QUITTING_ACK_WAIT_S = 0.100
RESEND_STALL_S = 10.0       # a stall >= 10 s always triggers a full resend
C3_MAX_CHUNK = 4 * 1024 * 1024

# ----------------------------------------------------------------------------------------------------------------
# Constants of §5.4
# ----------------------------------------------------------------------------------------------------------------

SN_PHASES = {0: "None", 1: "Booting", 2: "Menu", 3: "LoadingSave", 4: "ReadyIdle", 5: "WarmingUp", 6: "Active",
             7: "Quitting"}
SN_PHASE = {v: k for k, v in SN_PHASES.items()}
SN_LOADING_PHASES = frozenset((1, 2, 3))
SE_PHASES = {0: "None", 1: "Menu", 2: "LoadingWorld", 3: "InWorld", 4: "Saving", 5: "Exiting"}
SE_PHASE = {v: k for k, v in SE_PHASES.items()}
SE_LOADING_PHASES = frozenset((2, 4))

SN_FLAGS = {"bridgeMode": 0, "ghostingDisabled": 1, "stylesApplied": 2}
SE_FLAGS = {"gateOpen": 0, "terrainUp": 1, "displayRefused": 2, "failSafeActive": 3}
DEV_CONTROL = {"harness": 0, "seRelay": 1}
SE_FRAME_FLAGS = {"inWorld": 0, "paused": 1, "firstPerson": 2, "cameraInAirtightRoom": 3, "teleported": 4,
                  "characterPresent": 5, "characterDead": 6, "characterSeated": 7, "characterSwimming": 8}
SE_STATE_FLAGS = {"seForeground": 0, "sePaused": 1, "seExiting": 2, "seeThrough": 3, "cameraInAirtightRoom": 4,
                  "marginsOn": 5, "zOrderOk": 6, "seMixActive": 7, "guiScreenOpen": 8}
OVERLAYS = {"Paused": 0, "Unfocused": 1, "PeerStalled": 2, "PeerExited": 3}
SN_STATUS_FLAGS = {"worldLoaded": 0, "streamingSettled": 1, "combatEnabled": 2, "wireSelfTestPass": 3,
                   "areaBuilt": 4, "rectOk": 5, "uiHidden": 6, "frozen": 7, "runInBackgroundForced": 8,
                   "timeSyncApplied": 9, "damageSelfTestPass": 10}
CAPABILITIES = {"underwaterPicture": 0, "creaturesAndDamage": 1, "statics": 2, "timeOfDay": 3, "capture": 4}
HOST_KIND_SUBNAUTICA1 = 1
DAMAGE_UNIT_SN_HP = 1

PAD_TYPE = 0
C3_TYPES = {"ModelBegin": 1, "ModelChunk": 2, "ModelEnd": 3, "MaterialDef": 4, "SkinnedBind": 5}
C4_TYPES = {"GridAdd": 1, "EntityRemove": 2, "PartAdd": 3, "PartRemove": 4, "PartRecolor": 5, "SkinSet": 6,
            "RoomSet": 7, "CharacterAdd": 8, "ClearAll": 9}
C11_TYPES = {"Json": 1}
C12_TYPES = {"BatchStatics": 1, "BatchClear": 2}

# Sub-tables inside regions (§5.4)
C1_BONES_OFFSET = 0x100
C1_MAX_BONES = 160
C1_BONES_END = 0x1F00
C2_ENTITIES_OFFSET = 0x40
C2_MAX_ENTITIES = 1024
C2_SUBPARTS_OFFSET = 0x18040
C2_MAX_SUBPARTS = 2048
C2_LIGHTS_OFFSET = 0x38040
C2_MAX_LIGHTS = 64
C8_CAPACITY_RECORDS = 1024
C9_RECORDS_OFFSET = 0x40
C9_MAX_CREATURES = 256
C10_REQUEST_OFFSET = 0x000
C10_RESULT_OFFSET = 0x800

# ----------------------------------------------------------------------------------------------------------------
# Wire constants pinned by the `const` lines of the layout-hash text. Typed by hand from the §5.4
# sentences; group and member names follow the Wire `Records.cs` class and member names (layout 1.0 convention, like
# the record names). Flags listed in CONTRACT without bit numbers take bits 0, 1, 2... in the listed order (D4).
# ----------------------------------------------------------------------------------------------------------------

CONSTS = {
    # C0
    "SnPhase": {"None": 0, "Booting": 1, "Menu": 2, "LoadingSave": 3, "ReadyIdle": 4, "WarmingUp": 5, "Active": 6,
                "Quitting": 7},
    "SePhase": {"None": 0, "Menu": 1, "LoadingWorld": 2, "InWorld": 3, "Saving": 4, "Exiting": 5},
    "SnFlags": {"BridgeMode": 1, "GhostingDisabled": 2, "StylesApplied": 4},
    "SeFlags": {"GateOpen": 1, "TerrainUp": 2, "DisplayRefused": 4, "FailSafeActive": 8},
    "DevControl": {"HarnessAttached": 1, "SeChatRelay": 2},
    # C1
    "SeFrameFlags": {"InWorld": 1, "Paused": 2, "FirstPerson": 4, "CameraInAirtightRoom": 8, "Teleported": 16,
                     "CharacterPresent": 32, "CharacterDead": 64, "CharacterSeated": 128, "CharacterSwimming": 256},
    "CameraMode": {"Other": 0, "FirstPerson": 1, "ThirdPerson": 2, "Cockpit": 3, "Spectator": 4},
    # C2
    "SeEntityKind": {"RootGrid": 1, "Character": 2, "FloatingObject": 3},
    "SeEntityFlags": {"Static": 1, "InCameraGroup": 2},
    "SeLightFlags": {"Spot": 1, "Shadows": 2, "On": 4},
    # C5
    "SeStateFlags": {"SeForeground": 1, "SePaused": 2, "SeExiting": 4, "SeeThrough": 8, "CameraInAirtightRoom": 16,
                     "MarginsOn": 32, "ZOrderOk": 64, "SeMixActive": 128, "GuiScreenOpen": 256},
    "ViewState": {"Space": 0, "Approach": 1, "Underwater": 2, "Surfacing": 3},
    "ApproachMode": {"None": 0, "Track": 1, "Prime": 2},
    "DisplayMode": {"F": 0, "APrime": 1, "C": 2},
    "Overlays": {"Paused": 1, "Unfocused": 2, "PeerStalled": 4, "PeerExited": 8},
    "Zone": {"Core": 0, "TileVoid": 1, "OuterVoid": 2, "Far": 3, "OffSite": 4},
    "PerfMode": {"Idle": 0, "Track": 1, "Full": 2},
    # C6
    "SeTimeFlags": {"TimeSyncValid": 1, "SkyOnly": 2},
    # C7
    "SnStatusFlags": {"WorldLoaded": 1, "StreamingSettled": 2, "CombatEnabled": 4, "WireSelfTestPass": 8,
                      "AreaBuilt": 16, "RectOk": 32, "UiHidden": 64, "Frozen": 128, "RunInBackgroundForced": 256,
                      "TimeSyncApplied": 512, "DamageSelfTestPass": 1024},
    "HostKind": {"Subnautica1": 1},
    "Capabilities": {"UnderwaterPicture": 1, "CreaturesAndDamage": 2, "Statics": 4, "TimeOfDay": 8, "Capture": 16},
    # C8
    "DamageUnit": {"SnHp": 1},
    "DamageTargetKind": {"GridSection": 1, "Character": 2},
    "DamageFlags": {"MouthContact": 1, "Radius": 2, "FallbackHitPoint": 4, "FrameSubgrid": 8},
    # C9
    "SnCreatureFlags": {"Hunting": 1, "TargetIsMirror": 2},
    # C10
    "CaptureKind": {"NextFramePng": 1, "AtSeTick": 2},
    "CaptureStatus": {"Pending": 0, "Ok": 1, "Failed": 2},
    # C3 (type 0 is the pad record on every byte ring; chunks <= 4 MiB)
    "C3Type": {"Pad": 0, "ModelBegin": 1, "ModelChunk": 2, "ModelEnd": 3, "MaterialDef": 4, "SkinnedBind": 5,
               "MaxChunkBytes": 4 * 1024 * 1024},
    "ModelFlags": {"Skinned": 1, "HasTangents": 2},
    "MaterialFlags": {"GlassCw": 1, "GlassCcw": 2, "AlphaMasked": 4},
    # C4
    "C4Type": {"Pad": 0, "GridAdd": 1, "EntityRemove": 2, "PartAdd": 3, "PartRemove": 4, "PartRecolor": 5,
               "SkinSet": 6, "RoomSet": 7, "CharacterAdd": 8, "ClearAll": 9},
    "GridClass": {"LargeGrid": 1, "SmallGrid": 2},
    "SkinSectionKind": {"Grid": 1, "Character": 2},
    # C11, C12
    "C11Type": {"Pad": 0, "Json": 1},
    "C12Type": {"Pad": 0, "BatchStatics": 1, "BatchClear": 2},
    "StaticKind": {"Box": 1, "Capsule": 2, "Sphere": 3, "MeshObb": 4},
}

# ----------------------------------------------------------------------------------------------------------------
# Region map (§5.2)
# ----------------------------------------------------------------------------------------------------------------

KIND_HEADER, KIND_RING_CONTROL, KIND_SEQLOCK, KIND_SEQLOCK_TABLE, KIND_SEQLOCK_PAIR, KIND_RECORD_RING, KIND_BYTE_RING = \
    1, 2, 3, 4, 5, 6, 7
KIND_NAMES = {1: "Header", 2: "RingControl", 3: "Seqlock", 4: "SeqlockTable", 5: "SeqlockPair", 6: "RecordRing",
              7: "ByteRing"}

Region = collections.namedtuple("Region", "key id kind offset size record_size version title")

# key, id, kind, offset, size, recordSize, version, title
REGIONS = [
    Region("C0",   0x00, KIND_HEADER,        0x0000000, 0x0001000, 256, 1, "Header"),
    Region("RC",   0x01, KIND_RING_CONTROL,  0x0001000, 0x0001000, 128, 1, "Ring control"),
    Region("C5",   0x50, KIND_SEQLOCK,       0x0002000, 0x0001000, 256, 1, "SeState"),
    Region("C6",   0x60, KIND_SEQLOCK,       0x0003000, 0x0001000, 64,  1, "SeTime"),
    Region("C7",   0x70, KIND_SEQLOCK,       0x0004000, 0x0001000, 256, 1, "SnStatus"),
    Region("C10",  0xA0, KIND_SEQLOCK_PAIR,  0x0005000, 0x0001000, 256, 1, "Capture"),
    Region("C1",   0x10, KIND_SEQLOCK,       0x0010000, 0x0002000, 256, 1, "SeFrame"),
    Region("C2",   0x20, KIND_SEQLOCK_TABLE, 0x0020000, 0x0080000, 96,  1, "SeEntities"),
    Region("C9",   0x90, KIND_SEQLOCK_TABLE, 0x00A0000, 0x0004000, 48,  1, "SnCreatures"),
    Region("C8",   0x80, KIND_RECORD_RING,   0x00B0000, 0x0020000, 128, 1, "SnDamage"),
    Region("C11a", 0xB0, KIND_BYTE_RING,     0x0100000, 0x0040000, 8,   1, "CtlToSe"),
    Region("C11b", 0xB1, KIND_BYTE_RING,     0x0140000, 0x0040000, 8,   1, "CtlFromSe"),
    Region("C11c", 0xB2, KIND_BYTE_RING,     0x0180000, 0x0040000, 8,   1, "CtlToSn"),
    Region("C11d", 0xB3, KIND_BYTE_RING,     0x01C0000, 0x0040000, 8,   1, "CtlFromSn"),
    Region("C12",  0xC0, KIND_BYTE_RING,     0x0200000, 0x0400000, 8,   1, "SnStatics"),
    Region("C4",   0x40, KIND_BYTE_RING,     0x0600000, 0x0800000, 8,   1, "SeParts"),
    Region("C3",   0x30, KIND_BYTE_RING,     0x1000000, 0x4000000, 8,   1, "SeModels"),
]
REGIONS_BY_ID = sorted(REGIONS, key=lambda r: r.id)
REGION = {r.key: r for r in REGIONS}

# Reserved ranges of §5.2 (offset, size): never written by anyone.
RESERVED = [(0x0006000, 0x000A000), (0x0012000, 0x000E000), (0x00A4000, 0x000C000), (0x00D0000, 0x0030000),
            (0x0E00000, 0x0200000)]

Ring = collections.namedtuple("Ring", "index key producer consumer alt_producer")
# Ring r = 0..7 in the order C3, C4, C8, C12, C11a, C11b, C11c, C11d (§5.2). Producers/consumers from §5.1 and
# ARCHITECTURE §3.2; C11c has two exclusive producers (harness with devControl bit 0, SE relay with bit 1).
RINGS = [
    Ring(0, "C3", "se", "sn", None),
    Ring(1, "C4", "se", "sn", None),
    Ring(2, "C8", "sn", "se", None),
    Ring(3, "C12", "sn", "se", None),
    Ring(4, "C11a", "harness", "se", None),
    Ring(5, "C11b", "se", "harness", None),
    Ring(6, "C11c", "harness", "sn", "se"),
    Ring(7, "C11d", "sn", "harness", None),
]
RING = {r.key: r for r in RINGS}
SN_CONSUMES = ("C3", "C4", "C11c")       # creator rule 3
SE_CONSUMES = ("C8", "C12", "C11a")
HARNESS_CONSUMES = ("C11b", "C11d")

def ring_head_offset(key: str) -> int:
    return RING_CONTROL_OFFSET + RING[key].index * RING_STRIDE

def ring_tail_offset(key: str) -> int:
    return ring_head_offset(key) + RING_TAIL_DELTA

def ring_capacity(key: str) -> int:
    """Byte rings: bytes; C8: records."""
    g = REGION[key]
    if g.kind == KIND_RECORD_RING:
        return g.size // g.record_size
    return g.size

# ----------------------------------------------------------------------------------------------------------------
# Records (§5.4). Each entry: (name, offset, type). Types: u8 u16 u32 u64 i16 i32 i64 f32 f64, arrays as "f32[3]".
# ----------------------------------------------------------------------------------------------------------------

_SCALAR = {"u8": (1, "B"), "u16": (2, "H"), "u32": (4, "I"), "u64": (8, "Q"), "i16": (2, "h"), "i32": (4, "i"),
           "i64": (8, "q"), "f32": (4, "f"), "f64": (8, "d")}

Field = collections.namedtuple("Field", "name offset size type base count")

def _parse_type(t: str):
    if "[" in t:
        base, n = t[:-1].split("[")
        return base, int(n)
    return t, 1

class Record:
    """One fixed record. pack/unpack use field names; arrays are tuples; u8[n] arrays are bytes."""

    def __init__(self, name: str, size: int, fields):
        self.name = name
        self.size = size
        self.fields = []
        fmt = ["<"]
        for (fname, off, t) in fields:
            base, n = _parse_type(t)
            bsize, code = _SCALAR[base]
            self.fields.append(Field(fname, off, bsize * n, t, base, n))
            if base == "u8" and n > 1:
                fmt.append("%ds" % n)
            else:
                fmt.append(("%d" % n if n > 1 else "") + code)
        self.struct = struct.Struct("".join(fmt))
        self._by_name = {}
        for f in self.fields:
            if f.name != "pad":
                self._by_name[f.name] = f

    def field(self, name: str) -> Field:
        return self._by_name[name]

    def offset(self, name: str) -> int:
        return self._by_name[name].offset

    def pack(self, **values) -> bytes:
        args = []
        for f in self.fields:
            v = values.get(f.name) if f.name != "pad" else None
            if f.base == "u8" and f.count > 1:
                b = bytes(v) if v is not None else b""
                args.append(b[:f.count].ljust(f.count, b"\0"))
            elif f.count > 1:
                seq = list(v) if v is not None else []
                seq = seq + [0] * (f.count - len(seq))
                args.extend(seq[:f.count])
            else:
                args.append(v if v is not None else 0)
        out = self.struct.pack(*args)
        assert len(out) == self.size
        return out

    def unpack(self, buf, offset: int = 0) -> dict:
        vals = self.struct.unpack_from(buf, offset)
        out = {}
        i = 0
        for f in self.fields:
            if f.base == "u8" and f.count > 1:
                v = vals[i]
                i += 1
            elif f.count > 1:
                v = tuple(vals[i:i + f.count])
                i += f.count
            else:
                v = vals[i]
                i += 1
            if f.name != "pad":
                out[f.name] = v
        return out

_RECORD_SPECS = {
    # ---- C0 header (0x100) and its region table ----
    "SectionHeader": (0x100, [
        ("magic", 0x000, "u32"), ("layoutMajor", 0x004, "u16"), ("layoutMinor", 0x006, "u16"),
        ("byteOrderMark", 0x008, "u32"), ("headerSize", 0x00C, "u32"), ("sectionSize", 0x010, "u64"),
        ("layoutHash", 0x018, "u64"), ("wireVersion", 0x020, "u32"), ("siteVersion", 0x024, "u32"),
        ("damageScaleVersion", 0x028, "u32"), ("regionCount", 0x02C, "u32"), ("qpcFrequency", 0x030, "u64"),
        ("devControl", 0x038, "u32"), ("pad", 0x03C, "u32"),
        # SN block
        ("snPid", 0x040, "u32"), ("snGeneration", 0x044, "u32"), ("snHeartbeatMs", 0x048, "u64"),
        ("snPhase", 0x050, "u32"), ("snFlags", 0x054, "u32"), ("snHwnd", 0x058, "u64"), ("pad", 0x060, "u8[32]"),
        # SE block
        ("sePid", 0x080, "u32"), ("seGeneration", 0x084, "u32"), ("seHeartbeatMs", 0x088, "u64"),
        ("sePhase", 0x090, "u32"), ("seFlags", 0x094, "u32"), ("seHwnd", 0x098, "u64"),
        ("seLoadStep", 0x0A0, "u32"), ("pad", 0x0A4, "u8[92]"),
    ]),
    "RegionEntry": (32, [
        ("id", 0x00, "u32"), ("kind", 0x04, "u32"), ("offset", 0x08, "u64"), ("size", 0x10, "u64"),
        ("recordSize", 0x18, "u32"), ("version", 0x1C, "u32"),
    ]),
    # ---- ring control page: head and tail on separate 64-byte lines ----
    "RingControlEntry": (0x80, [
        ("head", 0x00, "u64"), ("pad", 0x08, "u8[56]"), ("tail", 0x40, "u64"), ("pad", 0x48, "u8[56]"),
    ]),
    # ---- C1 SeFrame ----
    "SeFrame": (0x100, [
        ("seq", 0x00, "u32"), ("flags", 0x04, "u32"), ("tick", 0x08, "u64"), ("qpcCapture", 0x10, "i64"),
        ("simTimeSec", 0x18, "f64"), ("simSpeed", 0x20, "f32"), ("worldEpoch", 0x24, "u32"),
        ("camPos", 0x28, "f32[3]"), ("camRot", 0x34, "f32[4]"), ("vFovDeg", 0x44, "f32"), ("near", 0x48, "f32"),
        ("aspect", 0x4C, "f32"), ("cameraMode", 0x50, "u32"), ("pad", 0x54, "u32"), ("characterId", 0x58, "u64"),
        ("charO", 0x60, "f32[3]"), ("charQ", 0x6C, "f32[4]"), ("charS", 0x7C, "f32"), ("boneCount", 0x80, "u32"),
        ("bonesVersion", 0x84, "u32"), ("pelvisSn", 0x88, "f32[3]"), ("pad", 0x94, "u8[108]"),
    ]),
    "SeBone": (48, [("rows", 0x00, "f32[12]")]),
    # ---- C2 SeEntities ----
    "SeEntitiesHeader": (0x40, [
        ("seq", 0x00, "u32"), ("entityCount", 0x04, "u32"), ("tick", 0x08, "u64"), ("worldEpoch", 0x10, "u32"),
        ("subpartCount", 0x14, "u32"), ("lightCount", 0x18, "u32"), ("pad", 0x1C, "u8[36]"),
    ]),
    "SeEntity": (96, [
        ("entityId", 0x00, "u64"), ("kind", 0x08, "u32"), ("flags", 0x0C, "u32"), ("groupId", 0x10, "u64"),
        ("o", 0x18, "f32[3]"), ("q", 0x24, "f32[4]"), ("s", 0x34, "f32"), ("k", 0x38, "f32"),
        ("v", 0x3C, "f32[3]"), ("omega", 0x48, "f32[3]"), ("partsVersion", 0x54, "u32"),
        ("skinVersion", 0x58, "u32"), ("pad", 0x5C, "u32"),
    ]),
    "SeSubpart": (64, [("ownerId", 0x00, "u64"), ("partKey", 0x08, "u64"), ("local", 0x10, "f32[12]")]),
    "SeLight": (64, [
        ("ownerId", 0x00, "u64"), ("pos", 0x08, "f32[3]"), ("dir", 0x14, "f32[3]"), ("colour", 0x20, "f32[3]"),
        ("range", 0x2C, "f32"), ("intensity", 0x30, "f32"), ("coneDeg", 0x34, "f32"), ("flags", 0x38, "u32"),
        ("pad", 0x3C, "u32"),
    ]),
    # ---- byte-ring framing ----
    "MessageHeader": (8, [("type", 0x00, "u32"), ("payloadBytes", 0x04, "u32")]),
    # ---- C3 messages ----
    "ModelBegin": (64, [
        ("modelKey", 0x00, "u64"), ("lod", 0x08, "u32"), ("vertexCount", 0x0C, "u32"), ("indexCount", 0x10, "u32"),
        ("submeshCount", 0x14, "u32"), ("flags", 0x18, "u32"), ("bounds", 0x1C, "f32[6]"),
        ("patternScale", 0x34, "f32"), ("chunkCount", 0x38, "u32"), ("totalBytes", 0x3C, "u32"),
    ]),
    "ModelChunkHeader": (16, [("modelKey", 0x00, "u64"), ("chunkIndex", 0x08, "u32"), ("bytes", 0x0C, "u32")]),
    "ModelEnd": (16, [("modelKey", 0x00, "u64"), ("payloadHash", 0x08, "u64")]),
    "MaterialDefHeader": (16, [("materialKey", 0x00, "u64"), ("drawTechnique", 0x08, "u32"), ("flags", 0x0C, "u32")]),
    "SkinnedBindHeader": (16, [("modelKey", 0x00, "u64"), ("boneCount", 0x08, "u32"), ("pad", 0x0C, "u32")]),
    "ModelVertex": (56, [
        ("pos", 0x00, "f32[3]"), ("normal", 0x0C, "f32[3]"), ("tangent", 0x18, "f32[3]"),
        ("binormal", 0x24, "f32[3]"), ("uv", 0x30, "f32[2]"),
    ]),
    "ModelVertexSkinned": (76, [
        ("pos", 0x00, "f32[3]"), ("normal", 0x0C, "f32[3]"), ("tangent", 0x18, "f32[3]"),
        ("binormal", 0x24, "f32[3]"), ("uv", 0x30, "f32[2]"), ("boneIndex", 0x38, "u8[4]"),
        ("weight", 0x3C, "f32[4]"),
    ]),
    "ModelSubmesh": (24, [
        ("materialKey", 0x00, "u64"), ("triStart", 0x08, "u32"), ("triCount", 0x0C, "u32"),
        ("drawTechnique", 0x10, "u32"), ("pad", 0x14, "u32"),
    ]),
    # ---- C4 messages ----
    "GridAdd": (32, [
        ("entityId", 0x00, "u64"), ("targetClass", 0x08, "u32"), ("flags", 0x0C, "u32"),
        ("parentEntityId", 0x10, "u64"), ("worldEpoch", 0x18, "u32"), ("pad", 0x1C, "u32"),
    ]),
    "EntityRemove": (8, [("entityId", 0x00, "u64")]),
    "PartAdd": (100, [
        ("entityId", 0x00, "u64"), ("partKey", 0x08, "u64"), ("modelKey", 0x10, "u64"), ("local", 0x18, "f32[12]"),
        ("colorMaskHSV", 0x48, "f32[3]"), ("uvOffset", 0x54, "f32[2]"), ("skinId", 0x5C, "u32"),
        ("flags", 0x60, "u32"),
    ]),
    "PartRemove": (16, [("entityId", 0x00, "u64"), ("partKey", 0x08, "u64")]),
    "PartRecolor": (32, [("entityId", 0x00, "u64"), ("partKey", 0x08, "u64"), ("hsv", 0x10, "f32[3]"),
                         ("pad", 0x1C, "u32")]),
    "SkinSetHeader": (16, [("entityId", 0x00, "u64"), ("skinVersion", 0x08, "u32"), ("sectionCount", 0x0C, "u32")]),
    "SkinSection": (24, [("sectionId", 0x00, "u32"), ("centre", 0x04, "f32[3]"), ("boxCount", 0x10, "u32"),
                         ("kind", 0x14, "u32")]),
    "SkinBox": (64, [("centre", 0x00, "f32[3]"), ("half", 0x0C, "f32[3]"), ("axes", 0x18, "f32[9]"),
                     ("flags", 0x3C, "u32")]),
    "RoomSetHeader": (16, [("entityId", 0x00, "u64"), ("count", 0x08, "u32"), ("pad", 0x0C, "u32")]),
    "RoomBox": (24, [("min", 0x00, "i32[3]"), ("max", 0x0C, "i32[3]")]),
    "CharacterAdd": (24, [("entityId", 0x00, "u64"), ("modelKey", 0x08, "u64"), ("flags", 0x10, "u32"),
                          ("boneCount", 0x14, "u32")]),
    "ClearAll": (8, [("worldEpoch", 0x00, "u32"), ("linkEpoch", 0x04, "u32")]),
    # ---- C5 SeState ----
    "SeState": (0x100, [
        ("seq", 0x00, "u32"), ("flags", 0x04, "u32"), ("tick", 0x08, "u64"), ("viewState", 0x10, "u32"),
        ("approachMode", 0x14, "u32"), ("displayMode", 0x18, "u32"), ("overlays", 0x1C, "u32"),
        ("zone", 0x20, "u32"), ("worldEpoch", 0x24, "u32"), ("perfMode", 0x28, "u32"), ("targetFps", 0x2C, "u32"),
        ("snBusTarget", 0x30, "f32"), ("crossfadeMs", 0x34, "f32"), ("helloEpoch", 0x38, "u32"),
        ("linkEpoch", 0x3C, "u32"), ("Rs", 0x40, "f64"), ("siteHash", 0x48, "u64"), ("siteVersion", 0x50, "u32"),
        ("pad", 0x54, "u32"), ("seRect", 0x58, "i32[4]"), ("camSnY", 0x68, "f32"), ("camVelSn", 0x6C, "f32[3]"),
        ("crossingTick", 0x78, "u64"), ("pad", 0x80, "u8[128]"),
    ]),
    # ---- C6 SeTime ----
    "SeTime": (64, [
        ("seq", 0x00, "u32"), ("jumpSeq", 0x04, "u32"), ("tick", 0x08, "u64"), ("hourAngleRad", 0x10, "f64"),
        ("sunElevationRad", 0x18, "f32"), ("sunDirSn", 0x1C, "f32[3]"), ("dayLengthSec", 0x28, "f32"),
        ("simSpeed", 0x2C, "f32"), ("paused", 0x30, "u32"), ("flags", 0x34, "u32"),
        ("sunriseHourAngleRad", 0x38, "f32"), ("pad", 0x3C, "u32"),
    ]),
    # ---- C7 SnStatus ----
    "SnStatus": (0x100, [
        ("seq", 0x00, "u32"), ("flags", 0x04, "u32"), ("snPhase", 0x08, "u32"), ("perfModeApplied", 0x0C, "u32"),
        ("snFrame", 0x10, "u64"), ("presentedSeTick", 0x18, "u64"), ("areaBuiltSeTick", 0x20, "u64"),
        ("areaBuiltPos", 0x28, "f32[3]"), ("helloAckEpoch", 0x34, "u32"), ("linkAckEpoch", 0x38, "u32"),
        ("mirroredEntityCount", 0x3C, "u32"), ("pendingParts", 0x40, "u32"), ("damageDropped", 0x44, "u32"),
        ("c3BytesDrained", 0x48, "u64"), ("damageOverflowDepth", 0x50, "u32"), ("frameMsP50", 0x54, "f32"),
        ("frameMsP99", 0x58, "f32"), ("hostKind", 0x5C, "u32"), ("snHwnd", 0x60, "u64"), ("snRect", 0x68, "i32[4]"),
        ("capabilities", 0x78, "u32"), ("vSyncCountApplied", 0x7C, "u32"), ("targetFrameRateApplied", 0x80, "i32"),
        ("fixedDeltaTime", 0x84, "f32"), ("resamplesLastFrame", 0x88, "u32"), ("pad", 0x8C, "u8[116]"),
    ]),
    # ---- C8 damage record ----
    "DamageRecord": (128, [
        ("dedupSeq", 0x00, "u32"), ("snGeneration", 0x04, "u32"), ("unit", 0x08, "u32"), ("amount", 0x0C, "f32"),
        ("seScaleExpected", 0x10, "f32"), ("snDamageType", 0x14, "u16"), ("flags", 0x16, "u16"),
        ("targetKind", 0x18, "u32"), ("sectionId", 0x1C, "u32"), ("targetEntityId", 0x20, "u64"),
        ("rootEntityId", 0x28, "u64"), ("snapshotTick", 0x30, "u64"), ("localHit", 0x38, "f32[3]"),
        ("snWorldHit", 0x44, "f32[3]"), ("attackDirSn", 0x50, "f32[3]"), ("dealerTechType", 0x5C, "i32"),
        ("dealerInstanceId", 0x60, "u32"), ("worldEpoch", 0x64, "u32"), ("snTimeSec", 0x68, "f64"),
        ("pad", 0x70, "u8[16]"),
    ]),
    # ---- C9 SnCreatures ----
    "SnCreaturesHeader": (64, [("seq", 0x00, "u32"), ("count", 0x04, "u32"), ("snFrame", 0x08, "u64"),
                               ("pad", 0x10, "u8[48]")]),
    "SnCreature": (48, [
        ("instanceId", 0x00, "u32"), ("techType", 0x04, "i32"), ("posSn", 0x08, "f32[3]"), ("velSn", 0x14, "f32[3]"),
        ("aggression", 0x20, "f32"), ("flags", 0x24, "u32"), ("targetEntityId", 0x28, "u64"),
    ]),
    # ---- C10 capture (request at +0x000, result at +0x800) ----
    "CaptureRequest": (0x100, [
        ("seq", 0x00, "u32"), ("reqId", 0x04, "u32"), ("kind", 0x08, "u32"), ("pad", 0x0C, "u32"),
        ("atSeTick", 0x10, "u64"), ("pathLength", 0x18, "u16"), ("path", 0x1A, "u8[230]"),
    ]),
    "CaptureResult": (0x100, [
        ("seq", 0x00, "u32"), ("reqId", 0x04, "u32"), ("status", 0x08, "u32"), ("alpha", 0x0C, "f32"),
        ("snFrame", 0x10, "u64"), ("seTickUsed", 0x18, "u64"), ("pathLength", 0x20, "u16"),
        ("path", 0x22, "u8[222]"),
    ]),
    # ---- C12 messages ----
    "BatchStaticsHeader": (16, [
        ("bx", 0x00, "i16"), ("by", 0x02, "i16"), ("bz", 0x04, "i16"), ("pad", 0x06, "u16"), ("epoch", 0x08, "u32"),
        ("count", 0x0C, "u32"),
    ]),
    "StaticPrimitive": (48, [
        ("kind", 0x00, "u8"), ("flags", 0x01, "u8"), ("pad", 0x02, "u16"), ("centreSn", 0x04, "f32[3]"),
        ("qSn", 0x10, "f32[4]"), ("size", 0x20, "f32[3]"), ("classIdHash", 0x2C, "u32"),
    ]),
    "BatchClear": (16, [
        ("bx", 0x00, "i16"), ("by", 0x02, "i16"), ("bz", 0x04, "i16"), ("pad", 0x06, "u16"), ("epoch", 0x08, "u32"),
        ("pad", 0x0C, "u32"),
    ]),
}

RECORDS = {name: Record(name, size, fields) for name, (size, fields) in _RECORD_SPECS.items()}

HDR = RECORDS["SectionHeader"]
REGION_ENTRY = RECORDS["RegionEntry"]
SE_STATE = RECORDS["SeState"]
SN_STATUS = RECORDS["SnStatus"]
SE_FRAME = RECORDS["SeFrame"]
SE_TIME = RECORDS["SeTime"]
DAMAGE = RECORDS["DamageRecord"]
CAPTURE_REQUEST = RECORDS["CaptureRequest"]
CAPTURE_RESULT = RECORDS["CaptureResult"]
SN_CREATURES_HEADER = RECORDS["SnCreaturesHeader"]
SE_ENTITIES_HEADER = RECORDS["SeEntitiesHeader"]

def header_offset(name: str) -> int:
    return HDR.offset(name)

# Fixed records and arrays inside regions (§5.2, §5.4): (region key, name, offset from the region start, count, record).
# The stride is the record size. Every non-ring region has its main record at offset 0.
Table = collections.namedtuple("Table", "region name offset count record")
TABLES = [
    Table("C0", "header", 0x000, 1, "SectionHeader"),
    Table("C0", "regionTable", REGION_TABLE_OFFSET, len(REGIONS), "RegionEntry"),
    Table("RC", "entries", 0x000, 8, "RingControlEntry"),
    Table("C1", "frame", 0x000, 1, "SeFrame"),
    Table("C1", "bones", C1_BONES_OFFSET, C1_MAX_BONES, "SeBone"),
    Table("C2", "header", 0x000, 1, "SeEntitiesHeader"),
    Table("C2", "entities", C2_ENTITIES_OFFSET, C2_MAX_ENTITIES, "SeEntity"),
    Table("C2", "subparts", C2_SUBPARTS_OFFSET, C2_MAX_SUBPARTS, "SeSubpart"),
    Table("C2", "lights", C2_LIGHTS_OFFSET, C2_MAX_LIGHTS, "SeLight"),
    Table("C5", "state", 0x000, 1, "SeState"),
    Table("C6", "time", 0x000, 1, "SeTime"),
    Table("C7", "status", 0x000, 1, "SnStatus"),
    Table("C8", "records", 0x000, C8_CAPACITY_RECORDS, "DamageRecord"),
    Table("C9", "header", 0x000, 1, "SnCreaturesHeader"),
    Table("C9", "records", C9_RECORDS_OFFSET, C9_MAX_CREATURES, "SnCreature"),
    Table("C10", "request", C10_REQUEST_OFFSET, 1, "CaptureRequest"),
    Table("C10", "result", C10_RESULT_OFFSET, 1, "CaptureResult"),
]

# Byte-ring message types and the record their payload starts with (§5.4 C3, C4, C12). C11 carries JSON (no record).
MESSAGES = [
    ("C3", C3_TYPES["ModelBegin"], "ModelBegin"), ("C3", C3_TYPES["ModelChunk"], "ModelChunkHeader"),
    ("C3", C3_TYPES["ModelEnd"], "ModelEnd"), ("C3", C3_TYPES["MaterialDef"], "MaterialDefHeader"),
    ("C3", C3_TYPES["SkinnedBind"], "SkinnedBindHeader"),
    ("C4", C4_TYPES["GridAdd"], "GridAdd"), ("C4", C4_TYPES["EntityRemove"], "EntityRemove"),
    ("C4", C4_TYPES["PartAdd"], "PartAdd"), ("C4", C4_TYPES["PartRemove"], "PartRemove"),
    ("C4", C4_TYPES["PartRecolor"], "PartRecolor"), ("C4", C4_TYPES["SkinSet"], "SkinSetHeader"),
    ("C4", C4_TYPES["RoomSet"], "RoomSetHeader"), ("C4", C4_TYPES["CharacterAdd"], "CharacterAdd"),
    ("C4", C4_TYPES["ClearAll"], "ClearAll"),
    ("C12", C12_TYPES["BatchStatics"], "BatchStaticsHeader"), ("C12", C12_TYPES["BatchClear"], "BatchClear"),
]

# ----------------------------------------------------------------------------------------------------------------
# FNV-1a and the canonical layout-hash text (§6)
# ----------------------------------------------------------------------------------------------------------------

def fnv1a64(data, h: int = FNV64_OFFSET) -> int:
    for b in data:
        h ^= b
        h = (h * FNV64_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h

def fnv1a32(data, h: int = FNV32_OFFSET) -> int:
    for b in data:
        h ^= b
        h = (h * FNV32_PRIME) & 0xFFFFFFFF
    return h

def canonical_text() -> str:
    """CONTRACT §6 layout-hash text: region, ring, table, message, const and field lines (six blocks, in that order)."""
    lines = []
    for r in REGIONS_BY_ID:
        lines.append("region %d %d 0x%x 0x%x %d %d" % (r.id, r.kind, r.offset, r.size, r.record_size, r.version))
    for ring in sorted(RINGS, key=lambda x: x.index):
        lines.append("ring %d %d 0x%x 0x%x" % (ring.index, REGION[ring.key].id, ring_head_offset(ring.key),
                                              ring_tail_offset(ring.key)))
    for t in sorted(TABLES, key=lambda x: (REGION[x.region].id, x.offset)):
        lines.append("table %d %s 0x%x %d %d %s" % (REGION[t.region].id, t.name, t.offset, t.count,
                                                    RECORDS[t.record].size, t.record))
    for key, mtype, rec in sorted(MESSAGES, key=lambda x: (REGION[x[0]].id, x[1])):
        lines.append("message %d %d %s" % (REGION[key].id, mtype, rec))
    consts = [(g, v, n) for g, members in CONSTS.items() for n, v in members.items()]
    for g, v, n in sorted(consts):        # group (code points), then value, then name
        lines.append("const %s %s %d" % (g, n, v))
    rows = []
    for name, rec in RECORDS.items():
        for f in rec.fields:
            rows.append((name, f.offset, "field %s %s 0x%x %d %s" % (name, f.name, f.offset, f.size, f.type)))
    rows.sort(key=lambda t: (t[0], t[1]))   # ordinal on str (code points), then offset
    lines.extend(t[2] for t in rows)
    return "".join(line + "\n" for line in lines)

def layout_hash() -> int:
    return fnv1a64(canonical_text().encode("ascii"))

def hex64(v: int) -> str:
    return "0x%016x" % v

def region_table_bytes() -> bytes:
    """The C0 region table (0x100, regionCount × 32 B) in region-id order."""
    out = bytearray()
    for r in REGIONS_BY_ID:
        out += REGION_ENTRY.pack(id=r.id, kind=r.kind, offset=r.offset, size=r.size, recordSize=r.record_size,
                                 version=r.version)
    return bytes(out)

def framed_size(payload_bytes: int) -> int:
    return 8 + ((payload_bytes + 7) & ~7)

# ----------------------------------------------------------------------------------------------------------------
# Self-test: internal consistency and spot checks typed from CONTRACT sentences
# ----------------------------------------------------------------------------------------------------------------

def selftest(verbose: bool = True) -> list:
    errors = []

    def check(cond, msg):
        if not cond:
            errors.append(msg)

    # region map covers [0, SECTION_SIZE) exactly with the reserved ranges, no overlap
    spans = sorted([(r.offset, r.size, r.key) for r in REGIONS] + [(o, s, "reserved") for o, s in RESERVED])
    pos = 0
    for off, size, key in spans:
        check(off == pos, "region map gap/overlap at 0x%x (%s starts at 0x%x)" % (pos, key, off))
        pos = off + size
    check(pos == SECTION_SIZE, "region map ends at 0x%x, expected 0x%x" % (pos, SECTION_SIZE))
    check(len(set(r.id for r in REGIONS)) == len(REGIONS), "duplicate region ids")
    check(len(REGIONS) * REGION_ENTRY_SIZE + REGION_TABLE_OFFSET <= HEADER_SIZE, "region table exceeds the header")
    # rings: power-of-two capacities, control words on separate 64-byte lines inside the control page
    for ring in RINGS:
        g = REGION[ring.key]
        check(g.size & (g.size - 1) == 0, "%s size is not a power of two" % ring.key)
        h, t = ring_head_offset(ring.key), ring_tail_offset(ring.key)
        check(h // 64 != t // 64, "%s head/tail share a cache line" % ring.key)
        check(REGION["RC"].offset <= h and t + 8 <= REGION["RC"].offset + REGION["RC"].size, "%s control" % ring.key)
    check(ring_head_offset("C3") == 0x1000 and ring_tail_offset("C3") == 0x1040, "C3 control words")
    check(ring_head_offset("C11d") == 0x1000 + 7 * 0x80, "C11d head")
    check(ring_capacity("C8") == C8_CAPACITY_RECORDS, "C8 capacity 1,024 records")
    check(REGION["C8"].size == 1024 * 128, "C8 1,024 × 128 B")
    check(REGION["C3"].size == 64 * 1024 * 1024 and REGION["C4"].size == 8 * 1024 * 1024, "C3 64 MiB, C4 8 MiB")
    check(REGION["C12"].size == 4 * 1024 * 1024, "C12 4 MiB")
    check(all(REGION[k].size == 256 * 1024 for k in ("C11a", "C11b", "C11c", "C11d")), "C11 4 × 256 KiB")
    # records: contiguous fields from 0 to size, struct size equal
    for name, rec in RECORDS.items():
        p = 0
        for f in rec.fields:
            check(f.offset == p, "%s.%s at 0x%x, expected 0x%x (gap or overlap)" % (name, f.name, f.offset, p))
            p = f.offset + f.size
        check(p == rec.size, "%s fields end at 0x%x, size 0x%x" % (name, p, rec.size))
        check(rec.struct.size == rec.size, "%s struct size %d != %d" % (name, rec.struct.size, rec.size))
    # sub-tables inside regions
    check(C2_ENTITIES_OFFSET == RECORDS["SeEntitiesHeader"].size, "C2 entities follow the header")
    check(C2_ENTITIES_OFFSET + C2_MAX_ENTITIES * 96 == C2_SUBPARTS_OFFSET, "C2 subparts follow 1,024 entities")
    check(C2_SUBPARTS_OFFSET + C2_MAX_SUBPARTS * 64 == C2_LIGHTS_OFFSET, "C2 lights follow 2,048 subparts")
    check(C2_LIGHTS_OFFSET + C2_MAX_LIGHTS * 64 <= REGION["C2"].size, "C2 lights fit")
    check(C1_BONES_OFFSET + C1_MAX_BONES * 48 == C1_BONES_END, "C1 160 bones end at 0x1F00")
    check(C1_BONES_END <= REGION["C1"].size, "C1 bones fit")
    check(C9_RECORDS_OFFSET + C9_MAX_CREATURES * 48 <= REGION["C9"].size, "C9 fits")
    check(C10_RESULT_OFFSET + 256 <= REGION["C10"].size, "C10 fits")
    # placement tables, messages and constants (hash blocks 2-5)
    for t in TABLES:
        g = REGION[t.region]
        check(t.record in RECORDS, "table %s.%s: unknown record %s" % (t.region, t.name, t.record))
        if t.record in RECORDS:
            check(t.offset + t.count * RECORDS[t.record].size <= g.size, "table %s.%s exceeds its region" % (t.region, t.name))
        check(g.kind != KIND_BYTE_RING, "table %s.%s inside a byte ring" % (t.region, t.name))
    for g in REGIONS:
        if g.kind != KIND_BYTE_RING:
            mine = [t for t in TABLES if t.region == g.key]
            check(any(t.offset == 0 for t in mine) and any(RECORDS[t.record].size == g.record_size for t in mine),
                  "%s: needs a table at offset 0 and one whose stride is recordSize %d" % (g.key, g.record_size))
    for key, mtype, rec in MESSAGES:
        check(rec in RECORDS and REGION[key].kind == KIND_BYTE_RING and mtype != PAD_TYPE, "message %s %d %s" % (key, mtype, rec))
    check(CONSTS["SnPhase"] == {v: k for k, v in SN_PHASES.items()} and CONSTS["SePhase"] == SE_PHASE, "phase tables agree")
    check(all(CONSTS["Overlays"][k] == 1 << b for k, b in OVERLAYS.items()), "overlay bits agree")
    check(CONSTS["C4Type"]["ClearAll"] == C4_TYPES["ClearAll"] == 9 and CONSTS["C3Type"]["MaxChunkBytes"] == C3_MAX_CHUNK,
          "message codes agree")
    # spot checks typed from CONTRACT sentences
    check(framed_size(RECORDS["PartAdd"].size) == 112 and 8 + RECORDS["PartAdd"].size == 108,
          "PartAdd: 100 B payload, 108 B framed, 112 B padded")
    check(HDR.offset("devControl") == 0x038, "devControl at C0 0x038")
    check(HDR.offset("seLoadStep") == 0x0A0, "seLoadStep at 0x0A0")
    check(SE_STATE.offset("Rs") == 0x40 and SE_STATE.offset("siteHash") == 0x48, "C5 Rs/siteHash")
    check(SN_STATUS.offset("capabilities") == 0x78 and SN_STATUS.offset("hostKind") == 0x5C, "C7 hostKind/caps")
    check(DAMAGE.offset("snTimeSec") == 0x68, "C8 snTimeSec at 0x68")
    check(RECORDS["StaticPrimitive"].offset("classIdHash") == 0x2C, "C12 classIdHash at 0x2C")
    check(MAGIC == struct.unpack("<I", MAGIC_BYTES)[0], "magic is DEEP little-endian")
    # the canonical text and the pinned hash
    text = canonical_text()
    check(text.count("\n") == CANONICAL_LINES_1_0, "canonical text has %d lines, CONTRACT says %d"
          % (text.count("\n"), CANONICAL_LINES_1_0))
    check(text.isascii() and "  " not in text and " \n" not in text, "canonical text ASCII, single spaces")
    h = fnv1a64(text.encode("ascii"))
    check(h == PINNED_LAYOUT_HASH_1_0, "layout hash %s != pinned %s" % (hex64(h), hex64(PINNED_LAYOUT_HASH_1_0)))
    check(fnv1a64(b"") == FNV64_OFFSET and fnv1a64(b"a") == 0xAF63DC4C8601EC8C, "FNV-1a 64 test vectors")
    if verbose:
        for e in errors:
            print("  FAIL " + e)
    return errors

# ----------------------------------------------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------------------------------------------

def repo_root() -> str:
    return os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

DEFAULT_FIXTURE = os.path.join(repo_root(), "tests", "DeepEngineers.Wire.Tests", "Fixtures", "layout-1.0.txt")

def compare(fixture: str) -> int:
    mine = canonical_text().encode("ascii")
    my_hash = fnv1a64(mine)
    print("python text: %d lines, %d bytes, FNV-1a 64 %s" % (mine.count(b"\n"), len(mine), hex64(my_hash)))
    if not os.path.exists(fixture):
        print("C# fixture not found: %s" % fixture)
        print("LAYOUT COMPARE NOT RUN (fixture missing)")
        return 2
    with open(fixture, "rb") as f:
        theirs = f.read()
    their_hash = fnv1a64(theirs)
    print("c# fixture : %d lines, %d bytes, FNV-1a 64 %s (%s)" % (theirs.count(b"\n"), len(theirs),
                                                                 hex64(their_hash), fixture))
    print("pinned     : %s (CONTRACT s6)" % hex64(PINNED_LAYOUT_HASH_1_0))
    if b"\r" in theirs:
        print("note: the fixture contains CR bytes (a git autocrlf checkout?); the canonical text uses LF only")
    if mine == theirs:
        ok = my_hash == PINNED_LAYOUT_HASH_1_0
        print("texts identical; hash %s pinned value" % ("equals the" if ok else "DIFFERS FROM the"))
        print("LAYOUT COMPARE PASS" if ok else "LAYOUT COMPARE FAIL (pinned hash)")
        return 0 if ok else 1
    a = mine.decode("ascii").splitlines()
    b = theirs.decode("ascii", "replace").replace("\r\n", "\n").splitlines()
    diff = list(difflib.unified_diff(b, a, "c#-fixture", "python", lineterm="", n=0))
    print("texts differ: %d diff lines (- c#, + python)" % len(diff))
    for line in diff[:200]:
        print("  " + line)
    print("LAYOUT COMPARE FAIL")
    return 1

def table_json() -> dict:
    return {
        "layout": "%d.%d" % (LAYOUT_MAJOR, LAYOUT_MINOR),
        "layoutHash": hex64(layout_hash()),
        "sectionSize": SECTION_SIZE,
        "regions": [dict(key=r.key, id=r.id, kind=r.kind, offset=r.offset, size=r.size, recordSize=r.record_size,
                         version=r.version) for r in REGIONS_BY_ID],
        "rings": [dict(index=r.index, key=r.key, head=ring_head_offset(r.key), tail=ring_tail_offset(r.key),
                       producer=r.producer, consumer=r.consumer) for r in RINGS],
        "records": {name: {"size": rec.size, "fields": [[f.name, f.offset, f.size, f.type] for f in rec.fields]}
                    for name, rec in sorted(RECORDS.items())},
    }

def main(argv) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="Independent Python layout oracle (CONTRACT §5, §6)")
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("selftest")
    t = sub.add_parser("text")
    t.add_argument("--out")
    sub.add_parser("hash")
    c = sub.add_parser("compare")
    c.add_argument("fixture", nargs="?", default=DEFAULT_FIXTURE)
    sub.add_parser("table")
    a = ap.parse_args(argv)
    if a.cmd == "text":
        data = canonical_text().encode("ascii")
        if a.out:
            with open(a.out, "wb") as f:
                f.write(data)
            print("wrote %s (%d bytes, %s)" % (a.out, len(data), hex64(fnv1a64(data))))
        else:
            sys.stdout.write(data.decode("ascii"))
        return 0
    if a.cmd == "hash":
        print(hex64(layout_hash()))
        return 0
    if a.cmd == "compare":
        return compare(a.fixture)
    if a.cmd == "table":
        print(json.dumps(table_json(), indent=1))
        return 0
    errs = selftest()
    text = canonical_text()
    print("layout %d.%d: %d regions, %d records, %d canonical lines, hash %s (pinned %s)" % (
        LAYOUT_MAJOR, LAYOUT_MINOR, len(REGIONS), len(RECORDS), text.count("\n"), hex64(layout_hash()),
        hex64(PINNED_LAYOUT_HASH_1_0)))
    print("LAYOUT SELFTEST PASS" if not errs else "LAYOUT SELFTEST FAIL (%d)" % len(errs))
    return 0 if not errs else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
