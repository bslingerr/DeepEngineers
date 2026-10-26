"""S17 Python twin (docs/S17-BRIEF.md; CONTRACT section 5.4 "S17 conventions"): the canonical text and its FNV-1a 64, the
kind table, the C5 request / C7 answer codec and the aim rule. Shares no code with the C# (Wire S17Conventions.cs); the
vectors it writes under tests/s17/vectors/ are read by the C# tests (S17Tests in tests/DeepEngineers.Wire.Tests).

 python -I tools/s17/s17_ref.py --selftest hand checks, the pin (C# source and CONTRACT block), vectors current
 python -I tools/s17/s17_ref.py --vectors (re)writes tests/s17/vectors/{wire,aim}.tsv
 python -I tools/s17/s17_ref.py --text prints the canonical text
"""
from __future__ import annotations

import math
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
VEC_DIR = os.path.join(ROOT, "tests", "s17", "vectors")
CS_SOURCE = os.path.join(ROOT, "src", "DeepEngineers.Wire", "S17Conventions.cs")
CONTRACT = os.path.join(ROOT, "docs", "CONTRACT.md")

HEADER = "ext s17 fish 2"
PINNED_HASH = 0x120ee00b7501f416
PINNED_LINES = 70
PINNED_BYTES = 5469

C9 = {"RowsMax": 32, "RadiusCm": 2000, "PeriodMs": 100, "StaleMs": 1000}
CATCHABLE = 256
CATCH = {"ReachCm": 250, "AimCm": 60, "LeadMaxMs": 200, "SlackCm": 150, "AckTimeoutMs": 1000, "UnpublishedMs": 5000, "ForgetMs": 30000}
C5_OFFSET, C5_SIZE, C5_VERSION, ACTIVE = 0xCC, 0x20, 1, 1
C7_OFFSET, C7_SIZE, C7_VERSION = 0xC8, 0x10, 1
C5_FIELDS = [("version", "0xcc", 1, "u8"), ("flags", "0xcd", 1, "u8"), ("reserved", "0xce", 2, "u16"), ("reqSeq", "0xd0", 4, "u32"),
             ("instanceId", "0xd4", 4, "u32"), ("techType", "0xd8", 4, "i32"), ("posSn", "0xdc", 12, "f32x3"), ("worldEpoch", "0xe8", 4, "u32")]
C7_FIELDS = [("version", "0xc8", 1, "u8"), ("result", "0xc9", 1, "u8"), ("reserved", "0xca", 2, "u16"), ("ackSeq", "0xcc", 4, "u32"),
             ("instanceId", "0xd0", 4, "u32"), ("techType", "0xd4", 4, "i32")]
RESULTS = ["Caught", "Gone", "Moved", "Kind", "Off"]
REFUSALS = ["Busy", "Again", "Timeout", "Late", "NoItem", "NotCaught"]
FOOD = {"RawMicro": 2670, "CookedMicro": 5340, "TimeS": 30, "CookS": 4}
ITEM = {"MassG": 1500, "VolumeMl": 1250, "SizeMm": 150}
# (TechType name, value, display name): the S8a spawn table's edible fish (TechType.cs values)
KINDS = [("Peeper", 2504, "Peeper"), ("Bladderfish", 2515, "Bladderfish"), ("Boomerang", 2510, "Boomerang"),
         ("Hoopfish", 2531, "Hoopfish"), ("Spadefish", 2520, "Spadefish"), ("Reginald", 2519, "Reginald"),
         ("Eyeye", 2513, "Eyeye"), ("HoleFish", 2501, "Holefish"), ("Spinefish", 2546, "Spinefish"),
         ("GarryFish", 2507, "Garryfish")]
FOOD_PROCESSOR_CLASS = "FoodProcessorRations"

FORMAT_LINES = [
    "aim the target is the Catchable C9 row nearest the eye with t = (p - cam) . fwd > 0, |(p - cam) - t * fwd| <= AimCm / 100 and |p - eye| <= ReachCm / 100; p = SnToSe(posSn + velSn * min(age, LeadMaxMs)), age = since the C9 read; eye = the character's head; empty hands, on foot (no seat), alive, no SE use object in front (the character detector's UseObject null), C9 fresh (its seq changed within StaleMs), the chart not in transition",
    "c5.s17 SeState 0xcc-0xeb written by SE every tick while S17 runs (version 0 = absent): the last catch request; flags Active while it waits for its ack (cleared on the ack or after AckTimeoutMs); reqSeq starts at a per-session value and grows by one per request",
    "c7.s17 SnStatus 0xc8-0xd7 written by Subnautica every frame while S17 runs (version 0 = absent): the answer to the last request handled, ackSeq = its reqSeq, result = Result",
    "c9.s17 Subnautica writes C9 every PeriodMs: the RowsMax nearest creatures of a kind row within RadiusCm of the camera, dead ones too (none in a containment: WaterParkCreature), nearest first; the overlap's collider buffer grows (up to 4096) when a scan fills it; instanceId = GameObject.GetInstanceID as u32, techType, posSn, velSn (its Rigidbody), aggression 0, flags Catchable, targetEntityId 0; count 0 when none",
    "catch.se F (SE's USE control, newly pressed) on a target sends one request when none waits (Busy) and the fish was not requested within ForgetMs (Again; a Moved answer or a timeout forgets it); the ack with ackSeq = reqSeq within AckTimeoutMs of the request's first write into C5 (UnpublishedMs from F while it was never written) gives the item (Caught) or refuses (NotCaught); none: Timeout, no item then; a later Caught ack (Late) still gives the item",
    "catch.sn a request (version 1, Active, reqSeq not the last handled) is handled once: the creature of instanceId among the last C9 rows, Gone when its object no longer exists, Kind when it is not a kind row, Moved when it lies over SlackCm from posSn, else Caught: its GameObject destroyed (no death, no loot) and acked; Gone too when the request's worldEpoch is not its C5 record's; a WaterParkCreature is Kind; a fish of an S8a spawn point leaves a tombstone there (no respawn until the camera is beyond the spawns' DespawnM); Off while Subnautica has no world loaded",
    "item.give one raw item of the kind into the character's inventory (MyInventory.AddItems), dropped at its feet (MyFloatingObjects.Spawn) when it does not fit; NoItem when the world mod's definition is missing",
    "items ConsumableItem DE_Fish_<Kind>_Raw and DE_Fish_<Kind>_Cooked: DisplayName <name> (raw) / <name> (cooked), Mass MassG / 1000 kg, Volume VolumeMl / 1000 L, Size SizeMm / 1000 m (SE's meal packs), one Food stat RawMicro or CookedMicro / 1e6 per TimeS (raw = MealPack_KelpCrisp, SE's smallest meal pack), no Health stat; blueprint DE_CookFish_<Kind>: 1 raw -> 1 cooked in CookS s, class FoodProcessorRations",
]

def raw_subtype(name):
    return "DE_Fish_%s_Raw" % name

def cooked_subtype(name):
    return "DE_Fish_%s_Cooked" % name

def blueprint(name):
    return "DE_CookFish_%s" % name

def canonical_text():
    out = [HEADER]
    c = lambda g, n, v: out.append("const %s %s %d" % (g, n, v))  # noqa: E731
    for k in ("RowsMax", "RadiusCm", "PeriodMs", "StaleMs"):
        c("C9", k, C9[k])
    c("C9Flags", "Catchable", CATCHABLE)
    for k in ("ReachCm", "AimCm", "LeadMaxMs", "SlackCm", "AckTimeoutMs", "UnpublishedMs", "ForgetMs"):
        c("Catch", k, CATCH[k])
    c("C5", "Offset", C5_OFFSET); c("C5", "Size", C5_SIZE); c("C5", "Version", C5_VERSION)
    c("C5Flags", "Active", ACTIVE)
    out += ["field s17 %s %s %d %s" % f for f in C5_FIELDS]
    c("C7", "Offset", C7_OFFSET); c("C7", "Size", C7_SIZE); c("C7", "Version", C7_VERSION)
    out += ["field s17ack %s %s %d %s" % f for f in C7_FIELDS]
    for i, n in enumerate(RESULTS, 1):
        c("Result", n, i)
    for i, n in enumerate(REFUSALS, 1):
        c("Refusal", n, i)
    for k in ("RawMicro", "CookedMicro", "TimeS", "CookS"):
        c("Food", k, FOOD[k])
    for k in ("MassG", "VolumeMl", "SizeMm"):
        c("Item", k, ITEM[k])
    for name, tt, disp in KINDS:
        out.append("kind %s %d name %s raw %s cooked %s blueprint %s" % (name, tt, disp, raw_subtype(name), cooked_subtype(name), blueprint(name)))
    out += ["format " + f for f in sorted(FORMAT_LINES)]
    return "".join(l + "\n" for l in out)

def fnv1a64(data):
    h = 0xcbf29ce484222325
    for b in data:
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def text_hash():
    return fnv1a64(canonical_text().encode("ascii"))

# ------------------------------------------------------------------------------------------------------------- codec

def f32(v):
    return struct.unpack("<f", struct.pack("<f", v))[0]

def encode_request(flags, seq, inst, tech, x, y, z, epoch):
    return struct.pack("<BBHIIifffI", C5_VERSION, flags, 0, seq, inst, tech, x, y, z, epoch)

def decode_request(b):
    if len(b) < C5_SIZE or b[0] != C5_VERSION:
        return None
    v, flags, _r, seq, inst, tech, x, y, z, epoch = struct.unpack("<BBHIIifffI", bytes(b[:C5_SIZE]))
    return {"flags": flags, "seq": seq, "inst": inst, "tech": tech, "pos": (x, y, z), "epoch": epoch}

def encode_ack(result, seq, inst, tech):
    return struct.pack("<BBHIIi", C7_VERSION, result, 0, seq, inst, tech)

def decode_ack(b):
    if len(b) < C7_SIZE or b[0] != C7_VERSION:
        return None
    v, result, _r, seq, inst, tech = struct.unpack("<BBHIIi", bytes(b[:C7_SIZE]))
    return {"result": result, "seq": seq, "inst": inst, "tech": tech}

def kind_of(tech):
    for k in KINDS:
        if k[1] == tech:
            return k
    return None

# ------------------------------------------------------------------------------------------------------------- aim

def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])

def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]

def _len(a):
    return math.sqrt(_dot(a, a))

def aim(cam, fwd, eye, p):
    """(qualifies, reach) of format aim; fwd is a unit vector."""
    reach = _len(_sub(p, eye))
    d = _sub(p, cam)
    t = _dot(d, fwd)
    if not t > 0:
        return False, reach
    perp = (d[0] - fwd[0] * t, d[1] - fwd[1] * t, d[2] - fwd[2] * t)
    if _len(perp) > CATCH["AimCm"] / 100.0:
        return False, reach
    return reach <= CATCH["ReachCm"] / 100.0, reach

def lead_s(age_s):
    return 0.0 if not age_s > 0 else min(age_s, CATCH["LeadMaxMs"] / 1000.0)

def within_slack(dx, dy, dz):
    s = CATCH["SlackCm"] / 100.0
    return dx * dx + dy * dy + dz * dz <= s * s

# ------------------------------------------------------------------------------------------------------------- vectors

def _g(v):
    return repr(float(v))

def vectors():
    wire = ["# kind\tfields...\thex (tools/s17/s17_ref.py --vectors)"]
    reqs = [(1, 7, 0xFFFFFF9C, 2504, 12.5, -3.25, 400.0, 3), (0, 4294967295, 1, 2546, -1e-3, 1e5, -0.5, 0), (1, 1, 123456, 2507, 0.0, 0.0, 0.0, 77)]
    for r in reqs:
        fl, seq, inst, tech, x, y, z, ep = r
        x, y, z = f32(x), f32(y), f32(z)
        wire.append("req\t%d\t%d\t%d\t%d\t%s\t%s\t%s\t%d\t%s" % (fl, seq, inst, tech, _g(x), _g(y), _g(z), ep, encode_request(fl, seq, inst, tech, x, y, z, ep).hex()))
    for a in [(1, 7, 0xFFFFFF9C, 2504), (3, 9, 5, 2515), (5, 4294967295, 0, 0)]:
        wire.append("ack\t%d\t%d\t%d\t%d\t%s" % (a + (encode_ack(*a).hex(),)))
    aims = ["# cam(3)\tfwd(3)\teye(3)\tp(3)\tqualifies\treach"]
    cam, fwd, eye = (0.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, 0.3)
    for p in [(0, 0, 2.0), (0, 0, 2.8), (0, 0, 2.79), (0.59, 0, 1.5), (0.61, 0, 1.5), (0, 0, -1.0), (0.3, 0.3, 2.5), (0, 0, 0.0001)]:
        ok, reach = aim(cam, fwd, eye, p)
        aims.append("\t".join(_g(v) for v in cam + fwd + eye + tuple(float(c) for c in p)) + "\t%d\t%s" % (1 if ok else 0, _g(reach)))
    cam2, eye2 = (10.0, 5.0, -2.0), (12.0, 5.0, -2.0)    # third person: the camera 2 m behind the head
    s = 1 / math.sqrt(2.0)
    fwd2 = (s, s, 0.0)
    for p in [(13.0, 7.0, -2.0), (14.0, 9.0, -2.0), (12.5, 7.5, -2.0), (12.0, 7.0, -2.0)]:
        ok, reach = aim(cam2, fwd2, eye2, p)
        aims.append("\t".join(_g(v) for v in cam2 + fwd2 + eye2 + tuple(float(c) for c in p)) + "\t%d\t%s" % (1 if ok else 0, _g(reach)))
    return {"wire.tsv": "\n".join(wire) + "\n", "aim.tsv": "\n".join(aims) + "\n"}

def write_vectors():
    os.makedirs(VEC_DIR, exist_ok=True)
    for name, text in vectors().items():
        with open(os.path.join(VEC_DIR, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print("wrote", os.path.join(VEC_DIR, name))

def cs_pinned_hash():
    try:
        with open(CS_SOURCE, encoding="utf-8") as f:
            m = re.search(r"PinnedHash\s*=\s*0x([0-9a-fA-F]+)UL", f.read())
        return int(m.group(1), 16) if m else None
    except OSError:
        return None

def contract_block():
    try:
        with open(CONTRACT, encoding="utf-8") as f:
            doc = f.read().replace("\r\n", "\n")
    except OSError:
        return None
    i = doc.find("```\n" + HEADER + "\n")
    if i < 0:
        return None
    i += 4
    j = doc.find("```", i)
    return None if j < 0 else doc[i:j]

def selftest():
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S17 twin self-test (tools/s17/s17_ref.py --selftest)")
    t = canonical_text()
    h = fnv1a64(t.encode("ascii"))
    check(h == PINNED_HASH, "hash 0x%016x = pinned 0x%016x" % (h, PINNED_HASH))
    check(t.count("\n") == PINNED_LINES and len(t) == PINNED_BYTES, "%d lines, %d bytes (pinned %d, %d)" % (t.count("\n"), len(t), PINNED_LINES, PINNED_BYTES))
    check(cs_pinned_hash() == PINNED_HASH, "the C# source pins the same hash")
    check(contract_block() == t, "the CONTRACT block equals the canonical text")
    check(C5_OFFSET >= 0xAC + 0x20 and C5_OFFSET + C5_SIZE <= 0x100, "C5 block inside SeState pad_80 after S15's 0xAC-0xCB")
    check(C7_OFFSET >= 0xC4 + 4 and C7_OFFSET + C7_SIZE <= 0x100, "C7 block inside SnStatus pad_8c after S14's 0xC4-0xC7")
    check(sum(f[2] for f in C5_FIELDS) == C5_SIZE and sum(f[2] for f in C7_FIELDS) == C7_SIZE, "the field tables fill the blocks")
    check(all(int(f[1], 16) == C5_OFFSET + sum(g[2] for g in C5_FIELDS[:i]) for i, f in enumerate(C5_FIELDS)), "C5 offsets contiguous")
    check(all(int(f[1], 16) == C7_OFFSET + sum(g[2] for g in C7_FIELDS[:i]) for i, f in enumerate(C7_FIELDS)), "C7 offsets contiguous")
    check(CATCHABLE == 1 << 8 and CATCHABLE & 3 == 0, "Catchable is C9 flags bit 8 (bits 0-1 keep hunting / targetIsMirror)")
    check(C9["RowsMax"] * 48 + 0x40 <= 0x4000, "the fish list fits the C9 region")
    check(len({k[1] for k in KINDS}) == len(KINDS) and kind_of(2504)[0] == "Peeper" and kind_of(2535) is None, "kinds unique; Mesmer (2535) is not a kind")
    check(FOOD["CookedMicro"] == 2 * FOOD["RawMicro"] and FOOD["RawMicro"] == 2670 and FOOD["CookedMicro"] <= 33500,
          "raw = MealPack_KelpCrisp 0.00267, cooked twice, under SE's largest ration 0.0335")
    b = encode_request(1, 5, 9, 2504, 1.0, 2.0, 3.0, 4)
    check(len(b) == C5_SIZE and decode_request(b)["seq"] == 5 and decode_request(b"\x00" * 32) is None, "request codec round trip; version 0 = absent")
    a = encode_ack(1, 5, 9, 2504)
    check(len(a) == C7_SIZE and decode_ack(a) == {"result": 1, "seq": 5, "inst": 9, "tech": 2504}, "ack codec round trip")
    check(aim((0, 0, 0), (0, 0, 1), (0, 0, 0), (0, 0, 2.5))[0] and not aim((0, 0, 0), (0, 0, 1), (0, 0, 0), (0, 0, 2.51))[0], "reach 2.5 m")
    check(aim((0, 0, 0), (0, 0, 1), (0, 0, 0), (0.6, 0, 1))[0] and not aim((0, 0, 0), (0, 0, 1), (0, 0, 0), (0.61, 0, 1))[0], "aim 0.6 m off the ray")
    check(not aim((0, 0, 0), (0, 0, 1), (0, 0, 0), (0, 0, -0.5))[0], "behind the camera: no target")
    check(lead_s(0.5) == 0.2 and lead_s(-1) == 0.0 and within_slack(1.5, 0, 0) and not within_slack(1.0, 1.2, 0), "lead clamp and slack")
    v = vectors()
    for name, text in v.items():
        p = os.path.join(VEC_DIR, name)
        cur = None
        if os.path.isfile(p):
            with open(p, encoding="utf-8") as f:
                cur = f.read()
        check(cur == text, "vectors %s current" % name)
    ok = all(results)
    print("S17 TWIN SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv):
    if "--vectors" in argv:
        write_vectors()
        return 0
    if "--text" in argv:
        sys.stdout.write(canonical_text())
        return 0
    if "--hash" in argv:
        t = canonical_text()
        print("0x%016x %d lines %d bytes" % (text_hash(), t.count("\n"), len(t)))
        return 0
    return selftest()

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
