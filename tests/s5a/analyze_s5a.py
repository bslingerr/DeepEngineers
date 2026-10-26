"""S5a "Arms and breath": the oracle over a launcher `s5a` session (stdlib only; `python -I`).

The SE plugin (SwimController, CONTRACT §2.10 and §5.4 "C1 swim extension") sends its swim state to Subnautica in the
C1 SeFrame record, bytes 0x94-0x9F of the layout-1.0 pad (layout 1.0 unchanged, hash 0x4f3a35f6fc1eaf31; the extension
pinned on its own: table C1_SWIM below = Wire's SwimWire.CanonicalText, FNV-1a 64 0x832ba93ca25bc307). The launcher's
s5a session (SE alone) drives the swim controller with de.swim scripts and samples the extension from the section;
this script judges the SE side, and, when the Subnautica log of the same session is given (--sn-log), joins the SN
plugin's lines by the C1 tick they name.

 python -I tests/s5a/analyze_s5a.py tests/out/s5a/<stamp> [--sn-log PATH...] [--lenient] [--json OUT]
 python -I tests/s5a/analyze_s5a.py --selftest
Exit 0 = every required row PASS, 1 = a red row, 2 = usage.

Parts and rows (ids S5a.FINS, S5a.O2, S5a.ARMS, S5a.CHOKE, S5a.SN, S5a.ALL; the launcher writes s5a-session.jsonl):
 fins two runs of the S2b.N1 scripts (n1a, n1b) at -50 m, `de.swim fins=ultra` then `fins=off`: every swim row of a
 run replays exactly in swim_replay.py (its `fin` bits are the run's modifier: 3.2 / 0), the S2b.N1 metrics
 hold against the open-loop model, and the measured forward steady speed is 8.1 m/s (ultra) / 5.70 m/s (off)
 within 5 %; the C1 `fins` byte of the window is 2 / 0. S5a.M1 (fins claimed, not applied) is red here.
 o2 in the water at -20 m, `de.swim o2=1.0`, `0.2`, `0.05`, then `auto`: after 0.5 s the C1 `o2` byte is
 round(v x 255) +-1 with the O2Override flag set in >= 90 % of the samples of each hold; after `auto` the flag is
 clear and the byte follows the suit (the dump's o2Level x 255 +-5). S5a.M2 (byte stuck) is red here.
 arms at -20 m a script forward 3 s, strafe 3 s, up 3 s, release 3 s while sampling C1: InWater and Controls set,
 mode Swim, Moving set in the drive segments (axes with the right sign) and clear in the release, speedH >= 0.6
 in the last second of forward/strafe, speedV >= 0.6 in the last second of up; then a teleport out of the water
 (feet +2 m): InWater clear within 1 s and for the rest of the window. S5a.M3 (flags frozen) is red here.
 choke at -20 m `o2=0.05` held 8 s then `auto`: the byte is 13 +-1 with the override flag; with --sn-log the SN
 plugin's DE.Breath lowO2 or choke event falls in the window, DE.O2 mirrored follows (0.05 +-0.02, snOxygen =
 mirrored x capacity +-1 unit, shielded=1), and DE.PDA played stays 0.
 sn (--sn-log) DE.Arms visible=1 in the arms window with InWater set and visible=0 in the out window; >= 1
 DE.Arms and DE.O2 line per second while the ghost is active; no DE.Ghost damage/death line.
 all no `DE.Swim FEATURE DISABLED` or `DE.Swim error` line; every C1 sample has version 1; the mutants seen.

SN LOG CONTRACT (the lines the Subnautica plugin writes; parsed here by regex `DE\\.(Arms|O2|Breath|PDA|SwimWire|Ghost) `
anywhere in a line, so BepInEx's `[Info:DeepEngineers] ` prefix or a JSONL wrapper with a "msg"/"text"/"line" key
are both fine; tokens are `key=value` separated by single spaces; numbers in invariant culture; `tick` = the C1 tick
of the SeFrame the SN plugin used on that frame, which is how the lines are joined to the SE session):
 DE.Arms tick=<u64> state=<out|swim|surface|fly> visible=<0|1> moving=<0|1> move=<f>,<s>,<v> speedH=<0.000>
 speedV=<0.000> vy=<-1.000..1.000> fins=<off|basic|ultra> anim=<clip or ->
 every 0.5 s while a C1 extension with version 1 is read, plus one line at every change of `state` or
 `visible`. state: out = InWater flag clear; fly = mode 3; surface = AtSurface flag; swim otherwise.
 visible = the arms renderers are enabled this frame (must be 1 only with InWater set and the camera
 underwater, 0 otherwise). move = the decoded axes -1..1 (3 decimals); speedH/speedV = byte / 255.
 DE.O2 tick=<u64> mirrored=<0.000> snOxygen=<0.0> capacity=<0.0> override=<0|1> shielded=<0|1>
 every 0.5 s while the ghost is active. mirrored = the C1 o2 byte / 255; snOxygen = the ghost's
 OxygenManager available oxygen after the mirror; capacity = its capacity; override = the O2Override flag;
 shielded = the ghost's liveMixin is shielded (must be 1).
 DE.Breath tick=<u64> event=<breath|bubbles|lowO2|choke|refill> o2=<0.000> depth=<0.0>
 one line per event as the game's own systems fire: breath = the breathing sound / PlayerBreathBubbles
 trigger, bubbles = a bubble burst, lowO2 = the LowOxygenAlert threshold crossed downwards, choke = the
 choking sound started, refill = back above the alert threshold.
 DE.PDA tick=<u64> suppressed=<n> played=<m> window=60
 once per minute and at shutdown: PDA voice / notification lines suppressed since the last line, and lines
 that got through (`played` must be 0).
 DE.SwimWire tick=<u64> version=<n> flags=0x<xx> mode=<n> fins=<n> speedH=<n> speedV=<n> move=<f>,<s>,<v> o2=<n>
 dc=<n> vy=<n> on the first C1 record with the extension and whenever `version` changes (raw bytes).
 DE.Ghost tick=<u64> event=<damage|death|moved|saved>... must never appear (CONTRACT §1: nothing hurts the ghost).
 Only lines with such an `event=` count: the S1 ghost rule's informational `DE.Ghost setup: …` /
 `DE.Ghost assert …` lines (no event=) are part of every SN log and are not ghost-harm events.

Session events (s5a-session.jsonl, written by launcher.py s5a; the stand-in of --selftest writes the same):
 start {parts, name, qpcFrequency, mutants, seAlone, snLog}; part {name, phase: begin|end, qpc, tick};
 fins {label, value, reply, qpc, tick}; o2_step {label, value: <0..1>|"auto", qpc, tick, reply};
 swim_script {label, segments, seconds, qpc, tick, startTick, supported}; reply {label, cmd, args, reply, qpc};
 c1swim {qpc, tick, seq, version, flags, mode, fins, speedH, speedV, moveF, moveS, moveV, o2, dc, vy} (~20 Hz
 while a part samples); end {rc}.
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import os
import re
import struct
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, "..", ".."))
for _p in (_HERE, os.path.join(_ROOT, "tests", "s2"), os.path.join(_ROOT, "tests", "s2b")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C  # noqa: E402 (rows, table, verdict, JSONL reading)
import swim_replay as W  # noqa: E402 (the replay-exact model, N1 oracle, row reader)
import sn_reference as R  # noqa: E402

# ------------------------------------------------------------------------------------------------ the C1 swim extension
# (name, offset in SeFrame, struct code) in offset order; the canonical text and its hash are pinned against
# Wire's SwimWire.CanonicalText / SwimWire.Hash by SwimWireTests.cs and by --selftest here.
C1_SWIM = [("version", 0x94, "B"), ("flags", 0x95, "B"), ("mode", 0x96, "B"), ("fins", 0x97, "B"), ("speedH", 0x98, "B"),
           ("speedV", 0x99, "B"), ("moveForward", 0x9A, "b"), ("moveStrafe", 0x9B, "b"), ("moveVertical", 0x9C, "b"),
           ("o2", 0x9D, "B"), ("depthClass", 0x9E, "B"), ("vyFrac", 0x9F, "b")]
C1_SWIM_OFFSET, C1_SWIM_SIZE, C1_SWIM_VERSION = 0x94, 12, 1
C1_SWIM_HASH = 0x832ba93ca25bc307
FLAG_IN_WATER, FLAG_UNDERWATER, FLAG_AT_SURFACE, FLAG_GROUNDED = 1, 2, 4, 8
FLAG_MOVING, FLAG_JETPACK_SUPPRESSED, FLAG_CONTROLS, FLAG_O2_OVERRIDE = 16, 32, 64, 128
MODE_WALK, MODE_SWIM, MODE_SURFACE, MODE_FLY = 0, 1, 2, 3
FINS_BYTE = {"off": 0, "basic": 1, "ultra": 2}
SAMPLE_KEYS = ("version", "flags", "mode", "fins", "speedH", "speedV", "moveF", "moveS", "moveV", "o2", "dc", "vy")

TICK_HZ = 60.0
SETTLE_S = 0.5                  # after a command: the controller's next tick and the C1 write
SHARE = 0.90                    # share of the samples of a hold that must agree
REL_TOL = 0.05                  # fins steady speeds: within 5 % (S2b.N1)
SPEED_FRAC_MIN = 0.6            # arms: speed fraction in the last second of a drive segment
OUT_WITHIN_S = 1.0              # arms.out: InWater clear this soon after the teleport
O2_SUIT_TOL = 5                 # o2 auto: byte vs the dump's o2Level x 255
SN_LINE_GAP_S = 1.0             # >= one DE.Arms / DE.O2 line per second while active
_SN_LINE = re.compile(r"DE\.(Arms|O2|Breath|PDA|SwimWire|Ghost) (.*)$")
GHOST_EVENTS = ("damage", "death", "moved", "saved")   # the DE.Ghost events that are red (CONTRACT §1)
_KV = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=(\S+)")
_DISABLED = "DE.Swim FEATURE DISABLED"
_ERROR_LINES = ("DE.Swim error", "DE.Swim depth MISMATCH", "(S2b patch off)")

def c1_swim_text() -> str:
    out = ["ext C1 swim %d 0x%x %d" % (C1_SWIM_VERSION, C1_SWIM_OFFSET, C1_SWIM_SIZE)]
    for name, off, code in C1_SWIM:
        out.append("field swim %s 0x%x 1 %s" % (name, off, "u8" if code == "B" else "i8"))
    return "\n".join(out) + "\n"

def fnv1a64(text: str) -> int:
    h = 0xcbf29ce484222325
    for c in text.encode("ascii"):
        h ^= c
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def decode_c1_swim(record: bytes) -> dict:
    """The extension of one C1 SeFrame record (>= 0xA0 bytes) as the session's c1swim fields."""
    v = struct.unpack_from("<BBBBBBbbbBBb", record, C1_SWIM_OFFSET)
    return {"version": v[0], "flags": v[1], "mode": v[2], "fins": v[3], "speedH": v[4], "speedV": v[5], "moveF": v[6],
            "moveS": v[7], "moveV": v[8], "o2": v[9], "dc": v[10], "vy": v[11]}

def encode_c1_swim(d: dict) -> bytes:
    return struct.pack("<BBBBBBbbbBBb", d.get("version", 1), d.get("flags", 0), d.get("mode", 0), d.get("fins", 0),
                       d.get("speedH", 0), d.get("speedV", 0), d.get("moveF", 0), d.get("moveS", 0), d.get("moveV", 0),
                       d.get("o2", 255), d.get("dc", 0), d.get("vy", 0))

def frac_byte(x: float) -> int:
    return 0 if x <= 0 else 255 if x >= 1 else int(math.floor(x * 255.0 + 0.5))

# ------------------------------------------------------------------------------------------------ the SN log
def _num(s):
    try:
        return float(s)
    except ValueError:
        return s

def parse_sn_line(text: str):
    """One SN log line -> {"kind": Arms|O2|Breath|PDA|SwimWire|Ghost, "tick": int|None, <key>: value...} or None."""
    m = _SN_LINE.search(text)
    if not m:
        return None
    d = {"kind": m.group(1), "tick": None, "raw": text.strip()}
    for k, v in _KV.findall(m.group(2)):
        if k == "move":
            parts = v.split(",")
            d[k] = tuple(_num(p) for p in parts) if len(parts) == 3 else v
        elif k == "flags" and v.startswith("0x"):
            d[k] = int(v, 16)
        else:
            d[k] = _num(v)
    t = d.get("tick")
    d["tick"] = int(t) if isinstance(t, float) else None
    return d

def read_sn_log(paths) -> list:
    out = []
    for p in paths or ():
        if not p or not os.path.exists(p):
            continue
        with open(p, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                text = line
                if line.startswith("{"):
                    try:
                        rec = json.loads(line)
                    except ValueError:
                        rec = None
                    if isinstance(rec, dict):
                        text = next((rec[k] for k in ("msg", "text", "line") if isinstance(rec.get(k), str)), "")
                d = parse_sn_line(text)
                if d:
                    out.append(d)
    return out

# ------------------------------------------------------------------------------------------------ the session
class Session:
    """A collected s5a folder: s5a-session.jsonl, the SE plugin's se-*.jsonl swim rows, the optional SN log lines."""

    def __init__(self, folder, sn_logs=()):
        self.dir = folder
        self.session = C.read_jsonl(os.path.join(folder, "s5a-session.jsonl"))
        self.se_files = sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        self.raw = C.read_jsonl(self.se_files)
        self.start = next((r for r in self.session if r.get("ev") == "start"), {})
        self.parts = self.start.get("parts") or []
        rows = {}
        for rec in self.raw:
            if rec.get("ev") == "swim" and rec.get("v") == W.ROW_VERSION and isinstance(rec.get("tick"), int):
                rows[rec["tick"]] = rec
        self.rows = [rows[k] for k in sorted(rows)]
        self.samples = [r for r in self.session if r.get("ev") == "c1swim" and isinstance(r.get("tick"), int)]
        self.lines = []
        for rec in self.raw:
            text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
            if isinstance(text, str):
                self.lines.append(text)
        sn_paths = list(sn_logs or ())
        if not sn_paths:
            sn_paths = sorted(glob.glob(os.path.join(folder, "sn-*.log"))) + sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))
        self.sn_paths = sn_paths
        self.sn = read_sn_log(sn_paths)

    def ev(self, name):
        return [r for r in self.session if r.get("ev") == name]

    def part(self, name):
        b = next((r for r in self.session if r.get("ev") == "part" and r.get("name") == name and r.get("phase") == "begin"), None)
        e = next((r for r in self.session if r.get("ev") == "part" and r.get("name") == name and r.get("phase") == "end"), None)
        return b, e

    def part_names(self, prefix):
        seen = []
        for r in self.session:
            if r.get("ev") == "part" and r.get("phase") == "begin" and str(r.get("name", "")).startswith(prefix):
                if r["name"] not in seen:
                    seen.append(r["name"])
        return seen

    def window(self, name):
        """(first tick, last tick) of a part by its begin/end events (end open: a very large tick)."""
        b, e = self.part(name)
        if b is None or not isinstance(b.get("tick"), int):
            return None
        return b["tick"], e["tick"] if e and isinstance(e.get("tick"), int) else 1 << 62

    def rows_in(self, w):
        return [r for r in self.rows if w and w[0] <= r["tick"] <= w[1]]

    def samples_in(self, w, settle_s=0.0):
        if not w:
            return []
        t0 = w[0] + int(round(settle_s * TICK_HZ))
        return [s for s in self.samples if t0 <= s["tick"] <= w[1]]

    def sn_in(self, kind, w):
        return [d for d in self.sn if d["kind"] == kind and d["tick"] is not None and w and w[0] <= d["tick"] <= w[1]]

    def replies(self, label_prefix):
        return [r for r in self.session if r.get("ev") == "reply" and str(r.get("label") or "").startswith(label_prefix)]

def _share(samples, pred):
    if not samples:
        return 0.0
    return sum(1 for s in samples if pred(s)) / float(len(samples))

def _pct(x):
    return "%.0f %%" % (100.0 * x)

# ------------------------------------------------------------------------------------------------ fins
def evaluate_fins(sess, rows):
    if "fins" not in sess.parts:
        return
    d = R.derived()
    expect = {"ultra": (W.FINS["ultra"], d["finsUltraForwardSteady"], 2), "off": (0.0, d["forwardSteady"], 0),
              "basic": (W.FINS["basic"], d["finsBasicForwardSteady"], 1)}
    any_part = False
    for mode in ("ultra", "off", "basic"):
        names = sess.part_names("fins.%s." % mode)
        if not names:
            if mode != "basic":
                C.row(rows, "S5a.FINS", "MISSING", "no fins.%s.* part" % mode, "n1a + n1b at fins %s" % mode,
                      "S5a fins %s: the S2b.N1 scripts" % mode, "fins")
            continue
        any_part = True
        fin_mod, steady_want, fins_byte = expect[mode]
        all_rows = []
        for name in names:
            w = sess.window(name)
            part_rows = sess.rows_in(w)
            if not part_rows:
                C.row(rows, "S5a.FINS", "MISSING", "no swim rows", "per-tick rows", "S5a fins %s: rows of %s" % (mode, name), "fins")
                continue
            all_rows.extend(part_rows)
            rep = W.replay(part_rows)
            C.row(rows, "S5a.FINS", "PASS" if rep["pass"] else "FAIL",
                  "rows %d local %d link %d maxVelDiff %.2g" % (rep["rows"], rep["localMismatch"], rep["linkMismatch"], rep["maxVelDiff"]),
                  "0 mismatches", "S5a fins %s: %s replays exactly in swim_replay.py (the row's fin bits drive the model)" % (mode, name), "fins",
                  first=rep["first"][:3])
            fins_ok = _share(part_rows, lambda r: abs(W.from_bits(r["fin"]) - fin_mod) < 1e-9 if "fin" in r else fin_mod == 0.0)
            C.row(rows, "S5a.FINS", "PASS" if fins_ok >= 0.99 else "FAIL", _pct(fins_ok), ">= 99 % of the rows",
                  "S5a fins %s: the rows' fin modifier is %.1f m/s (%s)" % (mode, fin_mod, name), "fins")
            n1 = W.n1_report(part_rows)
            bad = [c[0] for c in n1["checks"] if not c[3]]
            C.row(rows, "S5a.FINS", "PASS" if (n1["checks"] and not bad) else ("MISSING" if not n1["checks"] else "FAIL"),
                  "%d checks, %d red" % (len(n1["checks"]), len(bad)), "every metric within 5 %",
                  "S5a fins %s: S2b.N1 metrics of %s against the open-loop model (measured = SE's vSe)" % (mode, name), "fins",
                  red=bad[:4])
            fwd = next((c for c in n1["checks"] if c[0].startswith("forward steady")), None)
            if fwd is not None:
                ok = abs(fwd[1] - steady_want) <= REL_TOL * steady_want
                C.row(rows, "S5a.FINS", "PASS" if ok else "FAIL", "%.3f m/s (model %.3f)" % (fwd[1], fwd[2]),
                      "%.3f m/s +-5 %%" % steady_want,
                      "S5a fins %s: measured forward steady speed = Subnautica's %s (%s)" % (
                          mode, "(7.6 + %.1f) x 0.75" % fin_mod if fin_mod else "7.6 x 0.75", name), "fins")
        if all_rows:
            w0 = sess.window(names[0])
            w1 = sess.window(names[-1])
            smp = [s for s in sess.samples if w0 and w1 and w0[0] <= s["tick"] <= w1[1]]
            if smp:
                share = _share(smp, lambda s: s.get("fins") == fins_byte)
                C.row(rows, "S5a.FINS", "PASS" if share >= SHARE else "FAIL", _pct(share), ">= 90 % of the C1 samples",
                      "S5a fins %s: the C1 extension's fins byte is %d" % (mode, fins_byte), "fins")
    if not any_part:
        return
    ev = sess.ev("fins")
    C.row(rows, "S5a.FINS", "INFO", "; ".join("%s -> %s" % (e.get("value"), "ok" if (e.get("reply") or {}).get("ok") else "refused")
                                                 for e in ev)[:60] or "none", "ultra then off",
          "de.swim fins commands acknowledged", "fins", required=False)

# ------------------------------------------------------------------------------------------------ o2
def evaluate_o2(sess, rows):
    if "o2" not in sess.parts:
        return
    steps = [e for e in sess.ev("o2_step") if str(e.get("label", "")).startswith("o2.")]
    if not steps:
        C.row(rows, "S5a.O2", "MISSING", "no o2_step event", "1.0, 0.2, 0.05, auto", "S5a o2: the override steps", "o2")
        return
    for e in steps:
        v = e.get("value")
        name = "o2.%s" % (v if isinstance(v, str) else ("%g" % v))
        w = sess.window(name)
        smp = sess.samples_in(w, SETTLE_S)
        if not smp:
            C.row(rows, "S5a.O2", "MISSING", "no C1 samples", ">= 10 samples", "S5a o2 %s: C1 extension samples" % name, "o2")
            continue
        if isinstance(v, (int, float)):
            want = frac_byte(float(v))
            share = _share(smp, lambda s: abs(s.get("o2", -9) - want) <= 1 and (s.get("flags", 0) & FLAG_O2_OVERRIDE))
            C.row(rows, "S5a.O2", "PASS" if share >= SHARE else "FAIL", "%s of %d samples" % (_pct(share), len(smp)),
                  "byte %d +-1, override flag" % want,
                  "S5a o2 override %g: the C1 o2 byte mirrors the override (round(v x 255)) with the O2Override flag" % v, "o2",
                  seen=sorted({s.get("o2") for s in smp})[:6])
        else:
            clear = _share(smp, lambda s: not (s.get("flags", 0) & FLAG_O2_OVERRIDE))
            C.row(rows, "S5a.O2", "PASS" if clear >= SHARE else "FAIL", _pct(clear), ">= 90 % flag clear",
                  "S5a o2 auto: the O2Override flag is clear again", "o2")
            # the byte follows the suit: the dumps of the window carry o2Level (0..1)
            levels = []
            for r in sess.replies("o2.auto"):
                rep = r.get("reply")
                if isinstance(rep, dict) and isinstance(rep.get("o2Level"), (int, float)):
                    levels.append(float(rep["o2Level"]))
            if levels:
                want = frac_byte(levels[-1])
                last = smp[-max(1, len(smp) // 4):]
                share = _share(last, lambda s: abs(s.get("o2", -9) - want) <= O2_SUIT_TOL)
                C.row(rows, "S5a.O2", "PASS" if share >= SHARE else "FAIL", "%s (byte %s vs suit %d)" % (
                    _pct(share), sorted({s.get("o2") for s in last})[:4], want), "byte = o2Level x 255 +-%d" % O2_SUIT_TOL,
                      "S5a o2 auto: the byte follows the suit's own level (the dump's o2Level)", "o2")
            else:
                C.row(rows, "S5a.O2", "MISSING", "no dump with o2Level in the auto window", "a de.swim dump",
                      "S5a o2 auto: the byte follows the suit's own level", "o2")
    values = sorted({s.get("o2") for e in steps for s in sess.samples_in(sess.window(
        "o2.%s" % (e.get("value") if isinstance(e.get("value"), str) else "%g" % e.get("value"))), SETTLE_S)})
    C.row(rows, "S5a.O2", "PASS" if len(values) >= 3 else "FAIL", "%d distinct byte values" % len(values), ">= 3",
          "S5a o2: the mirrored byte moved across the steps (S5a.M2 'stuck' is red here)", "o2")

# ------------------------------------------------------------------------------------------------ arms
def _segments_of(sess, label):
    ev = next((e for e in sess.ev("swim_script") if e.get("label") == label), None)
    if ev is None:
        return None
    t0 = ev.get("startTick") if isinstance(ev.get("startTick"), int) else ev.get("tick")
    if not isinstance(t0, int):
        return None
    out, t = [], t0
    for seg in ev.get("segments") or []:
        n = int(round(float(seg.get("seconds", 0)) * TICK_HZ))
        out.append((seg.get("label"), t, t + n - 1, (float(seg.get("forward", 0)), float(seg.get("strafe", 0)),
                                                       float(seg.get("vertical", 0)))))
        t += n
    return out

def evaluate_arms(sess, rows):
    if "arms" not in sess.parts:
        return
    w = sess.window("arms")
    smp = sess.samples_in(w, SETTLE_S)
    if not smp:
        C.row(rows, "S5a.ARMS", "MISSING", "no C1 samples in the arms window", ">= 10 samples", "S5a arms: C1 extension samples", "arms")
        return
    share = _share(smp, lambda s: (s["flags"] & FLAG_IN_WATER) and (s["flags"] & FLAG_UNDERWATER) and s["mode"] == MODE_SWIM)
    C.row(rows, "S5a.ARMS", "PASS" if share >= SHARE else "FAIL", "%s of %d" % (_pct(share), len(smp)), ">= 90 %",
          "S5a arms: InWater + Underwater flags and mode Swim while the script swims at -20 m", "arms")
    share = _share(smp, lambda s: s["flags"] & FLAG_CONTROLS)
    C.row(rows, "S5a.ARMS", "PASS" if share >= SHARE else "FAIL", _pct(share), ">= 90 %",
          "S5a arms: Controls flag (the model sets the velocity)", "arms")
    share = _share(smp, lambda s: s["flags"] & FLAG_JETPACK_SUPPRESSED)
    C.row(rows, "S5a.ARMS", "PASS" if share >= SHARE else "FAIL", _pct(share), ">= 90 %",
          "S5a arms: JetpackSuppressed flag with the head under", "arms")
    segs = _segments_of(sess, "arms")
    if not segs:
        C.row(rows, "S5a.ARMS", "MISSING", "no swim_script event for 'arms'", "4 segments", "S5a arms: the script's segments", "arms")
    else:
        axes = {"moveF": 0, "moveS": 1, "moveV": 2}
        for label, a, b, move in segs:
            a2 = a + int(round(0.3 * TICK_HZ))
            seg_smp = [s for s in sess.samples if a2 <= s["tick"] <= b]
            if not seg_smp:
                C.row(rows, "S5a.ARMS", "MISSING", "no samples", "samples", "S5a arms %s: C1 samples" % label, "arms")
                continue
            drive = any(abs(m) > 0 for m in move)
            share = _share(seg_smp, lambda s: bool(s["flags"] & FLAG_MOVING) == drive)
            C.row(rows, "S5a.ARMS", "PASS" if share >= SHARE else "FAIL", _pct(share), ">= 90 %",
                  "S5a arms %s: Moving flag %s" % (label, "set" if drive else "clear (release)"), "arms")
            if drive:
                def signs_ok(s, move=move):
                    for k, idx in axes.items():
                        want = move[idx]
                        got = s.get(k, 0)
                        if want > 0 and got < 64 or want < 0 and got > -64 or want == 0 and got != 0:
                            return False
                    return True
                share = _share(seg_smp, signs_ok)
                C.row(rows, "S5a.ARMS", "PASS" if share >= SHARE else "FAIL", _pct(share), ">= 90 %",
                      "S5a arms %s: the move axes carry the input (forward %+g strafe %+g vertical %+g)" % ((label,) + move), "arms")
                last = [s for s in seg_smp if s["tick"] >= b - int(TICK_HZ)]
                key = "speedV" if move[2] != 0 else "speedH"
                share = _share(last, lambda s: s.get(key, 0) >= SPEED_FRAC_MIN * 255)
                C.row(rows, "S5a.ARMS", "PASS" if (last and share >= SHARE) else ("MISSING" if not last else "FAIL"),
                      "%s (%s %s)" % (_pct(share), key, sorted({s.get(key) for s in last})[-3:] if last else "-"),
                      "%s >= %.0f %% of 255 in the last second" % (key, 100 * SPEED_FRAC_MIN),
                      "S5a arms %s: the speed fraction of the held axis" % label, "arms")
    # out of the water
    wo = sess.window("arms.out")
    out_smp = sess.samples_in(wo)
    if not out_smp:
        C.row(rows, "S5a.ARMS", "MISSING", "no arms.out samples", "samples after the teleport", "S5a arms.out: InWater clears out of the water", "arms")
    else:
        first_clear = next((s["tick"] for s in out_smp if not (s["flags"] & FLAG_IN_WATER)), None)
        within = first_clear is not None and first_clear - wo[0] <= OUT_WITHIN_S * TICK_HZ
        # the judged second starts when the flag clears, not after a settle: SE alone has no shore at the site, so the
 # teleported engineer falls back into the water about 1.1 s later; the arms must hide during that second
        tail = [] if first_clear is None else [s for s in out_smp if first_clear <= s["tick"] < first_clear + OUT_WITHIN_S * TICK_HZ]
        share = _share(tail, lambda s: not (s["flags"] & FLAG_IN_WATER) and s["mode"] == MODE_WALK and not (s["flags"] & FLAG_MOVING))
        C.row(rows, "S5a.ARMS", "PASS" if within and tail and share >= SHARE else "FAIL",
              "clear after %s ticks; %s of the next %.0f s" % ("-" if first_clear is None else first_clear - wo[0], _pct(share), OUT_WITHIN_S),
              "<= %.0f s; >= 90 %%" % OUT_WITHIN_S,
              "S5a arms.out: InWater clear (mode Walk, not Moving) once the engineer is out of the water, for the second after it clears (he falls back in: no shore): the arms hide", "arms")

# ------------------------------------------------------------------------------------------------ choke and the SN join
def evaluate_choke(sess, rows):
    if "choke" not in sess.parts:
        return
    w = sess.window("choke")
    smp = sess.samples_in(w, SETTLE_S)
    if not smp:
        C.row(rows, "S5a.CHOKE", "MISSING", "no C1 samples", "samples", "S5a choke: C1 extension samples", "choke")
        return
    want = frac_byte(0.05)
    share = _share(smp, lambda s: abs(s.get("o2", -9) - want) <= 1 and (s["flags"] & FLAG_O2_OVERRIDE))
    C.row(rows, "S5a.CHOKE", "PASS" if share >= SHARE else "FAIL", _pct(share), "byte %d +-1, override flag" % want,
          "S5a choke: the C1 o2 byte holds 0.05 with the override flag for the hold", "choke")
    if not sess.sn:
        C.row(rows, "S5a.CHOKE", "OTHER", "no SN log", "--sn-log", "S5a choke: the SN plugin's breath/choke events (needs the Subnautica log)",
              "choke", required=False)
        return
    br = sess.sn_in("Breath", w)
    kinds = sorted({str(d.get("event")) for d in br})
    ok = any(k in ("lowO2", "choke") for k in kinds)
    C.row(rows, "S5a.CHOKE", "PASS" if ok else "FAIL", "events %s" % (",".join(kinds) or "none"), "lowO2 or choke in the window",
          "S5a choke: Subnautica's own low-O2 alert / choke fired from the mirrored oxygen (DE.Breath)", "choke")
    o2l = sess.sn_in("O2", w)
    o2l = [d for d in o2l if d["tick"] >= w[0] + SETTLE_S * TICK_HZ]
    if not o2l:
        C.row(rows, "S5a.CHOKE", "MISSING", "no DE.O2 line in the window", ">= 1 per second", "S5a choke: DE.O2 mirror lines", "choke")
    else:
        share = _share(o2l, lambda d: isinstance(d.get("mirrored"), float) and abs(d["mirrored"] - 0.05) <= 0.02 and
                       d.get("override") == 1 and d.get("shielded") == 1 and
                       isinstance(d.get("snOxygen"), float) and isinstance(d.get("capacity"), float) and
                       abs(d["snOxygen"] - d["mirrored"] * d["capacity"]) <= 1.0)
        C.row(rows, "S5a.CHOKE", "PASS" if share >= SHARE else "FAIL", "%s of %d lines" % (_pct(share), len(o2l)),
              "mirrored 0.05 +-0.02, snOxygen = mirrored x capacity +-1, override 1, shielded 1",
              "S5a choke: DE.O2 shows the OxygenManager mirrored from C1 with the ghost's health shielded", "choke")
    pda = [d for d in sess.sn if d["kind"] == "PDA"]
    played = sum(int(d.get("played") or 0) for d in pda)
    C.row(rows, "S5a.CHOKE", "PASS" if pda and played == 0 else ("MISSING" if not pda else "FAIL"),
          "%d lines, played %d, suppressed %d" % (len(pda), played, sum(int(d.get("suppressed") or 0) for d in pda)),
          "played 0", "S5a choke: PDA voice lines stay suppressed (DE.PDA played=0)", "choke")

def evaluate_sn(sess, rows):
    if not sess.sn:
        C.row(rows, "S5a.SN", "OTHER", "no SN log given", "--sn-log", "S5a sn: the SN plugin's lines joined by C1 tick "
              "(the orchestrator runs the SN side with the s2 recipe and passes its log)", "sn", required=False)
        return
    ghost = [d for d in sess.sn if d["kind"] == "Ghost" and str(d.get("event") or "") in GHOST_EVENTS]
    C.row(rows, "S5a.SN", "PASS" if not ghost else "FAIL", "%d lines" % len(ghost), "0 lines",
          "S5a sn: no DE.Ghost damage/death/moved/saved line (CONTRACT §1: nothing in Subnautica touches the ghost)", "sn",
          lines=[d["raw"][:80] for d in ghost[:3]])
    sw = [d for d in sess.sn if d["kind"] == "SwimWire"]
    vers = sorted({int(d.get("version") or 0) for d in sw})
    C.row(rows, "S5a.SN", "PASS" if vers == [C1_SWIM_VERSION] else ("MISSING" if not sw else "FAIL"), "versions %s" % vers,
          "[%d]" % C1_SWIM_VERSION, "S5a sn: the SN plugin read the C1 swim extension version 1 (DE.SwimWire)", "sn")
    if "arms" in sess.parts:
        w = sess.window("arms")
        arms = [d for d in sess.sn_in("Arms", w) if d["tick"] >= w[0] + SETTLE_S * TICK_HZ] if w else []
        if not arms:
            C.row(rows, "S5a.SN", "MISSING", "no DE.Arms line in the arms window", ">= 1 per second", "S5a sn: DE.Arms while swimming", "sn")
        else:
            share = _share(arms, lambda d: d.get("visible") == 1 and d.get("state") == "swim")
            C.row(rows, "S5a.SN", "PASS" if share >= SHARE else "FAIL", "%s of %d" % (_pct(share), len(arms)), ">= 90 % visible=1 state=swim",
                  "S5a sn: the arms are visible and in the swim state while the engineer swims under water", "sn")
            gaps = max((b["tick"] - a["tick"] for a, b in zip(arms, arms[1:])), default=0) / TICK_HZ
            C.row(rows, "S5a.SN", "PASS" if gaps <= SN_LINE_GAP_S and len(arms) >= 3 else "FAIL", "max gap %.2f s, %d lines" % (gaps, len(arms)),
                  "<= %.0f s" % SN_LINE_GAP_S, "S5a sn: DE.Arms at least every second (0.5 s contract)", "sn")
        wo = sess.window("arms.out")
        out = [d for d in sess.sn_in("Arms", wo) if d["tick"] >= wo[0] + OUT_WITHIN_S * TICK_HZ] if wo else []
        if out:
            share = _share(out, lambda d: d.get("visible") == 0 and d.get("state") == "out")
            C.row(rows, "S5a.SN", "PASS" if share >= SHARE else "FAIL", _pct(share), ">= 90 % visible=0 state=out",
                  "S5a sn: the arms hide out of the water", "sn")
        else:
            C.row(rows, "S5a.SN", "MISSING", "no DE.Arms line after the teleport out", "lines", "S5a sn: the arms hide out of the water", "sn")
    if "o2" in sess.parts:
        for e in sess.ev("o2_step"):
            v = e.get("value")
            if not isinstance(v, (int, float)):
                continue
            w = sess.window("o2.%g" % v)
            lines = [d for d in sess.sn_in("O2", w) if d["tick"] >= w[0] + SETTLE_S * TICK_HZ] if w else []
            if not lines:
                C.row(rows, "S5a.SN", "MISSING", "no DE.O2 line", "lines", "S5a sn: DE.O2 during o2 override %g" % v, "sn")
                continue
            share = _share(lines, lambda d: isinstance(d.get("mirrored"), float) and abs(d["mirrored"] - float(v)) <= 0.02 and
                           isinstance(d.get("snOxygen"), float) and isinstance(d.get("capacity"), float) and
                           abs(d["snOxygen"] - d["mirrored"] * d["capacity"]) <= 1.0 and d.get("shielded") == 1)
            C.row(rows, "S5a.SN", "PASS" if share >= SHARE else "FAIL", _pct(share), ">= 90 %",
                  "S5a sn: DE.O2 mirrored %g into the OxygenManager (snOxygen = mirrored x capacity), shielded" % v, "sn")

def evaluate_common(sess, rows):
    dis = [x for x in sess.lines if _DISABLED in x]
    C.row(rows, "S5a.ALL", "PASS" if not dis else "FAIL", "%d line(s)" % len(dis), "0 lines",
          "no `%s` line" % _DISABLED, "all", lines=dis[:3])
    errs = [x for x in sess.lines if any(k in x for k in _ERROR_LINES)]
    C.row(rows, "S5a.ALL", "PASS" if not errs else "FAIL", "%d line(s)" % len(errs), "0 lines",
          "no `DE.Swim error`, `DE.Swim depth MISMATCH` or `(S2b patch off)` line", "all", lines=errs[:3])
    if sess.samples:
        share = _share(sess.samples, lambda s: s.get("version") == C1_SWIM_VERSION)
        C.row(rows, "S5a.ALL", "PASS" if share >= 0.999 else "FAIL", "%s of %d samples" % (_pct(share), len(sess.samples)),
              "version 1", "C1 swim extension version byte (0 = an SE build before S5a)", "all")
    else:
        C.row(rows, "S5a.ALL", "MISSING", "no c1swim sample", "samples", "C1 swim extension samples", "all")
    C.row(rows, "S5a.ALL", "PASS" if fnv1a64(c1_swim_text()) == C1_SWIM_HASH else "FAIL", "0x%016x" % fnv1a64(c1_swim_text()),
          "0x%016x" % C1_SWIM_HASH, "the C1 swim extension table of this analyser hashes to the pinned value (SwimWireTests)", "all")
    muts = sess.start.get("mutants") or []
    seen = sorted({m for x in sess.lines for m in re.findall(r"S5a\.M\d+", x)})
    C.row(rows, "S5a.ALL", "INFO", "mutants %s; in log %s" % (",".join(muts) or "none", ",".join(seen) or "none"),
          "none for a clean run", "seeded failures requested / seen in the SE log", "all", required=False)
    C.row(rows, "S5a.ALL", "INFO", "sn logs %d (%d lines)" % (len(sess.sn_paths), len(sess.sn)), "--sn-log",
          "the Subnautica log joined by C1 tick", "all", required=False)

def evaluate(folder, sn_logs=()) -> list:
    sess = Session(folder, sn_logs)
    rows = []
    if not sess.session:
        C.row(rows, "S5a", "MISSING", "no s5a-session.jsonl", "a launcher s5a session", "session log", "all")
        return rows
    for fn in (evaluate_fins, evaluate_o2, evaluate_arms, evaluate_choke, evaluate_sn, evaluate_common):
        try:
            fn(sess, rows)
        except Exception as e:     # an analyser bug must not hide the other rows
            C.row(rows, "S5a.%s" % fn.__name__.split("_")[-1].upper(), "FAIL", "analyser error %s" % type(e).__name__,
                  "-", str(e)[:120], "all")
    return rows

# =============================================================================================== stand-in session
N1_SCRIPTS = (
    ("n1a", (("n1.fwd", 10.0, (0, 0, 1), False), ("n1.rel1", 5.0, (0, 0, 0), False),
             ("n1.strafe", 5.0, (1, 0, 0), False), ("n1.rel2", 5.0, (0, 0, 0), False))),
    ("n1b", (("n1.up", 3.0, (0, 1, 0), False), ("n1.down", 3.0, (0, -1, 0), False),
             ("n1.rel3", 3.0, (0, 0, 0), False), ("n1.back", 5.0, (0, 0, -1), False),
             ("n1.rel5", 4.0, (0, 0, 0), False), ("n1.sprint", 5.0, (0, 0, 1), True),
             ("n1.rel4", 5.0, (0, 0, 0), False))),
)
ARMS_SCRIPT = (("arms.fwd", 3.0, (0, 0, 1), False), ("arms.strafe", 3.0, (1, 0, 0), False),
               ("arms.up", 3.0, (0, 1, 0), False), ("arms.rel", 3.0, (0, 0, 0), False))
O2_STEPS = (1.0, 0.2, 0.05, "auto")

class SynthS5a:
    """A deterministic stand-in of the SE plugin + launcher for --selftest: swim_replay's Sim runs the model (the
 plugin's rows), the C1 extension is composed as SwimWireFill does, the session timeline is the launcher's. Knobs:
 mutants {"M1", "M2", "M3"} (S5a.M1-M3 as the plugin seeds them), sn (write an SN log in the contract's format),
 sn_defects {"nochoke", "pda", "visible", "damage"}."""

    QPCF = 10_000_000

    def __init__(self, mutants=(), parts=("fins", "o2", "arms", "choke"), sn=False, sn_defects=(), fins_default="ultra"):
        self.mut = set(mutants)
        self.parts = list(parts)
        self.sn_on, self.sn_defects = sn, set(sn_defects)
        self.tick = 1000
        self.session, self.se, self.sn = [], [], []
        self.fins = fins_default
        self.o2_override = None
        self.suit = 1.0
        self.eye = -3000.0
        self.lat = False
        self.frozen = None
        self.o2_frozen = None
        self.last_sample_tick = None
        self.pda_last = 0
        self.sn_last = {"Arms": -10 ** 9, "O2": -10 ** 9}
        self.sn_state = None
        self.low_sent = False

    # ----------------------------------------------------------------------------------------------- plumbing
    def qpc(self):
        return 5_000_000_000 + self.tick * self.QPCF // 60

    def utc(self):
        import datetime
        t = datetime.datetime(2026, 10, 16, 12, 0, 0) + datetime.timedelta(seconds=self.tick / 60.0)
        return t.isoformat(timespec="milliseconds") + "Z"

    def ev(self, _ev, **kw):
        rec = {"utc": self.utc(), "qpc": self.qpc(), "ev": _ev}
        rec.update(kw)
        self.session.append(rec)

    def log(self, msg):
        self.se.append({"t": self.utc(), "ev": "log", "msg": msg})

    def part(self, name, phase):
        self.ev("part", name=name, phase=phase, tick=self.tick)

    def reply(self, label, args, rep):
        self.ev("reply", side="se", cmd="de.swim", args=args, reply=rep, label=label, tick=self.tick)

    def dump(self, label):
        self.reply(label, {"dump": True}, {"ok": True, "state": "swim" if self.lat else "out", "o2Level": self.suit,
                                           "fins": self.fins, "o2Override": self.o2_override})

    # ----------------------------------------------------------------------------------------------- the plugin
    def wire(self, active, moving, move, vel, max_speed, mode, controls):
        flags = 0
        if active:
            if self.lat:
                flags |= FLAG_IN_WATER
            if self.eye < 0:
                flags |= FLAG_UNDERWATER
            if mode == MODE_SURFACE:
                flags |= FLAG_AT_SURFACE
            if moving:
                flags |= FLAG_MOVING
            if self.lat and self.eye < 0:
                flags |= FLAG_JETPACK_SUPPRESSED
            if controls:
                flags |= FLAG_CONTROLS
        ref = max_speed * 0.75 if controls else 0.0
        h = math.sqrt(vel[0] ** 2 + vel[2] ** 2)
        d = {"version": 1, "flags": flags, "mode": mode if active else 0, "fins": FINS_BYTE[self.fins],
             "speedH": frac_byte(h / ref) if ref else 0, "speedV": frac_byte(abs(vel[1]) / ref) if ref else 0,
             "moveF": int(round(move[2] * 127)) if active else 0, "moveS": int(round(move[0] * 127)) if active else 0,
             "moveV": int(round(move[1] * 127)) if active else 0,
             "o2": frac_byte(self.o2_override) if self.o2_override is not None else (frac_byte(self.suit) if active else 255),
             "dc": 1 if self.eye < -0.1 else 0, "vy": int(round(max(-1.0, min(1.0, vel[1] / ref)) * 127)) if ref else 0}
        if self.o2_override is not None:
            d["flags"] |= FLAG_O2_OVERRIDE
        if "M2" in self.mut:
            if self.o2_frozen is None:
                self.o2_frozen = d["o2"]
            d["o2"] = self.o2_frozen
            d["flags"] &= ~FLAG_O2_OVERRIDE
        if "M3" in self.mut and active:
            if self.frozen is None:
                self.frozen = dict(d)
            live = dict(d)
            d = dict(self.frozen)
            d["o2"], d["fins"], d["version"] = live["o2"], live["fins"], live["version"]
            d["flags"] = (d["flags"] & ~FLAG_O2_OVERRIDE) | (live["flags"] & FLAG_O2_OVERRIDE)
        return d

    def sample(self, d):
        rec = {"ev": "c1swim", "utc": self.utc(), "qpc": self.qpc(), "tick": self.tick, "seq": self.tick * 2}
        rec.update(d)
        self.session.append(rec)

    def sn_lines(self, d):
        """The SN plugin's lines of this tick from the C1 extension it read (the contract's format)."""
        if not self.sn_on:
            return
        if self.sn_state is None:
            # the S1 ghost rule's informational line every real SN log carries (no event=: never red)
            self.sn.append("[Info   :DeepEngineers.SN] DE.Ghost setup: EnterLockedMode(null) mode=LockedPiloting frozenStats=True")
            self.sn.append("DE.SwimWire tick=%d version=%d flags=0x%02x mode=%d fins=%d speedH=%d speedV=%d move=%d,%d,%d o2=%d dc=%d vy=%d" % (
                self.tick, d["version"], d["flags"], d["mode"], d["fins"], d["speedH"], d["speedV"], d["moveF"], d["moveS"],
                d["moveV"], d["o2"], d["dc"], d["vy"]))
        inw = bool(d["flags"] & FLAG_IN_WATER)
        state = "out" if not inw else "fly" if d["mode"] == MODE_FLY else "surface" if d["flags"] & FLAG_AT_SURFACE else "swim"
        visible = 1 if (inw and d["flags"] & FLAG_UNDERWATER) else 0
        if "visible" in self.sn_defects:
            visible = 1
        if self.tick - self.sn_last["Arms"] >= 30 or state != self.sn_state:
            self.sn_last["Arms"] = self.tick
            self.sn_state = state
            self.sn.append("[Info   :DeepEngineers] DE.Arms tick=%d state=%s visible=%d moving=%d move=%.3f,%.3f,%.3f speedH=%.3f "
                           "speedV=%.3f vy=%.3f fins=%s anim=%s" % (
                               self.tick, state, visible, 1 if d["flags"] & FLAG_MOVING else 0, d["moveF"] / 127.0,
                               d["moveS"] / 127.0, d["moveV"] / 127.0, d["speedH"] / 255.0, d["speedV"] / 255.0,
                               d["vy"] / 127.0, self.fins, "swim_fwd" if d["flags"] & FLAG_MOVING else "swim_idle"))
        mirrored = d["o2"] / 255.0
        if self.tick - self.sn_last["O2"] >= 30:
            self.sn_last["O2"] = self.tick
            self.sn.append("[Info   :DeepEngineers] DE.O2 tick=%d mirrored=%.3f snOxygen=%.1f capacity=%.1f override=%d shielded=1" % (
                self.tick, mirrored, mirrored * 45.0, 45.0, 1 if d["flags"] & FLAG_O2_OVERRIDE else 0))
        if mirrored < 0.2 and not self.low_sent:
            self.low_sent = True
            if "nochoke" not in self.sn_defects:
                self.sn.append("DE.Breath tick=%d event=lowO2 o2=%.3f depth=%.1f" % (self.tick, mirrored, -self.eye))
                self.sn.append("DE.Breath tick=%d event=choke o2=%.3f depth=%.1f" % (self.tick + 6, mirrored, -self.eye))
        if mirrored >= 0.2:
            self.low_sent = False
        if self.tick - self.pda_last >= 3600:
            self.pda_last = self.tick
            self.sn.append("DE.PDA tick=%d suppressed=3 played=%d window=60" % (self.tick, 1 if "pda" in self.sn_defects else 0))
        if "damage" in self.sn_defects and self.tick % 600 == 0:
            self.sn.append("DE.Ghost tick=%d event=damage amount=5" % self.tick)

    def idle(self, seconds, sampling=False):
        # the controller runs a full tick (Active) with the eye below +5 m (SwimController.ArmBelowY); above it, idle
        for _ in range(int(round(seconds * 60))):
            self.tick += 1
            active = self.eye < 5.0
            d = self.wire(active, False, (0, 0, 0), (0, 0, 0), 0.0, MODE_SWIM if self.lat else MODE_WALK, False)
            if sampling and self.tick % 3 == 0:
                self.sample(d)
            self.sn_lines(d)

    def swim_script(self, label, segs, sampling=False):
        """Runs a de.swim script through swim_replay's model (rows every tick), composes the C1 extension per tick."""
        body = [{"label": s[0], "seconds": s[1], "forward": float(s[2][2]), "strafe": float(s[2][0]),
                 "vertical": float(s[2][1]), "sprint": bool(s[3])} for s in segs]
        self.ev("swim_script", label=label, segments=body, seconds=sum(s[1] for s in segs), tick=self.tick,
                startTick=self.tick + 1, supported=True)
        fins_mod = W.FINS[self.fins]
        sim = W.Sim(self.eye, 0, "fins" if "M1" in self.mut else None, tick0=self.tick + 1, fins=fins_mod)
        sim.s.lat, sim.s.ctl, sim.s.md, sim.s.eu = True, True, W.SWIM, True   # already swimming (latched since the teleport)
        max_speed = 0.0
        for lab, secs, move, spr in segs:
            sim.move, sim.spr = move, spr
            n = int(round(secs * 60))
            for _ in range(n):
                sim.run(1)
                self.tick += 1
                row = dict(sim.rows[-1])
                row["seg"] = lab
                row["src"], row["step"], row["vSe"] = "script", 0, list(row["vel"])
                self.se.append(dict(row, t=self.utc()))
                if int(row["o_st"]):
                    max_speed = W.from_bits(row["o_max"])
                vel = tuple(W.from_bits(row[k]) for k in ("o_vx", "o_vy", "o_vz"))
                self.eye = sim.p[1]
                d = self.wire(True, any(m != 0 for m in move), move, vel, max_speed, MODE_SWIM, True)
                if sampling and self.tick % 3 == 0:
                    self.sample(d)
                self.sn_lines(d)
        self.lat = True

    def teleport(self, eye, label):
        self.eye = eye
        self.lat = eye < -0.1
        self.reply(label, {"teleportChar": [0, eye - 1.7, 0]}, {"ok": True})

    # ----------------------------------------------------------------------------------------------- the launcher
    def session_run(self):
        self.ev("start", parts=self.parts, name="DeepEngineersBridge", qpcFrequency=self.QPCF, seAlone=True,
                mutants=["S5a." + m for m in sorted(self.mut)], snLog=self.sn_on, standin={"se": False})
        self.log("DE.Swim config: on (selftest stand-in)")
        for m in sorted(self.mut):
            self.log("DE.Mutant S5a.%s ON (selftest stand-in)" % m)
        self.idle(1.0)
        self.dump("session.check")
        if "fins" in self.parts:
            for mode in ("ultra", "off"):
                self.fins = mode
                self.ev("fins", label="fins.%s" % mode, value=mode, reply={"ok": True, "line": "DE.Swim fins " + mode}, tick=self.tick)
                for name, segs in N1_SCRIPTS:
                    self.teleport(-50.0, "fins.%s.%s.arrive" % (mode, name))
                    self.lat = True
                    self.idle(3.0)
                    pname = "fins.%s.%s" % (mode, name)
                    self.part(pname, "begin")
                    self.swim_script(pname, segs, sampling=True)
                    self.part(pname, "end")
                    self.dump(pname + ".after")
            self.fins = "ultra"
            self.ev("fins", label="fins.restore", value="ultra", reply={"ok": True}, tick=self.tick)
        if "o2" in self.parts:
            self.teleport(-20.0, "o2.arrive")
            self.idle(2.0)
            for v in O2_STEPS:
                lab = "o2.%s" % (v if isinstance(v, str) else "%g" % v)
                self.o2_override = None if v == "auto" else float(v)
                self.ev("o2_step", label=lab, value=v, tick=self.tick, reply={"ok": True})
                self.part(lab, "begin")
                self.idle(4.0, sampling=True)
                self.dump(lab + ".dump")
                self.part(lab, "end")
        if "arms" in self.parts:
            self.teleport(-20.0, "arms.arrive")
            self.lat = True
            self.idle(2.0)
            self.part("arms", "begin")
            self.swim_script("arms", ARMS_SCRIPT, sampling=True)
            self.part("arms", "end")
            self.part("arms.out", "begin")
            self.teleport(3.7, "arms.out.arrive")
            self.idle(3.0, sampling=True)
            self.part("arms.out", "end")
        if "choke" in self.parts:
            self.teleport(-20.0, "choke.arrive")
            self.lat = True
            self.idle(2.0)
            self.part("choke", "begin")
            self.o2_override = 0.05
            self.ev("o2_step", label="choke", value=0.05, tick=self.tick, reply={"ok": True})
            self.idle(8.0, sampling=True)
            self.o2_override = None
            self.ev("o2_step", label="choke.auto", value="auto", tick=self.tick, reply={"ok": True})
            self.part("choke", "end")
        self.ev("end", rc=None)
        return self

    def write(self, folder):
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, "s5a-session.jsonl"), "w", encoding="utf-8") as f:
            for r in self.session:
                f.write(json.dumps(r) + "\n")
        with open(os.path.join(folder, "se-20261016-120000.jsonl"), "w", encoding="utf-8") as f:
            for r in self.se:
                f.write(json.dumps(r) + "\n")
        if self.sn_on:
            with open(os.path.join(folder, "sn-standin.log"), "w", encoding="utf-8") as f:
                for line in self.sn:
                    f.write(line + "\n")
        return folder

# =============================================================================================== selftest
def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-84s %s %s" % (label, "PASS" if cond else "FAIL", str(detail)[:140]))

    def bad(rows):
        return [r for r in rows if r.get("required", True) and r["status"] != "PASS"]

    def why(rows):
        return "; ".join("%s %s %s | %s" % (x["id"], x["status"], str(x["measured"])[:30], x["what"][:50]) for x in rows[:4])

    def rows_of(name, **kw):
        folder = SynthS5a(**kw).session_run().write(os.path.join(root, name))
        return evaluate(folder)

    root = tempfile.mkdtemp(prefix="de_s5a_")
    try:
        check("C1 swim table: 12 bytes 0x94-0x9f in offset order, the canonical text hashes to the pinned value",
              [o for _n, o, _c in C1_SWIM] == list(range(0x94, 0xA0)) and fnv1a64(c1_swim_text()) == C1_SWIM_HASH,
              "0x%016x" % fnv1a64(c1_swim_text()))
        rec = bytearray(0x100)
        d = {"version": 1, "flags": FLAG_IN_WATER | FLAG_MOVING | FLAG_O2_OVERRIDE, "mode": 1, "fins": 2, "speedH": 191,
             "speedV": 3, "moveF": 127, "moveS": -64, "moveV": 0, "o2": 13, "dc": 2, "vy": -12}
        rec[0x94:0xA0] = encode_c1_swim(d)
        check("C1 swim codec round trip (signed axes, flags)", decode_c1_swim(bytes(rec)) == d)
        check("fraction byte: 0.05 -> 13, 0.2 -> 51, 0.5 -> 128, 1 -> 255", (frac_byte(0.05), frac_byte(0.2), frac_byte(0.5), frac_byte(1.0)) == (13, 51, 128, 255))
        line = parse_sn_line("[Info   :DeepEngineers] DE.Arms tick=1234 state=swim visible=1 moving=1 move=1.000,0.000,-0.500 speedH=0.750 speedV=0.000 vy=0.000 fins=ultra anim=swim_fwd")
        check("SN line parser: DE.Arms key=value with the move triple", line and line["kind"] == "Arms" and line["tick"] == 1234 and
              line["move"] == (1.0, 0.0, -0.5) and line["speedH"] == 0.75 and line["fins"] == "ultra", str(line)[:100])
        tmp = os.path.join(root, "sn.jsonl")
        with open(tmp, "w", encoding="utf-8") as f:
            f.write('{"t":"x","msg":"DE.O2 tick=77 mirrored=0.051 snOxygen=2.3 capacity=45.0 override=1 shielded=1"}\n')
            f.write("DE.SwimWire tick=78 version=1 flags=0x51 mode=1 fins=2 speedH=191 speedV=0 move=127,0,0 o2=13 dc=1 vy=0\n")
        lines = read_sn_log([tmp])
        check("read_sn_log: JSONL msg and plain lines; hex flags", len(lines) == 2 and lines[0]["mirrored"] == 0.051 and lines[1]["flags"] == 0x51)

        # ---------------------------------------------------------------- green
        rows = rows_of("green", sn=True)
        check("green stand-in with an SN log: every required row PASS (%d rows)" % len(rows), not bad(rows), why(bad(rows)))
        ids = {r["id"] for r in rows}
        check("  rows for every part: FINS, O2, ARMS, CHOKE, SN, ALL", {"S5a.FINS", "S5a.O2", "S5a.ARMS", "S5a.CHOKE", "S5a.SN", "S5a.ALL"} <= ids, sorted(ids))
        fins_rows = [r for r in rows if r["id"] == "S5a.FINS" and "forward steady" in r["what"]]
        check("  fins: ultra measured 8.1 and off 5.70 (the n1a forward segment of each run)", len(fins_rows) == 2 and
              all(("8.1" in r["threshold"]) == ("ultra" in r["what"]) for r in fins_rows),
              "; ".join(r["measured"] for r in fins_rows))
        rows = rows_of("green-se-only", sn=False)
        check("green stand-in without an SN log: PASS, SN rows OTHER (not required)", not bad(rows) and
              all(r["status"] == "OTHER" and not r["required"] for r in rows if r["id"] == "S5a.SN"), why(bad(rows)))
        rows = rows_of("green-fins-only", parts=("fins",), sn=False)
        check("fins part alone: PASS, no O2/ARMS/CHOKE rows", not bad(rows) and not any(r["id"] in ("S5a.O2", "S5a.ARMS", "S5a.CHOKE") for r in rows), why(bad(rows)))

        # ---------------------------------------------------------------- mutants
        rows = rows_of("m1", mutants=("M1",), sn=True)
        red = bad(rows)
        check("S5a.M1 fins not applied: FINS red (replay mismatch, ultra steady 5.70 instead of 8.1), the rest green",
              red and all(r["id"] == "S5a.FINS" for r in red) and any("8.1" in r["threshold"] for r in red) and
              any("replays exactly" in r["what"] for r in red), why(red))
        rows = rows_of("m2", mutants=("M2",), sn=True)
        red = bad(rows)
        check("S5a.M2 O2 stuck: O2 and CHOKE red (byte never moves, no override flag), FINS/ARMS green",
              red and {r["id"] for r in red} <= {"S5a.O2", "S5a.CHOKE", "S5a.SN"} and any(r["id"] == "S5a.O2" for r in red) and
              any(r["id"] == "S5a.CHOKE" for r in red), why(red))
        rows = rows_of("m3", mutants=("M3",), sn=True)
        red = bad(rows)
        check("S5a.M3 flags frozen: >= 2 ARMS rows red (the flags of the first tick everywhere), FINS/O2 green",
              red and sum(1 for r in red if r["id"] == "S5a.ARMS") >= 2 and
              not any(r["id"] in ("S5a.FINS", "S5a.O2") for r in red), why(red))

        # ---------------------------------------------------------------- SN defects
        rows = rows_of("nochoke", sn=True, sn_defects=("nochoke",))
        red = bad(rows)
        check("SN log without the lowO2/choke event: CHOKE red", red and all(r["id"] == "S5a.CHOKE" for r in red) and
              any("DE.Breath" in r["what"] for r in red), why(red))
        rows = rows_of("pda", sn=True, sn_defects=("pda",))
        red = bad(rows)
        check("SN log with a PDA line played: CHOKE red (played != 0)", red and all("PDA" in r["what"] for r in red), why(red))
        rows = rows_of("visible", sn=True, sn_defects=("visible",))
        red = bad(rows)
        check("SN log with the arms visible out of the water: SN red", red and all(r["id"] == "S5a.SN" and "hide" in r["what"] for r in red), why(red))
        rows = rows_of("damage", sn=True, sn_defects=("damage",))
        red = bad(rows)
        check("SN log with a DE.Ghost damage line: SN red", red and all(r["id"] == "S5a.SN" and "DE.Ghost" in r["what"] for r in red), why(red))

        # ---------------------------------------------------------------- empty folder
        empty = os.path.join(root, "empty")
        os.makedirs(empty)
        rows = evaluate(empty)
        check("an empty folder: MISSING, not an exception", rows and rows[0]["status"] == "MISSING")
        text = C.table(rows)
        check("the table renders", "check" in text and "MISSING" in text)
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("S5A SELFTEST %s (%d checks, %d failed)" % ("PASS" if ok else "FAIL", len(results), results.count(False)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S5a oracle over a launcher s5a session folder")
    ap.add_argument("folder", nargs="?")
    ap.add_argument("--sn-log", action="append", default=[], help="the Subnautica plugin's log of the same session (repeatable)")
    ap.add_argument("--lenient", action="store_true", help="MISSING rows do not fail")
    ap.add_argument("--json")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.folder:
        ap.print_help()
        return 2
    rows = evaluate(a.folder, a.sn_log)
    print(C.table(rows))
    ok = C.verdict(rows, not a.lenient)
    print("S5A ORACLE %s" % ("PASS" if ok else "FAIL"))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump({"tool": "analyze_s5a", "folder": a.folder, "ok": ok, "rows": rows}, f, indent=1, default=str)
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
