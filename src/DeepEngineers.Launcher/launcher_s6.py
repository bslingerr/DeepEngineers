"""`launcher.py s6` (ROADMAP S6 "Collision B + combat"; CONTRACT §2.6, §2.7, §4, §5.4 "S6 combat conventions").
Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`): the
session machinery (preflight, start_games, the in-world wait, the C11 harness, close_session, collect) is launcher.py's.

 python -I src/DeepEngineers.Launcher/launcher.py s6 --mode S6a|S6se|S6b [--parts...] [--mutants S6.M1,...|

 python -I src/DeepEngineers.Launcher/launcher.py s6 --dry-run (no --mode: the plan of all three)
 python -I src/DeepEngineers.Launcher/launcher.py s6 --list-mutant-groups | --selftest | --analyze tests/out/s6/<stamp>

Modes (one launch of each game the mode names):
 S6a Subnautica + the stand-in SE (tests/standins/fake_se_producer.py --s6 --s2): a synthetic hull with its
 collision skin (bt1 = 20 x 6 x 6 m, 50 boxes, 2 sections, looping at 5 m/s; bt1k = 1,000 boxes; rotated;
 hull100; rotor) streamed as SE would, the forwarded hits drained from C8 into the stand-in's s6.c8 rows.
 Parts selftest, g1e3, bt1, n3, n4, n5, n6, n7, teardown (all by default).
 S6se SE alone (Legacy.exe -continue on the Survival throwaway copy --world, default "DE Lab Survival", made by
 tools/lab/make_survival_copy.py --destructible --keep-bottles --point-last-session) + the stand-in Subnautica
 host (tests/standins/fake_sn_host.py --s1 --s3 --s6 --damage-rate 0): grids placed by SE's de.s3 place (a 3 x 3
 wall of LargeBlockArmorBlock, then of SmallBlockArmorBlock: the S3 lab keeps one grid at a time), their
 SkinSet decoded by the host, the host's damage plans written to C8 as Subnautica would. Parts skin, n1, n10,
 teardown (all by default).
 S6b both games on the same Survival copy: selftest, n7 (before any creature is spawned), bt1e (a boneshark near
 the swimming SE character, then a reaper spawned 30 m from a real large grid's proxy), n3 (peepers around that
 grid), n5, n6, teardown by default
 behind a rock) and n8 (a rotor at 10 rpm with a small-grid head; --rotor-at x,y,z when one already stands in
 the copy).
Part aliases: n2 and n9 = selftest (S6.N2 Freedom gate and S6.N9 self-check come from the same part).
Combat parts (bt1, n3, n4, n5, n6, bt1e, n8) are refused when Subnautica's Freedom gate fails (S6.N2: de.s6 status after
the selftest part); the session logs combat_refused and the analyzer shows S6.N2 red.

Every command and reply goes into s6-session.jsonl; C7 (fixedDeltaTime, flags, capabilities, damageDropped,
damageOverflowDepth) is sampled at 10 Hz during every part ("c7" events with the part and the seconds since its start);
both sides' s6.* rows are tailed while the session runs (spawn acknowledgements, skins, applied ids); spawns are
labelled <part>.spawn.<TechType> so the analyzer tells "no hit" (FAIL) from "no spawn" (MISSING). Output
tests/out/s6/<stamp>/ (s6-session.jsonl, se-*/sn-*.jsonl or the stand-ins' *-standin.jsonl, the game logs) and the S6
ORACLE TABLE of tests/s6/analyze_s6.py (s6-table.txt, s6-results.json). Games are never piped and only closed by PID
(CloseMainWindow; the stand-ins by TerminateProcess). This launcher never writes into a game folder, a save or SE's
LastSession: the world copies are the orchestrator's (tools/lab/make_survival_copy.py, SE closed)."""
from __future__ import annotations

import datetime
import json
import math
import os
import subprocess
import sys
import threading
import time

S6_MODES = ("S6a", "S6se", "S6b")
S6_PARTS = {"S6a": ("selftest", "g1e3", "bt1", "n3", "n4", "n5", "n6", "n7", "teardown"),
            "S6se": ("skin", "n1", "n10", "teardown"),
            "S6b": ("selftest", "n7", "bt1e", "n3", "n4", "n8", "n5", "n6", "teardown")}
S6_DEFAULT_PARTS = {"S6a": S6_PARTS["S6a"], "S6se": S6_PARTS["S6se"],
                    "S6b": ("selftest", "n7", "bt1e", "n3", "n5", "n6", "teardown")}
OWNER_PARTS = ("n4", "n8")
PART_ALIASES = {"n2": "selftest", "n9": "selftest"}
COMBAT_PARTS = ("bt1", "n3", "n4", "n5", "n6", "bt1e", "n8")
SN_MUTANTS = ("S6.M1", "S6.M2", "S6.M5", "S6.M8", "S6.M9", "S6.M10", "S6.M11", "S6.M12")
SE_MUTANTS = ("S6.M3", "S6.M7")
WORLD_MUTANTS = ("S6.M4", "S6.M6")
ALL_MUTANTS = SN_MUTANTS + SE_MUTANTS + WORLD_MUTANTS
MUTANT_MODES = {"S6.M1": ("S6a",), "S6.M2": ("S6a",), "S6.M5": ("S6a",), "S6.M9": ("S6a",), "S6.M10": ("S6a", "S6b"),
                "S6.M12": ("S6a", "S6b"), "S6.M6": ("S6a", "S6b"), "S6.M8": ("S6b",), "S6.M11": ("S6b",),
                "S6.M3": ("S6se",), "S6.M7": ("S6se",), "S6.M4": ("S6se",)}
# runbook groups (one launch each): the mutant, the mode and parts that show it, the rows that must turn red
S6_MUTANT_GROUPS = {
    "triggers": {"mutants": ("S6.M1",), "mode": "S6a", "parts": ("selftest", "bt1"), "red": ("S6.B-T1.1",)},
    "candeal": {"mutants": ("S6.M2",), "mode": "S6a", "parts": ("selftest", "bt1"), "red": ("S6.B-T1.2",)},
    "dedup": {"mutants": ("S6.M3",), "mode": "S6se", "parts": ("skin", "n1", "teardown"), "red": ("S6.N1.dup",)},
    "creative": {"mutants": ("S6.M4",), "mode": "S6se", "parts": ("skin", "n1", "teardown"),
                 "red": ("S6.N1.character",), "world": "DE Lab S6 Creative"},
    "frame": {"mutants": ("S6.M5",), "mode": "S6a", "parts": ("g1e3",), "red": ("S6.G1-E3",)},
    "hostcreative": {"mutants": ("S6.M6",), "mode": "S6a", "parts": ("selftest",), "red": ("S6.N2",),
                     "hostSlot": True},
    "swap": {"mutants": ("S6.M7",), "mode": "S6se", "parts": ("skin", "n1", "teardown"),
             "red": ("S6.N1.small", "S6.N1.large")},
    "playercapsule": {"mutants": ("S6.M8",), "mode": "S6b", "parts": ("selftest", "bt1e", "teardown"),
                      "red": ("S6.B-T1e.character",)},
    "sync": {"mutants": ("S6.M9",), "mode": "S6a", "parts": ("g1e3",), "red": ("S6.G1-E3",)},
    "radius": {"mutants": ("S6.M10",), "mode": "S6a", "parts": ("selftest", "n5", "teardown"), "red": ("S6.N5",)},
    "rootframe": {"mutants": ("S6.M11",), "mode": "S6b", "parts": ("selftest", "n8", "teardown"), "red": ("S6.N8",),
                  "owner": True},
    "forwarder": {"mutants": ("S6.M12",), "mode": "S6a", "parts": ("selftest",), "red": ("S6.N9",)},
}
SURVIVAL_WORLD = "DE Lab Survival"
CREATIVE_WORLD = "DE Lab S6 Creative"
SHALLOW = (0.0, 0.0)              # Safe Shallows anchor (SN x, z): seabed -17.212 m (S4 real run)
SEABED_FALLBACK = -17.212
PARK = (24.0, -8.0, 0.0)          # the real SE character parked in open water after the harness attaches (as s5/tp1)
BT1 = {"centre": (0.0, -8.0, 25.0), "radius": 15.0, "speed": 5.0, "camera": (0.0, -6.0, -14.0, 0.0),
       "seconds": 180.0, "baseline": 30.0, "dist": 15.0, "kSeconds": 60.0,
       "spawn": {"Peeper": 20, "BoneShark": 1, "ReaperLeviathan": 1}}
N3 = {"seconds": 60.0, "hz": 2.0, "count": 20}
N4 = {"spot": (0.0, -60.0), "bury": 1.5, "sharks": 2, "wait": 150.0}
NEAR_HULL = {"sn": (0.0, -8.0, 12.0), "camera": (0.0, -8.0, -4.0, 0.0), "dist": 12.0,  # n5/n6: the camera looks at it
 # integrator S6: a slow loop (S6a) - the crashfish's AggressiveWhenSeeTarget.minimumVelocity (0.2 m/s on the
 # Crash prefab) rejects a target whose Rigidbody reports less;
 # 20 m hull + 2 m loop stays 4 m short of the camera, the spawn ray passes within 2 m of the hull centre
             "speed": 1.0, "radius": 2.0}
N5 = {"tech": "Crash", "count": 3, "wait": 75.0}
N6 = {"tech": "Shocker", "count": 1, "wait": 75.0}
N7 = {"seconds": 10.0, "awayM": 400.0}
REFILL_S = 20.0                  # S6b: de.swim refill every 20 s and after every teleportChar
GRID_SPOT = (0.0, -10.0, 40.0)    # S6b: the real grid (a 3 x 3 wall placed by de.s3), SN centre
ROTOR_SPOT = (-30.0, -8.0, 40.0)
# charAway: the character leaves the creatures for open water near the surface over the grandReef (seabed ~ -235 m)
BT1E = {"reaperDist": 30.0, "sharkDist": 12.0, "wait": 150.0, "charAway": (-1029.0, -5.0, -1116.0)}
N1 = {"unique": 30, "dupsA": {"large": 4, "character": 3}, "dupsB": {"small": 3}, "wait": 45.0}
G1E3 = {"count": 200, "samples": 400, "samplesQuick": 100}
PLATE = {"rows": 3, "cols": 3, "orientation": "wall"}
QUICK = 0.25                      # --quick: holds and waits scaled by this

# ----------------------------------------------------------------------------------------------------------------
# helpers
# ----------------------------------------------------------------------------------------------------------------

def analyzer(LCH):
    p = os.path.join(LCH.REPO, "tests", "s6")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s6   # noqa: E402
    return analyze_s6

def s6_stream(LCH):
    p = os.path.join(LCH.REPO, "tests", "standins")
    if p not in sys.path:
        sys.path.insert(0, p)
    import s6_stream   # noqa: E402
    return s6_stream

def wire_ref(LCH):
    p = os.path.join(LCH.REPO, "tools", "worldmap")
    if p not in sys.path:
        sys.path.insert(0, p)
    import wire_ref as W   # noqa: E402
    return W

def _int_id(v):
    if v is None or isinstance(v, bool):
        return None
    if isinstance(v, int):
        return v & 0xFFFFFFFFFFFFFFFF
    if isinstance(v, float):
        return int(v)
    s = str(v).strip()
    try:
        return (int(s, 16) if s.lower().startswith("0x") else int(s)) & 0xFFFFFFFFFFFFFFFF
    except ValueError:
        return None

def world_check(LCH, name, want_mode, force_unmarked=False):
    """Read-only facts of an SE world copy and the problems that refuse a session on it (S6se / S6b: Survival with
 destructible blocks; the creative group: Creative with destructible blocks): folder, LastSession, GameMode,
 DestructibleBlocks, autosave."""
    import re
    info = LCH.s4_world_info(name)
    probs = []
    if not info.get("folder"):
        return info, [info.get("error") or "world %r not found" % name]
    if name.strip().lower() == "de lab":
        probs.append("--world is the lab world itself: S6 damages blocks, run it on a throwaway copy")
    if info.get("lastSession") != os.path.basename(info["folder"]):
        probs.append("LastSession.sbl points at %r, not %r (tools/lab/make_survival_copy.py --point-last-session)" % (
            info.get("lastSession"), name))
    try:
        with open(os.path.join(info["folder"], "Sandbox_config.sbc"), encoding="utf-8-sig", errors="replace") as f:
            txt = f.read()
    except OSError as e:
        return info, probs + ["Sandbox_config.sbc unreadable: %s" % e]
    m = re.search(r"<GameMode>([^<]*)</GameMode>", txt)
    info["gameMode"] = m.group(1) if m else None
    m = re.search(r"<DestructibleBlocks>([^<]*)</DestructibleBlocks>", txt)
    info["destructibleBlocks"] = (m.group(1).strip().lower() == "true") if m else None
    if info["gameMode"] != want_mode:
        probs.append("GameMode %s, this session needs %s" % (info["gameMode"], want_mode))
    if info["destructibleBlocks"] is False:
        probs.append("DestructibleBlocks false (make_survival_copy.py --destructible)")
    # review F15: a combat session damages blocks; it runs only on a marked throwaway copy with autosave off
    marked = os.path.exists(os.path.join(info["folder"], "DE_THROWAWAY.txt"))
    info["marked"] = marked
    if not marked and not force_unmarked:
        probs.append("no DE_THROWAWAY.txt marker in %s (a throwaway copy from make_survival_copy.py, or --force-unmarked)" % info["folder"])
    if info.get("autosaveMinutes") not in (0, None):
        if force_unmarked:
            info["note"] = "autosave %s min: the damaged blocks may be saved into the copy (it is a throwaway)" % info.get("autosaveMinutes")
        else:
            probs.append("autosave %s min: make the copy with --autosave-off (or --force-unmarked)" % info.get("autosaveMinutes"))
    return info, probs

def host_slot_check(LCH, slot):
    """The Subnautica host slot for S6.M6 (read-only): exists, and its gameinfo.json game mode when readable."""
    d = os.path.join(LCH.sn_saves_dir(), slot)
    if not os.path.isdir(d):
        return {"dir": d}, ["host slot %s not found under %s" % (slot, LCH.sn_saves_dir())]
    info = {"dir": d}
    try:
        with open(os.path.join(d, "gameinfo.json"), encoding="utf-8-sig") as f:
            gi = json.load(f)
        info["gameMode"] = gi.get("gameMode")
    except (OSError, ValueError):
        info["gameMode"] = None
    return info, []

class C7Sampler:
    """10 Hz "c7" events while a part is set: the part, the seconds since its begin and the S6 fields of C7."""

    def __init__(self, LCH, name, sess):
        self.LCH, self.name, self.sess = LCH, name, sess
        self.part, self.t0 = None, None
        self.stop_ev = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)

    def start(self):
        self.thread.start()
        return self

    def stop(self):
        self.stop_ev.set()
        self.thread.join(3.0)

    def set(self, part):
        self.part, self.t0 = part, time.monotonic()

    def run(self):
        L, S = self.LCH.L, self.LCH.S
        sec = None
        try:
            while not self.stop_ev.is_set():
                part = self.part
                if part is None:
                    self.stop_ev.wait(0.05)
                    continue
                if sec is None:
                    sec = S.Section.open(self.name, readonly=True)
                    if sec is None or sec.mapped_size < L.SECTION_SIZE:
                        sec = None
                        self.stop_ev.wait(0.5)
                        continue
                _st, _q, d = S.seqlock_read(sec, L.REGION["C7"].offset, L.SN_STATUS.size)
                if d:
                    v = L.SN_STATUS.unpack(d)
                    self.sess.event("c7", part=part, sinceS=round(time.monotonic() - (self.t0 or 0), 3),
                                    fixedDeltaTime=round(v["fixedDeltaTime"], 6), flags=v["flags"],
                                    capabilities=v["capabilities"], damageDropped=v["damageDropped"],
                                    damageOverflowDepth=v["damageOverflowDepth"], snFrame=v["snFrame"])
                self.stop_ev.wait(0.1)
        except Exception as e:      # a sampler never takes the session down
            sys.stderr.write("c7 sampler stopped: %r\n" % (e,))
        finally:
            if sec:
                sec.close()

class RowTail:
    """The s6.* rows appended since the session start to one side's JSONL (read-only): a stand-in's stdout file in
 the session folder, or the plugin's se-*.jsonl / sn-*-<pid>.jsonl in %LOCALAPPDATA%\\DeepEngineers\\logs."""

    def __init__(self, AN, paths=None, pattern=None, t_start=0.0):
        self.AN, self.paths, self.pattern, self.t_start = AN, paths, pattern, t_start
        self.offsets, self.partial, self.rows = {}, {}, []

    def poll(self):
        import glob
        files = self.paths or [p for p in sorted(glob.glob(self.pattern or "")) if os.path.getmtime(p) >=
                               self.t_start - 5]
        for p in files:
            try:
                with open(p, "rb") as f:
                    f.seek(self.offsets.get(p, 0))
                    data = f.read()
                    self.offsets[p] = f.tell()
            except OSError:
                continue
            if not data:
                continue
            text = self.partial.pop(p, "") + data.decode("utf-8", errors="replace")
            lines = text.split("\n")
            if not text.endswith("\n"):
                self.partial[p] = lines.pop()
            recs = []
            for ln in lines:
                ln = ln.strip()
                if ln.startswith("{"):
                    try:
                        recs.append(json.loads(ln))
                    except ValueError:
                        pass
            self.rows.extend((n, d) for n, d, _st in self.AN.parse_rows(recs))
        return len(self.rows)

    def mark(self):
        return self.poll()

    def since(self, mark, name=None):
        self.poll()
        return [d for n, d in self.rows[mark:] if name is None or n == name]

# ----------------------------------------------------------------------------------------------------------------
# the runner
# ----------------------------------------------------------------------------------------------------------------

class S6Runner:
    """The scripted S6 parts through the C11 harness, one table per peer in the syntax each takes. An action refused as unknown is not sent again (its rows stay MISSING)."""

    SE_ACTIONS = {
        "status": ("de.s6", lambda: {"status": True}),
        "skin": ("de.s6", lambda eid, rows: {"skin": eid, "rows": bool(rows)}),
        "resend": ("de.s6", lambda eid: {"resend": eid}),
        "inject": ("de.s6", lambda dmg, apply: {"damage": dmg, "apply": bool(apply)}),
        "selftest": ("de.s6", lambda: {"selftest": True}),
        "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
        "place": ("de.s3", lambda sn, block, label: {"place": {"sn": list(sn), "yawDeg": 0.0, "block": block,
                                                               "plate": dict(PLATE), "label": label}}),
        "remove": ("de.s3", lambda: {"remove": True}),
        # the stand-in producer only (S6a)
        "hull": ("de.s6", lambda skin, sn, yaw, speed, radius: {"action": "spawn", "skin": skin, "sn": list(sn),
                                                                 "yawDeg": float(yaw), "speedMps": float(speed),
                                                                 "radiusM": float(radius)}),
        "motion": ("de.s6", lambda speed, radius: {"action": "motion", "speedMps": float(speed),
                                                   "radiusM": float(radius)}),
        "hullTeardown": ("de.s6", lambda: {"action": "teardown"}),
        "pose": ("de.s6", lambda: {"action": "pose"}),
    }
    STANDIN_ONLY = ("hull", "motion", "hullTeardown", "pose")
    SN_ACTIONS = {
        "status": ("de.s6", lambda: {"action": "status"}),
        "selftest": ("de.s6", lambda: {"action": "selftest"}),
        "bt1": ("de.s6", lambda secs, spawn, dist, base: {"action": "bt1", "seconds": float(secs), "spawn": dict(spawn),
                                                           "dist": float(dist), "baselineSeconds": float(base)}),
        "probe": ("de.s6", lambda wm1, count, repose: {"action": "probe", "wm1": wm1, "count": int(count),
                                                       "repose30": bool(repose)}),
        "paths": ("de.s6", lambda tech, secs, hz: {"action": "paths", "techType": tech, "seconds": float(secs),
                                                   "hz": float(hz)}),
        "penetration": ("de.s6", lambda secs: {"action": "penetration", "seconds": float(secs)}),
        "spawn": ("de.spawn", lambda tech, n, dist: {"techType": tech, "count": int(n), "dist": float(dist)}),
        "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
        "teardown": ("de.s3", lambda: {"action": "teardown"}),
        "s6teardown": ("de.s6", lambda: {"action": "teardown"}),   # review F1: creatures and local proxies of a combat part
        "count": ("de.s3", lambda: {"action": "count"}),
        # the stand-in host only (S6se)
        "skins": ("de.s6", lambda: {"action": "skins"}),
        "damage": ("de.s6", lambda mode, targets, unique, dups, seed: {"action": "damage", "mode": mode,
                                                                       "targets": targets, "unique": int(unique),
                                                                       "dups": dups, "seed": int(seed)}),
    }

    def __init__(self, LCH, log, a, sess, sn, se, out, stamp, t_start):
        self.LCH, self.L, self.S = LCH, LCH.L, LCH.S
        self.log, self.a, self.sess, self.sn, self.se, self.out, self.stamp = log, a, sess, sn, se, out, stamp
        self.mode = a.mode
        self.q = a.quick
        self.sec = self.S.Section.open(a.name)
        self.h = self.S.Harness(self.sec) if self.sec else None
        self.c7 = C7Sampler(LCH, a.name, sess).start()
        AN = analyzer(LCH)
        self.se_tail = RowTail(AN, paths=[os.path.join(out, "se-standin.jsonl")] if self.mode == "S6a" else None,
                               pattern=os.path.join(LCH.LOG_DIR, "se-*.jsonl"), t_start=t_start)
        self.sn_tail = RowTail(AN, paths=[os.path.join(out, "sn-standin.jsonl")] if self.mode == "S6se" else None,
                               pattern=os.path.join(LCH.LOG_DIR, "sn-*-%d.jsonl" % sn.pid) if sn else None,
                               t_start=t_start)
        self.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
        self._last_refill = 0.0
        self.unsupported = set()
        self.gate_ok = None
        self.grid = None            # (entityId, kind) of the S3 grid SE placed last
        self.grid_sn = None         # its block (0, 0, 0) centre, SN
        self.hull = None            # the stand-in hull's spawn reply (S6a)

 # ---- plumbing -------------------------------------------------------------------------------------------------
    def alive(self):
        return (self.se is None or self.se.poll() is None) and (self.sn is None or self.sn.poll() is None)

    def attach(self):
        if self.h is None:
            return False, "section absent"
        ok, why = self.h.attach()
        if not ok:
            return False, why
        for side in ("se", "sn"):
            rep = None
            for _ in range(10):         # SE re-syncs its C11a tail at attach: repeat the ping (ROADMAP 5.10)
                rep = self.call(side, "de.ping", {}, timeout=1.0)
                if rep is not None:
                    break
            if rep is None:
                return False, "%s does not answer C11 (dev build needed)" % side.upper()
        return True, ""

    def close(self):
        self.c7.stop()
        if self.h:
            self.h.detach()
        if self.sec:
            self.sec.close()

    def scale(self, seconds, floor=2.0):
        return max(floor, seconds * QUICK) if self.q else seconds

    def wait(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end and self.alive():
            self.h.poll_replies()
            self.keep_breathing()
            time.sleep(min(0.05, max(0.0, end - time.monotonic())))

    def call(self, side, cmd, args=None, timeout=None, label=None):
        t0 = time.monotonic()
        q0 = self.S.qpc()
        rid, st = self.h.send(side, cmd, args or {})
        self.stats["sent"] += 1
        rep = self.h.wait_reply(rid, timeout or self.a.cmd_timeout) if st == self.S.RW_OK else None
        self.sess.event("reply", side=side, cmd=cmd, args=args or {}, id=rid, reply=rep, qpc=q0, label=label,
                        ms=round((time.monotonic() - t0) * 1000, 1))
        if rep is None:
            self.stats["noReply"] += 1
        elif rep.get("ok"):
            self.stats["ok"] += 1
        else:
            self.stats["failed"] += 1
            if "unknown" in str(rep.get("error", "")).lower() and cmd not in self.stats["unknown"]:
                self.stats["unknown"].append(cmd)
        return rep

    @staticmethod
    def ok(rep):
        return bool(rep) and bool(rep.get("ok"))

    def act(self, side, action, *args, label=None, timeout=None):
        key = (side, action)
        if key in self.unsupported:
            return None
        table = self.SE_ACTIONS if side == "se" else self.SN_ACTIONS
        cmd, build = table[action]
        rep = self.call(side, cmd, build(*args), timeout=timeout, label=label or "%s.%s" % (side, action))
        if rep is not None and not rep.get("ok") and "unknown" in str(rep.get("error", "")).lower():
            self.unsupported.add(key)
            self.log.say("  S6: %s does not take %s (%s): its rows will be MISSING" % (side.upper(), action,
                                                                                    rep.get("error")))
        return rep

    def part(self, name, phase):
        self.sess.event("part", name=name, phase=phase, qpc=self.S.qpc())
        self.c7.set(name if phase == "begin" else None)
        self.log.say("  S6 part %s %s" % (name, phase), ev="s6_part", part=name, phase=phase)

    def owner(self, step, prompt, seconds):
        if not getattr(self.a, "owner", False):
            self.sess.event("owner", step=step, phase="skipped", prompt=prompt)
            self.log.say("  S6 owner step %s skipped (no --owner): %s" % (step, prompt), ev="s6_owner_skipped")
            return False
        self.log.say("  OWNER (%d s): %s" % (seconds, prompt), ev="owner_prompt", step=step)
        self.sess.event("owner", step=step, phase="begin", prompt=prompt)
        self.wait(seconds)
        self.sess.event("owner", step=step, phase="end")
        return True

    def teleport(self, x, y, z, yaw=0.0, label=None):
        """SE's camera (the stand-in's spectator camera, or the real SE's spectator) to an SN point (de.teleport)."""
        return self.call("se", "de.teleport", {"x": x, "y": y, "z": z, "yawDeg": yaw, "label": label or ""},
                         label=label)

    def swim(self, args, label):
        rep = self.call("se", "de.swim", args, label=label)
        if self.mode == "S6b" and isinstance(args, dict) and "teleportChar" in args:
            self._last_refill = time.monotonic()
            self.call("se", "de.swim", {"refill": True}, label=label + ".refill")
        return rep

    def keep_breathing(self):
        """S6-3 ("i died of o2"): in Survival the S2b oxygen rule drains the suit like Subnautica's 45 s tank
 and refills only with the head out; the scripted character stays under for minutes (it drowned during n3, SE
 respawned it 1.5 km under water, it drowned again). S6b: de.swim refill every REFILL_S seconds inside the waits
 and after every teleportChar (swim)."""
        if self.mode != "S6b":
            return
        now = time.monotonic()
        if now - self._last_refill < REFILL_S:
            return
        self._last_refill = now
        self.call("se", "de.swim", {"refill": True}, label="o2.refill")

    def spawn(self, part, tech, n, dist):
        rep = self.act("sn", "spawn", tech, n, dist, label="%s.spawn.%s" % (part, tech), timeout=10.0)
        if not self.ok(rep):
            self.log.say("  S6 %s: de.spawn %s x%d not acknowledged (%s)" % (part, tech, n, json.dumps(rep)[:160]
                                                                            if rep else "no reply"), ev="s6_spawn_failed")
        return rep

    def wait_rows(self, tail, mark, pred, timeout, label):
        """Polls a tail until pred(rows since mark) or the timeout; returns the rows."""
        t0 = time.monotonic()
        rows = []
        while time.monotonic() - t0 < timeout and self.alive():
            rows = tail.since(mark)
            if pred(rows):
                self.sess.event("waited", label=label, seconds=round(time.monotonic() - t0, 2), done=True)
                return rows
            self.wait(0.5)
        self.sess.event("waited", label=label, seconds=round(time.monotonic() - t0, 2), done=False)
        self.log.say("  S6 %s: not seen after %.0f s" % (label, timeout), ev="s6_wait_timeout", label=label)
        return rows

    def park(self):
        if self.mode == "S6a":
            return
        rep = self.swim({"teleportChar": list(PARK), "yawDeg": 0.0}, "park")
        if not self.ok(rep):
            self.log.say("  S6 park: de.swim teleportChar not acknowledged (%s)" % (json.dumps(rep)[:160] if rep else
                                                                                   "no reply"), ev="s6_park_failed")
        self.wait(2.0)

    def mutants_on(self, mutants):
        for m in mutants:
            if m in SN_MUTANTS:
                self.act("sn", "mutant", m, True, label="mutant.%s" % m)
            elif m in SE_MUTANTS:
                self.act("se", "mutant", m, True, label="mutant.%s" % m)

    def combat_allowed(self, part):
        if part not in COMBAT_PARTS or self.mode == "S6se":
            return True
        if self.gate_ok is None:
            self.check_gate("precheck")
        if self.gate_ok is False:
            self.sess.event("combat_refused", part=part, reason="Freedom gate failed (S6.N2)")
            self.log.say("  S6 part %s REFUSED: Subnautica's Freedom gate failed (S6.N2)" % part, ev="s6_refused")
            return False
        return True

    def check_gate(self, label):
        rep = self.act("sn", "status", label="%s.gate" % label, timeout=10.0)
        g = (rep or {}).get("gate") if self.ok(rep) else None
        if isinstance(g, dict) and "pass" in g:
            self.gate_ok = bool(g.get("pass"))
        elif self.ok(rep) and "freedom" in rep:
            self.gate_ok = bool(rep.get("freedom"))
        self.sess.event("gate", label=label, gate=g, ok=self.gate_ok)
        return self.gate_ok

    # ---- the stand-in hull (S6a) ----------------------------------------------------------------------------------
    def hull_spawn(self, part, skin, sn, speed=0.0, radius=15.0, yaw=0.0):
        mark = self.sn_tail.mark()
        rep = self.act("se", "hull", skin, sn, yaw, speed, radius, label="%s.hull.%s" % (part, skin), timeout=10.0)
        if not self.ok(rep):
            return None
        self.hull = rep
        root = _int_id(rep.get("rootId"))
        # Subnautica decoded the skin and built the proxies (its s6.skin / s6.proxy rows), or 20 s
        self.wait_rows(self.sn_tail, mark, lambda rows: any(
            _int_id(d.get("entity")) == root and (d.get("sections") or d.get("op") in ("create", "update"))
            for d in rows), self.scale(20.0, 5.0), "%s.proxies" % part)
        return rep

    # ---- the real SE's S3 grid (S6se, S6b) ----------------------------------------------------------------------------
    def place_grid(self, part, block, sn):
        mark = self.sn_tail.mark() if self.mode == "S6se" else self.se_tail.mark()
        rep = self.act("se", "place", sn, block, "s6.%s" % part, label="%s.place.%s" % (part, block), timeout=20.0)
        eid = _int_id((rep or {}).get("entityId")) if self.ok(rep) else None
        if eid is None:
            self.log.say("  S6 %s: de.s3 place %s failed (%s)" % (part, block, json.dumps(rep)[:200] if rep else
                                                                  "no reply"), ev="s6_place_failed")
            return None
        self.grid = (eid, block)
        self.grid_sn = tuple(sn)
        tail = self.sn_tail if self.mode == "S6se" else self.se_tail
        self.wait_rows(tail, mark, lambda rows: any(_int_id(d.get("entity")) == eid and d.get("sections") for d in rows),
                       self.scale(40.0, 10.0), "%s.skin %d" % (part, eid))
        return eid

    # ---- parts: S6a and S6b ---------------------------------------------------------------------------------------------
    def p_selftest(self):
        """S6.N2 + S6.N9: Subnautica de.s6 {action: selftest} (1 SN_HP on a test section and on the Player, exactly one
 record each, C7 bit 10), then de.s6 {action: status}: the Freedom gate (mode=Freedom invisible=false); a failed
 gate refuses the combat parts that follow"""
        self.part("selftest", "begin")
        self.act("sn", "selftest", label="selftest.run", timeout=30.0)
        if self.mode == "S6b":
            self.act("se", "selftest", label="selftest.se", timeout=15.0)
        self.wait(3.0)
        self.check_gate("selftest")
        self.part("selftest", "end")

    def p_g1e3(self):
        """S6.G1-E3: the stand-in's rotated hull as live proxies; the WM-1 box records (tests/out/wm1.jsonl, generated by
 wire_ref.py vectors when absent) probed by Subnautica's de.s6 {action: probe, count 200, repose30} after
 SyncTransforms; the bounds of wire_ref.s6_box_bound for each probed record's depth and tilt as s6bound events"""
        self.part("g1e3", "begin")
        path = ensure_wm1(self.LCH, self.log, dry=getattr(self.a, "selftest", False))
        self.sess.event("wm1", path=path)
        if self.mode == "S6a" and self.can("hull"):
            x, z = SHALLOW
            self.teleport(x, -8.0, z - 10.0, 0.0, label="g1e3.camera")
            self.hull_spawn("g1e3", "rotated", (x, -8.0, z + 15.0))
        log_bounds(self.LCH, self.sess, path, G1E3["count"], G1E3["samplesQuick" if self.q else "samples"])
        if path:
            mark = self.sn_tail.mark()
            self.act("sn", "probe", path, G1E3["count"], True, label="g1e3.probe", timeout=60.0)
            self.wait_rows(self.sn_tail, mark, lambda rows: sum(1 for d in rows if "devM" in d or "got" in d) >=
                           G1E3["count"], self.scale(120.0, 20.0), "g1e3.probe rows")
        if self.mode == "S6a" and self.can("hullTeardown"):
            self.act("se", "hullTeardown", label="g1e3.hullTeardown")
        self.part("g1e3", "end")

    def p_bt1(self):
        """S6.B-T1: the bt1 proxy (50 boxes, 2 sections) looping at 5 m/s (radius 15 m) in the Safe Shallows, Subnautica's
 de.s6 {action: bt1, seconds 180, spawn {Peeper 20, BoneShark 1, ReaperLeviathan 1}, dist 15, baselineSeconds
 30} (penetration, hit and perf rows); then bt1.k: the same loop with the 1,000-box skin and 60 s of
 de.s6 {action: penetration} (perf rows with 1,000 shapes for B-T1 (3))"""
        if not self.combat_allowed("bt1"):
            return
        c = BT1["centre"]
        cx, cy, cz, cyaw = BT1["camera"]
        self.part("bt1", "begin")
        self.teleport(cx, cy, cz, cyaw, label="bt1.camera")
        self.wait(1.0)
        if self.mode == "S6a":
            self.hull_spawn("bt1", "bt1", c, BT1["speed"], BT1["radius"])
        secs, base = self.scale(BT1["seconds"], 20.0), self.scale(BT1["baseline"], 5.0)
        self.act("sn", "bt1", secs, BT1["spawn"], BT1["dist"], base, label="bt1.spawn", timeout=30.0)
        self.wait(secs + base + 5.0)
        self.part("bt1.k", "begin")
        if self.mode == "S6a":
            self.hull_spawn("bt1.k", "bt1k", c, BT1["speed"], BT1["radius"])
        k = self.scale(BT1["kSeconds"], 10.0)
        self.act("sn", "penetration", k, label="bt1.k.penetration", timeout=10.0)
        self.wait(k + 2.0)
        self.part("bt1.k", "end")
        if self.mode != "S6se": self.act("sn", "s6teardown", label="bt1.s6teardown", timeout=10.0)   # review F1
        self.part("bt1", "end")

    def p_n3(self):
        """S6.N3: peepers (20 spawned through de.spawn) around the looping bt1 proxy (S6a) or the placed grid's proxies
 (S6b); Subnautica's de.s6 {action: paths, techType Peeper, seconds 60, hz 2}: no sampled position inside a
 section"""
        if not self.combat_allowed("n3"):
            return
        self.part("n3", "begin")
        cx, cy, cz, cyaw = BT1["camera"]
        if self.mode == "S6a":
            if not self.hull or self.hull.get("skin") != "bt1":
                self.hull_spawn("n3", "bt1", BT1["centre"], BT1["speed"], BT1["radius"])
        else:
            if self.grid is None or self.grid_sn is None:
                self.place_grid("n3", "LargeBlockArmorBlock", GRID_SPOT)
            gx, gy, gz = self.grid_sn or GRID_SPOT
            cx, cy, cz, cyaw = gx, gy, gz - 20.0, 0.0
        self.teleport(cx, cy, cz, cyaw, label="n3.camera")
        self.wait(1.0)
        self.spawn("n3", "Peeper", N3["count"], 12.0 if self.mode == "S6b" else BT1["dist"])
        secs = self.scale(N3["seconds"], 10.0)
        self.act("sn", "paths", "Peeper", secs, N3["hz"], label="n3.paths", timeout=10.0)
        self.wait(secs + 2.0)
        if self.mode != "S6se": self.act("sn", "s6teardown", label="n3.s6teardown", timeout=10.0)   # review F1
        self.part("n3", "end")

    def p_n4(self):
        """S6.N4: S6a: the stand-in's static 100 m hull (hull100, 100 boxes) with its centre buried 1.5 m under the Safe
 Shallows seabed (the octree reference height at the spot, else -17.2 m) while its upper sections stand in the
 water; two bonesharks spawned beside it; the bites on the hull's sections (the stand-in's s6.c8 rows). S6b

 a rock, then the same bonesharks (SE's s6.damage rows on that grid)"""
        if not self.combat_allowed("n4"):
            return
        x, z = N4["spot"]
        if self.mode == "S6b":
            if not self.owner("n4.hull", "build or paste a hull about 100 m long lying at SN (%g, %g) with its middle "
                                         "sunk below the seabed or behind a rock (its ends in the water); with S5 on it "
                                         "is mirrored by itself (else /de s3 mirror near)" % (x, z), 240.0):
                return
            self.part("n4", "begin")
            self.sess.event("n4_geometry", spot=[x, z], rockBetween="owner", reference="the owner's hull")
            self.teleport(x + 15.0, SEABED_FALLBACK + 8.0, z, -90.0, label="n4.camera")
            self.wait(3.0)
            mark = self.se_tail.mark()
            self.spawn("n4", "BoneShark", N4["sharks"], 8.0)
            self.wait_rows(self.se_tail, mark, lambda rows: any("applied" in d and str(d.get("kind")) in ("grid", "1")
                                                                for d in rows), self.scale(N4["wait"], 20.0),
                           "n4.bites")
            if self.mode != "S6se": self.act("sn", "s6teardown", label="n4.s6teardown", timeout=10.0)   # review F1
            self.part("n4", "end")
            return
        self.part("n4", "begin")
        oracle = None
        try:
            oracle = self.LCH.s4_oracle()
        except Exception:     # noqa: BLE001 - the reference heights are optional
            oracle = None
        seabed = self.LCH.s4_ref(oracle, x, z, SEABED_FALLBACK)
        centre_y = seabed - N4["bury"]
        origin_y = centre_y + 2.5          # hull100: the box envelope's centre is 2.5 m below its origin
        self.sess.event("n4_geometry", spot=[x, z], seabedY=seabed, hullCentreY=centre_y, hullOriginY=origin_y,
                        upperSectionRootY=origin_y + 6.25, rockBetween=seabed > centre_y,
                        reference="octrees" if oracle is not None else "fallback")
        if self.mode == "S6a":
            self.hull_spawn("n4", "hull100", (x, origin_y, z), 0.0, 0.0, 0.0)
        self.teleport(x + 12.0, seabed + 6.0, z, -90.0, label="n4.camera")      # east of the hull, looking west
        self.wait(2.0)
        mark = self.se_tail.mark()
        self.spawn("n4", "BoneShark", N4["sharks"], 8.0)
        root = _int_id((self.hull or {}).get("rootId"))
        self.wait_rows(self.se_tail, mark, lambda rows: any(_int_id(d.get("target")) == root for d in rows),
                       self.scale(N4["wait"], 20.0), "n4.bites")
        if self.mode != "S6se": self.act("sn", "s6teardown", label="n4.s6teardown", timeout=10.0)   # review F1
        self.part("n4", "end")

    def near_target(self, part):
        """n5/n6: a proxy in front of the camera (the spawn ray hits it, CONTRACT §5.4): the bt1 hull on a slow 2 m loop
 at 1 m/s (S6a; crashfish target only a Rigidbody faster than 0.2 m/s) or the placed, static grid (S6b)."""
        sx, sy, sz = NEAR_HULL["sn"]
        cx, cy, cz, cyaw = NEAR_HULL["camera"]
        if self.mode == "S6a":
            self.hull_spawn(part, "bt1", (sx, sy, sz), NEAR_HULL["speed"], NEAR_HULL["radius"], 0.0)
        else:
            # review F1: a fresh wall per combat part (a reaper bite is 80 x 25 = 2,000 of a 2,500 block: the previous
 # part's wall may be gone); the previous S3 grid is removed first
            if self.grid is not None:
                self.act("se", "remove", label="%s.removePrevious" % part, timeout=10.0)
                self.grid = None
                self.grid_sn = None
                self.wait(2.0)
            self.place_grid(part, "LargeBlockArmorBlock", (sx, sy, sz))
            gx, gy, gz = self.grid_sn or (sx, sy, sz)
            cx, cy, cz, cyaw = gx, gy, gz - (sz - NEAR_HULL["camera"][2]), 0.0     # the same 16 m south, looking north
        self.teleport(cx, cy, cz, cyaw, label="%s.camera" % part)
        self.wait(2.0)

    def p_n5(self):
        """S6.N5: crashfish spawned at the proxy in front of the camera (de.spawn Crash x3): DeepBlast records, amount
 <= 50 (x k), radius flag, one per section per blast"""
        if not self.combat_allowed("n5"):
            return
        self.part("n5", "begin")
        self.near_target("n5")
        tail = self.se_tail
        mark = tail.mark()
        self.spawn("n5", N5["tech"], N5["count"], NEAR_HULL["dist"])
        self.wait_rows(tail, mark, lambda rows: any(self.type_of(d) == 7 for d in rows), self.scale(N5["wait"], 20.0),
                       "n5.blast")
        self.wait(3.0)
        if self.mode != "S6se": self.act("sn", "s6teardown", label="n5.s6teardown", timeout=10.0)   # review F1
        self.part("n5", "end")

    def p_n6(self):
        """S6.N6: an ampeel (de.spawn Shocker) at the proxy in front of the camera: DeepShock records of 15 (x k) with the
 fallback-hit-point flag"""
        if not self.combat_allowed("n6"):
            return
        self.part("n6", "begin")
        self.near_target("n6")
        tail = self.se_tail
        mark = tail.mark()
        self.spawn("n6", N6["tech"], N6["count"], NEAR_HULL["dist"])
        self.wait_rows(tail, mark, lambda rows: any(self.type_of(d) == 8 for d in rows), self.scale(N6["wait"], 20.0),
                       "n6.shock")
        self.wait(3.0)
        if self.mode != "S6se": self.act("sn", "s6teardown", label="n6.s6teardown", timeout=10.0)   # review F1
        self.part("n6", "end")

    def p_n7(self):
        """S6.N7: C7 fixedDeltaTime at 10 Hz: n7.moving (S6a: the bt1 loop at 5 m/s near the camera; S6b: the SE
 character swimming, camera on it), n7.still (S6a: the loop stopped; S6b: n7.idle, the character idle in the
 water, information only: an idling swimmer may still move its section), n7.away (the camera 400 m from every
 proxy): 0.02 while a moving proxy is visible, 0.1 otherwise (2 s hysteresis)"""
        self.part("n7", "begin")
        secs = self.scale(N7["seconds"], 6.0)
        cx, cy, cz, cyaw = BT1["camera"]
        if self.mode == "S6a":
            self.teleport(cx, cy, cz, cyaw, label="n7.camera")
            self.hull_spawn("n7", "bt1", BT1["centre"], BT1["speed"], BT1["radius"])
            self.part("n7.moving", "begin")
            self.wait(secs)
            self.part("n7.moving", "end")
            self.act("se", "motion", 0.0, BT1["radius"], label="n7.stop")
            self.part("n7.still", "begin")
            self.wait(secs)
            self.part("n7.still", "end")
            self.act("se", "motion", BT1["speed"], BT1["radius"], label="n7.restart")
            self.teleport(cx + N7["awayM"], cy, cz, cyaw, label="n7.away")
            self.part("n7.away", "begin")
            self.wait(secs)
            self.part("n7.away", "end")
            self.teleport(cx, cy, cz, cyaw, label="n7.back")
        else:
            x, y, z = PARK
            self.swim({"teleportChar": [x, y, z], "yawDeg": 0.0}, "n7.char")
            self.swim({"camera": "third"}, "n7.camera")
            self.wait(2.0)
            self.swim({"script": [{"label": "n7.fwd", "seconds": secs + 3.0, "forward": 0.6}], "label": "n7"},
                      "n7.swim")
            self.part("n7.moving", "begin")
            self.wait(secs)
            self.part("n7.moving", "end")
            self.wait(3.5)
            self.part("n7.idle", "begin")
            self.wait(secs)
            self.part("n7.idle", "end")
            self.teleport(x + N7["awayM"], y, z, 0.0, label="n7.away")
            self.part("n7.away", "begin")
            self.wait(secs)
            self.part("n7.away", "end")
        self.part("n7", "end")

    # ---- parts: S6b only --------------------------------------------------------------------------------------------
    def p_bt1e(self):
        """S6.B-T1e: (1) a boneshark (de.spawn BoneShark, 12 m ahead of SE's third-person camera) near the swimming SE
 character: character records of 30 x k_char; then the character is moved to open water over the grandReef,
 1.5 km away (it survives); (2) a 3 x 3 wall of LargeBlockArmorBlock placed by de.s3 at SN (0, -10, 40), SE's spectator camera 3 m south of it looking
 south, a reaper spawned 30 m out: block integrity -80 x 25 under the hit point; one DE.Damage applied per hit id"""
        if not self.combat_allowed("bt1e"):
            return
        self.part("bt1e", "begin")
        x, y, z = PARK
        self.swim({"teleportChar": [x, y, z], "yawDeg": 0.0}, "bt1e.char")
        self.swim({"camera": "third"}, "bt1e.camera")
        self.wait(2.0)
        self.swim({"script": [{"label": "bt1e.swim", "seconds": 4.0, "forward": 0.3}], "label": "bt1e"}, "bt1e.swim")
        mark = self.se_tail.mark()
        self.spawn("bt1e", "BoneShark", 1, BT1E["sharkDist"])
        self.wait_rows(self.se_tail, mark, lambda rows: any(str(d.get("kind")) in ("character", "2") for d in rows if
                                                            "applied" in d or "reason" in d),
                       self.scale(BT1E["wait"], 30.0), "bt1e.character")
        ax, ay, az = BT1E["charAway"]
        self.swim({"teleportChar": [ax, ay, az], "yawDeg": 0.0}, "bt1e.charAway")
        self.wait(2.0)
        gx, gy, gz = GRID_SPOT
        eid = self.place_grid("bt1e", "LargeBlockArmorBlock", (gx, gy, gz))
        self.teleport(gx, gy, gz - 3.0, 180.0, label="bt1e.camera")
        self.wait(3.0)
        mark = self.se_tail.mark()
        self.spawn("bt1e", "ReaperLeviathan", 1, BT1E["reaperDist"])
        self.wait_rows(self.se_tail, mark, lambda rows: any(_int_id(d.get("target")) == eid for d in rows
                                                            if "applied" in d), self.scale(BT1E["wait"], 30.0),
                       "bt1e.grid")
        self.wait(5.0)
        if self.mode != "S6se": self.act("sn", "s6teardown", label="bt1e.s6teardown", timeout=10.0)   # review F1
        self.part("bt1e", "end")

    def p_n8(self):
        """S6.
 --rotor-at x,y,z names one that already stands in the copy); SE's camera beside the head, two bonesharks;
 SE's s6.damage rows with FrameSubgrid on the head under the hit point in the subgrid's frame"""
        if not self.combat_allowed("n8"):
            return
        spot = tuple(self.a.rotor_at) if getattr(self.a, "rotor_at", None) else ROTOR_SPOT
        if not getattr(self.a, "rotor_at", None):
            if not self.owner("n8.rotor", "build a large-grid rotor at SN (%g, %g, %g) with a SMALL-grid head (Add Small "
                                          "Head), a few small armor blocks on the head, and set it to 10 rpm" % spot,
                              180.0):
                return
        self.part("n8", "begin")
        self.teleport(spot[0], spot[1] + 2.0, spot[2] - 8.0, 0.0, label="n8.camera")
        self.wait(5.0)
        mark = self.se_tail.mark()
        self.spawn("n8", "BoneShark", 2, 6.0)
        self.wait_rows(self.se_tail, mark, lambda rows: any(int(d.get("flags") or 0) & 8 for d in rows
                                                            if "applied" in d or "reason" in d),
                       self.scale(150.0, 30.0), "n8.subgrid")
        self.wait(3.0)
        if self.mode != "S6se": self.act("sn", "s6teardown", label="n8.s6teardown", timeout=10.0)   # review F1
        self.part("n8", "end")

    # ---- parts: S6se (SE alone + the stand-in host) -----------------------------------------------------------------
    def p_skin(self):
        """the large grid (a 3 x 3 wall of LargeBlockArmorBlock, de.s3 place at SN (24, -8, 12)) mirrored with its
 SkinSet: the host's s6.skin rows against SE's (S6.SKIN*, information), SE de.s6 {skin, rows} and one dry-run
 de.s6 {damage, apply false} on a box centre (the cell/block SE would pick)"""
        self.part("skin", "begin")
        self.act("se", "selftest", label="skin.seSelftest", timeout=15.0)     # SE's own S6 self-check (de.s6 selftest)
        eid = self.place_grid("skin", "LargeBlockArmorBlock", (PARK[0], PARK[1], PARK[2] + 12.0))
        if eid is not None:
            rep = self.act("se", "skin", str(eid), True, label="skin.census", timeout=10.0)
            box = None
            try:
                # SE's reply (S6Commands -> CollisionSkin.CensusReply): list [{id, c, n, hash, b: [[x, y, z] <= 8]}];
 # the older detail[{boxes: [{c}]}] shape is still read (integrator S6)
                det = (rep.get("list") or rep.get("detail") or rep.get("skins", [{}])[0].get("detail") or []) \
                    if self.ok(rep) else []
                first = det[0] if det else {}
                b = (first.get("b") or [None])[0] if first.get("b") else \
                    ((first.get("boxes") or [None])[0] if first.get("boxes") else None)
                box = b.get("c") if isinstance(b, dict) else (list(b) if isinstance(b, (list, tuple)) and len(b) == 3
                                                              else None)
            except (AttributeError, IndexError, KeyError, TypeError):
                box = None
            self.act("se", "inject", {"target": str(eid), "kind": "grid", "section": "auto",
                                      "local": box or [0.0, 0.0, 0.0], "amount": 1.0, "type": 0}, False,
                     label="skin.inject.dry", timeout=10.0)
        self.part("skin", "end")

    def run_plan(self, label, mode, targets, dups, seed):
        """The host's de.s6 {action: damage}, then SE's rows until every planned id is applied or rejected."""
        mark = self.se_tail.mark()
        rep = self.act("sn", "damage", mode, targets, N1["unique"], dups, seed, label=label, timeout=10.0)
        if not self.ok(rep):
            self.log.say("  S6 %s: the host refused the plan (%s)" % (label, json.dumps(rep)[:200] if rep else "no reply"),
                         ev="s6_plan_failed")
            return None
        plan = rep.get("plan") or {}
        ids = set()
        for t in plan.get("targets") or []:
            ids |= set(t.get("ids") or [])
        for x in plan.get("list") or []:
            ids.add(x.get("id"))
        AN = analyzer(self.LCH)
        self.wait_rows(self.se_tail, mark, lambda rows: ids <= {AN.hid(d) for d in rows}, self.scale(N1["wait"], 15.0),
                       "%s: %d ids accounted" % (label, len(ids)))
        self.wait(2.0)
        return plan

    def p_n1(self):
        """S6.N1 through the stand-in host: (a) the large wall and the character (C1), 30 unique bites each + 4 and 3
 duplicates; (b) the small wall (SmallBlockArmorBlock, it replaces the large one: the S3 lab keeps one grid),
 30 bites + 3 duplicates; SE's s6.damage / s6.reject rows judged against the host's s6.plan rows"""
        self.part("n1", "begin")
        large = self.grid[0] if self.grid and self.grid[1] == "LargeBlockArmorBlock" else \
            self.place_grid("n1", "LargeBlockArmorBlock", (PARK[0], PARK[1], PARK[2] + 12.0))
        if large is not None:
            self.run_plan("n1.a", "n1", {"large": "0x%016x" % large, "character": True}, N1["dupsA"], self.a.seed)
        small = self.place_grid("n1", "SmallBlockArmorBlock", (PARK[0], PARK[1], PARK[2] + 12.0))
        if small is not None:
            self.run_plan("n1.b", "n1", {"small": "0x%016x" % small}, N1["dupsB"], self.a.seed + 1)
        self.part("n1", "end")

    def p_n10(self):
        """S6.N10: the host's 100 malformed records (old generation, old epoch, seq 0, unit, amount 0 / < 0 / > 500 /
 NaN, kind, type, unknown entity or section, frame flag, non-finite hit point) on the grid it knows and the
 character: none applied, every one rejected with its reason"""
        self.part("n10", "begin")
        if self.grid is None:
            self.place_grid("n10", "SmallBlockArmorBlock", (PARK[0], PARK[1], PARK[2] + 12.0))
        self.run_plan("n10", "n10", "auto", None, self.a.seed)
        self.part("n10", "end")

    def p_teardown(self):
        """the stand-in hull removed (S6a) or SE's S3 grid removed (de.s3 remove); Subnautica's world-loss teardown and
 count (de.s3 {action: teardown | count}) with a real Subnautica; both sides' de.s6 status (counters) for the log"""
        self.part("teardown", "begin")
        self.act("se", "status", label="teardown.seStatus", timeout=10.0)
        self.act("sn", "status", label="teardown.snStatus", timeout=10.0)
        if self.mode == "S6a":
            if self.can("hullTeardown"):
                self.act("se", "hullTeardown", label="teardown.hull")
        else:
            self.act("se", "remove", label="teardown.remove", timeout=10.0)
        self.wait(3.0)
        if self.mode != "S6se":
            self.act("sn", "s6teardown", label="teardown.s6")   # review F1: creatures + local proxies first
            self.act("sn", "teardown", label="teardown.sn")
            self.wait(2.0)
            self.act("sn", "count", label="teardown.count")
        self.part("teardown", "end")

    # ---- small helpers ------------------------------------------------------------------------------------------------
    def can(self, action):
        if action in self.STANDIN_ONLY and self.mode != "S6a":
            return False
        return ("se", action) not in self.unsupported

    @staticmethod
    def type_of(d):
        for k in ("type", "snType", "snDamageType"):
            v = d.get(k)
            if isinstance(v, (int, float)) and not isinstance(v, bool):
                return int(v)
        return {"DeepBite": 0, "DeepBlast": 7, "DeepShock": 8}.get(d.get("seType"))

# ----------------------------------------------------------------------------------------------------------------
# G1-E3 inputs
# ----------------------------------------------------------------------------------------------------------------

def wm1_path(LCH):
    return os.path.join(LCH.REPO, "tests", "out", "wm1.jsonl")

def ensure_wm1(LCH, log, dry=False):
    """The WM-1 vector file (ROADMAP 5.9: generated, never committed): tests/out/wm1.jsonl, made by
 `wire_ref.py vectors` when absent. Returns its absolute path, or None."""
    p = wm1_path(LCH)
    if os.path.exists(p) or dry:
        return p
    try:
        os.makedirs(os.path.dirname(p), exist_ok=True)
        rc = subprocess.call([sys.executable, "-I", os.path.join(LCH.REPO, "tools", "worldmap", "wire_ref.py"),
                              "vectors", p], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, timeout=600)
        if rc == 0 and os.path.exists(p):
            log.say("  S6 g1e3: WM-1 vectors written to %s" % p)
            return p
    except (OSError, subprocess.SubprocessError) as e:
        log.say("  S6 g1e3: WM-1 vectors not generated (%s)" % e)
    return None

def bound_key(depth, tilt):
    """The bucket of one probed box: depth rounded up to the next 100 m, the effective tilt min(t, 180 - t) up to the
 next 5 degrees (the corner error is symmetric about a level hull turned upside down and grows with both)."""
    eff = min(tilt, 180.0 - tilt)
    return (math.ceil(max(depth, 0.0) / 100.0) * 100.0, min(90.0, math.ceil(max(eff, 0.0) / 5.0) * 5.0))

def log_bounds(LCH, sess, path, count, samples):
    """s6bound events for the buckets of the first `count` box records of the WM-1 file (wire_ref.s6_box_bound)."""
    if not path or not os.path.exists(path):
        return 0
    try:
        W = wire_ref(LCH)
        site = W.site_by_name("v1")
    except (ImportError, AttributeError, KeyError):
        return 0
    if not hasattr(W, "s6_box_bound"):
        return 0
    keys = {bound_key(b["depth"], b["tilt"]) for b in s6_stream(LCH).wm1_boxes(path, count)}
    for depth, tilt in sorted(keys):
        b = W.s6_box_bound(site, depth, tilt, samples=samples)
        sess.event("s6bound", depth=depth, tilt=tilt, boundM=b["boundM"], worstM=b["worstM"], s=b["s"],
                   samples=samples)
    return len(keys)

# ----------------------------------------------------------------------------------------------------------------
# sessions
# ----------------------------------------------------------------------------------------------------------------

def producer_cmd(LCH, a):
    return [sys.executable, "-I", os.path.join(LCH.REPO, "tests", "standins", "fake_se_producer.py"), "--name", a.name,
            "--s6", "--s2", "--quiet", "--load-after", "1.0", "--load-seconds", "1.0", "--skin", "synth:bt1",
            "--screen", a.screen, "--fov", "%g" % a.fov]

def host_cmd(LCH, a):
    return [sys.executable, "-I", os.path.join(LCH.REPO, "tests", "standins", "fake_sn_host.py"), "--name", a.name,
            "--s1", "--s3", "--s6", "--damage-rate", "0", "--damage-seed", str(a.seed), "--quit-on-se-exit", "--quiet"]

def mutant_env(mode, mutants, a):
    """(Subnautica env, SE env) for the session's mutants."""
    sn_env = {"DEEPENGINEERS_S6": "on"}
    se_env = {"DEEPENGINEERS_S5": "on", "DEEPENGINEERS_S6": "on"}
    sn_m = [m for m in mutants if m in SN_MUTANTS]
    se_m = [m for m in mutants if m in SE_MUTANTS]
    if sn_m:
        sn_env["DEEPENGINEERS_MUTANT"] = ",".join(sn_m)
    if se_m:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(se_m)
        se_env["DEEPENGINEERS_MUTANT"] = ",".join(se_m)
    if "S6.M6" in mutants and getattr(a, "sn_host_slot", None):
        sn_env["DEEPENGINEERS_SN_HOST_SLOT"] = a.sn_host_slot
    return sn_env, se_env

def resolve(LCH, log, a, mode):
    """(parts, mutants, world, problems, needs) of one mode from the arguments and the mutant group: problems refuse
 even a dry run (exit 2);"""
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    parts = None
    world = a.world
    probs, needs = [], []
    if a.mutant_group:
        g = S6_MUTANT_GROUPS[a.mutant_group]
        mutants, parts = list(g["mutants"]), list(g["parts"])
        world = world or g.get("world")
    if a.parts.strip().lower() == "all":
        parts = list(S6_PARTS[mode])
    elif a.parts:
        parts = [PART_ALIASES.get(x.strip(), x.strip()) for x in a.parts.split(",") if x.strip()]
    if parts is None:
        parts = list(S6_DEFAULT_PARTS[mode])
        if mode == "S6b" and a.owner:
            parts[parts.index("n3") + 1:parts.index("n3") + 1] = ["n4", "n8"]
        elif mode == "S6b" and a.rotor_at:
            parts.insert(parts.index("n3") + 1, "n8")
    seen = []
    for p in parts:
        if p not in seen:
            seen.append(p)
    parts = seen
    unknown = [p for p in parts if p not in S6_PARTS[mode]]
    if unknown:
        probs.append("parts %s are not %s parts (known: %s)" % (unknown, mode, ",".join(S6_PARTS[mode])))
    bad = [m for m in mutants if m not in ALL_MUTANTS]
    if bad:
        probs.append("unknown mutants %s (known: %s)" % (bad, ",".join(ALL_MUTANTS)))
    wrong = [m for m in mutants if m in MUTANT_MODES and mode not in MUTANT_MODES[m]]
    if wrong:
        probs.append("mutants %s do not run in %s" % (wrong, mode))
    if "S6.M6" in mutants and not getattr(a, "sn_host_slot", None):
        needs.append("S6.M6 needs --sn-host-slot (a Creative throwaway copy of the host save slot, owner-approved)")
    if "n8" in parts and not (a.owner or a.rotor_at):
        needs.append("n8 needs the owner's rotor: --owner (the launcher prints the step) or --rotor-at x,y,z")
    if mode == "S6b" and "n4" in parts and not a.owner:
        needs.append("S6b n4 needs the owner's hand-built 100 m hull: --owner (the launcher prints the step)")
    if mode in ("S6se", "S6b"):
        world = world or (CREATIVE_WORLD if "S6.M4" in mutants else SURVIVAL_WORLD)
    return parts, mutants, world, probs, needs

def plan_lines(LCH, a, mode, parts, mutants, world) -> list:
    sn_env, se_env = mutant_env(mode, mutants, a)
    out = []
    if mode == "S6a":
        out.append("  mode S6a: Subnautica + SE stand-in %s" % subprocess.list2cmdline(producer_cmd(LCH, a)))
    elif mode == "S6se":
        out.append("  mode S6se: SE (Legacy.exe -continue) on %r + Subnautica stand-in %s" % (
            world, subprocess.list2cmdline(host_cmd(LCH, a))))
    else:
        out.append("  mode S6b: Subnautica + SE (Legacy.exe -continue) on %r" % world)
    if mode in ("S6se", "S6b"):
        want = "Creative" if "S6.M4" in mutants else "Survival"
        info, probs = world_check(LCH, world, want, getattr(a, 'force_unmarked', False))
        out.append("  world %r: folder %s, LastSession %s, GameMode %s, DestructibleBlocks %s, autosave %s%s" % (
            world, info.get("folder"), info.get("lastSession"), info.get("gameMode"), info.get("destructibleBlocks"),
            info.get("autosaveMinutes"), (" -> REFUSED: " + "; ".join(probs)) if probs else " -> ok"))
        fresh = "--force" + (" --force-unmarked" if info.get("folder") and not os.path.exists(
            os.path.join(info["folder"], "DE_THROWAWAY.txt")) else "") if info.get("folder") else ""
        flags = " ".join(x for x in ("--game-mode Creative" if want == "Creative" else "", fresh, "--destructible "
                                     "--keep-bottles --autosave-off --point-last-session") if x)
        out.append("  orchestrator, SE closed: python -I tools/lab/make_survival_copy.py --name \"%s\" %s   (a fresh copy "
                   "of the current DE Lab; or --in-place instead of --force to edit the existing copy; after the "
                   "sessions: --restore-last-session)" % (world, flags))
    if "S6.M6" in mutants:
        out.append("  host slot (S6.M6): DEEPENGINEERS_SN_HOST_SLOT=%s (a Creative throwaway copy of the host slot, "
                   "owner-approved; rule 10)" % (getattr(a, "sn_host_slot", None) or "<--sn-host-slot>"))
    out.append("  env: Subnautica %s; SE %s" % (json.dumps(sn_env), json.dumps(se_env)))
    for p in parts:
        doc = (getattr(S6Runner, "p_" + p).__doc__ or "").strip()
        out.append("  part %-9s %s%s" % (p, " ".join(doc.split()), " [combat: refused when the Freedom gate fails]" if
                                         p in COMBAT_PARTS else ""))
    owner = []
    if mode == "S6b" and "n4" in parts:
        owner.append("n4: build or paste a hull about 100 m long at SN (%g, %g), its middle below the seabed or behind a "
                     "rock, when prompted" % N4["spot"])
    if "n8" in parts and not a.rotor_at:
        owner.append("n8: build the rotor (large rotor, small-grid head, 10 rpm) at SN (%g, %g, %g) when prompted" %
                     ROTOR_SPOT)
    out.append("  owner steps: %s" % ("; ".join(owner) if owner else "none (spawning creatures needs no owner)"))
    return out

def s6_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    try:
        rows, S = AN.evaluate(out)
        extra = {"mode": S.mode, "parts": S.parts, "mutants": S.mutants}
    except Exception as e:     # an analyser bug must not hide the session
        import traceback
        rows, extra = [{"id": "S6", "status": "FAIL", "measured": "analyser error %s" % type(e).__name__,
                        "threshold": "-", "what": str(e)[:120] + " | " + traceback.format_exc().strip().splitlines()[
                            -1][:120], "part": "all", "required": True}], {}
    ok = AN.write_results(out, rows, not getattr(a, "lenient", False), dict(extra, tool="launcher s6"))
    print("\nS6 ORACLE TABLE (%s)  (* = not required; INFO = information)" % out)
    try:
        print(AN.C.table(rows))
    except UnicodeEncodeError:
        print(AN.C.table(rows).encode("ascii", "replace").decode("ascii"))
    log.say("S6 ORACLE %s: %d rows, %d FAIL, %d MISSING -> %s" % (
        "PASS" if ok else "FAIL", len(rows), sum(1 for r in rows if r["status"] == "FAIL" and r.get("required", True)),
        sum(1 for r in rows if r["status"] == "MISSING" and r.get("required", True)),
        os.path.join(out, "s6-results.json")), ev="s6_result", ok=ok)
    return 0 if ok else 1

def s6(LCH, log, a) -> int:
    S = LCH.S
    if a.list_mutant_groups:
        for name, g in S6_MUTANT_GROUPS.items():
            print("  %-13s %-6s mutants %-7s parts %-22s red %s%s" % (
                name, g["mode"], ",".join(g["mutants"]), ",".join(g["parts"]), ",".join(g["red"]),
                (" (world %s)" % g["world"]) if g.get("world") else (" (--sn-host-slot)" if g.get("hostSlot") else
                                                                       (" (owner rotor)" if g.get("owner") else ""))))
        return 0
    if a.analyze:
        return s6_analyze(LCH, log, a, a.analyze)
    if a.selftest:
        return s6_selftest(LCH, log)
    if a.mutant_group and a.mutant_group not in S6_MUTANT_GROUPS:
        log.say("unknown mutant group %s (known: %s)" % (a.mutant_group, ",".join(S6_MUTANT_GROUPS)))
        return 2
    modes = [a.mode] if a.mode else ([S6_MUTANT_GROUPS[a.mutant_group]["mode"]] if a.mutant_group else None)
    if a.mutant_group and a.mode and a.mode != S6_MUTANT_GROUPS[a.mutant_group]["mode"]:
        log.say("group %s runs in %s, not %s" % (a.mutant_group, S6_MUTANT_GROUPS[a.mutant_group]["mode"], a.mode))
        return 2
    if modes is None:
        if not a.dry_run:
            log.say("s6: choose --mode S6a|S6se|S6b (or a --mutant-group); --dry-run without a mode prints all three")
            return 2
        modes = list(S6_MODES)
    if a.dry_run:
        rc = 0
        for mode in modes:
            a.mode = mode
            parts, mutants, world, probs, needs = resolve(LCH, log, a, mode)
            print("S6 %s plan: parts %s%s%s" % (mode, ",".join(parts), " (quick)" if a.quick else "",
                                                " mutants %s" % ",".join(mutants) if mutants else ""))
            for line in plan_lines(LCH, a, mode, parts, mutants, world):
                print(line)
            for p in needs:
                print("  A REAL RUN NEEDS: %s" % p)
            for p in probs:
                print("  PROBLEM: %s" % p)
                rc = 2
        print("  mutant groups: %s" % ", ".join("%s=%s(%s)" % (n, g["mode"], ",".join(g["mutants"]))
                                               for n, g in S6_MUTANT_GROUPS.items()))
        log.say("dry run: nothing started")
        return rc
    mode = modes[0]
    a.mode = mode
    parts, mutants, world, probs, needs = resolve(LCH, log, a, mode)
    if probs or needs:
        for p in probs + needs:
            log.say("REFUSE s6: %s" % p)
        return 2
    if mode in ("S6se", "S6b"):
        want = "Creative" if "S6.M4" in mutants else "Survival"
        info, wprobs = world_check(LCH, world, want, getattr(a, 'force_unmarked', False))
        if wprobs:
            for p in wprobs:
                log.say("REFUSE s6 %s: world %r: %s" % (mode, world, p))
            return 2
    if "S6.M6" in mutants:
        _info, hprobs = host_slot_check(LCH, a.sn_host_slot)
        if hprobs:
            for p in hprobs:
                log.say("REFUSE s6: %s" % p)
            return 2
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s6", stamp)
    sn_env, se_env = mutant_env(mode, mutants, a)
    a.cont = True
    if mode == "S6a" and not a.se_cmd:
        a.se_cmd = json.dumps(producer_cmd(LCH, a))
    if mode == "S6se" and not a.sn_cmd:
        a.sn_cmd = json.dumps(host_cmd(LCH, a))
    sn_cmd, se_cmd = LCH.session_commands(a)
    log.say("S6 session %s (%s): parts %s%s%s; Subnautica: %s; SE: %s; out %s" % (
        stamp, mode, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants else
        "", subprocess.list2cmdline(sn_cmd), subprocess.list2cmdline(se_cmd), out), ev="s6_plan")
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s6-session.jsonl"))
    standin_sn, standin_se = bool(a.sn_cmd), bool(a.se_cmd)
    sess.event("start", kind="s6", stamp=stamp, mode=mode, parts=parts, mutants=mutants, name=a.name,
               argv=sys.argv[1:], quick=a.quick, standin={"sn": standin_sn, "se": standin_se}, snCmd=sn_cmd,
               seCmd=se_cmd, qpcFrequency=S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env, world=world,
               wm1=wm1_path(LCH) if "g1e3" in parts else None, seed=a.seed)
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            runner = S6Runner(LCH, log, a, sess, sn, se, out, stamp, t_start)
            ok, why = runner.attach()
            if not ok:
                log.say("S6: harness not usable: %s" % why, ev="s6_harness_refused")
                rc = 1
            else:
                runner.park()
                runner.mutants_on(mutants)
                for p in parts:
                    if not runner.alive():
                        log.say("S6: a game exited before part %s" % p, ev="s6_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:
                        log.say("S6 part %s failed: %r" % (p, e), ev="s6_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
                log.say("S6 script finished: %s" % json.dumps(runner.stats), ev="s6_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            LCH.close_session(log, a, sn, se, standin_se, standin_sn)
        S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None,
                   unsupported=sorted("%s.%s" % k for k in runner.unsupported) if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, sn, standin_sn, standin_se, stamp)
    if rc is not None:
        log.say("S6 session aborted (rc %d); analysing what was collected" % rc)
    res = s6_analyze(LCH, log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# self-test: every part of every mode against fake peers (no section, no process, no game)
# ----------------------------------------------------------------------------------------------------------------

class _FakeHarness:
    """Answers the runner's commands like the plugins / stand-ins would (canned replies), recording them."""

    def __init__(self, LCH, gate=True):
        self.LCH, self.gate, self.sent, self.ids = LCH, gate, [], 0x0200000000000000

    def send(self, side, cmd, args):
        self.sent.append((side, cmd, args))
        return len(self.sent), self.LCH.S.RW_OK

    def wait_reply(self, rid, timeout):
        side, cmd, args = self.sent[rid - 1]
        if cmd == "de.s3" and isinstance(args, dict) and "place" in args:
            self.ids += 1
            return {"id": rid, "ok": True, "entityId": self.ids}
        if cmd == "de.s6" and args.get("action") == "spawn":
            return {"id": rid, "ok": True, "line": "de.s6 spawn", "skin": args.get("skin"),
                    "rootId": "0x%016x" % (0x0044455336000001)}
        if cmd == "de.s6" and args.get("action") == "status":
            return {"id": rid, "ok": True, "gate": {"mode": "Freedom" if self.gate else "Creative",
                                                    "invisible": not self.gate, "pass": self.gate}}
        if cmd == "de.s6" and args.get("action") == "damage":
            ids = ["5:%d" % i for i in range(1, 4)]
            return {"id": rid, "ok": True, "plan": {"mode": args.get("mode"), "targets": [{"ids": ids}],
                                                     "list": [{"id": i} for i in ids]}}
        return {"id": rid, "ok": True, "line": "%s %s" % (cmd, json.dumps(args)[:40])}

    def poll_replies(self):
        pass

class _Null:
    def __init__(self):
        self.events = []

    def event(self, ev, **kw):
        self.events.append(dict(kw, ev=ev))

    def say(self, text, ev="say", **kw):
        self.events.append(dict(kw, ev=ev, text=text))

    def set(self, part):
        pass

    def stop(self):
        pass

    def mark(self):
        return 0

    def since(self, mark, name=None):
        return []

class _Proc:
    pid = 0

    @staticmethod
    def poll():
        return None

def s6_selftest(LCH, log) -> int:
    import argparse
    results = []

    def check(ok, what, detail=""):
        results.append(bool(ok))
        print("  %s %s%s" % ("PASS" if ok else "FAIL", what, (": " + detail) if detail and not ok else ""))

    print("S6 launcher self-test (fake peers: no section, no process, no game)")
    expect = {"selftest": ("de.s6",), "g1e3": ("de.s6",), "bt1": ("de.s6", "de.teleport"), "n3": ("de.spawn",),
              "n4": ("de.spawn",), "n5": ("de.spawn",), "n6": ("de.spawn",), "n7": ("de.teleport",),
              "teardown": (), "skin": ("de.s3", "de.s6"), "n1": ("de.s3", "de.s6"), "n10": ("de.s6",),
              "bt1e": ("de.swim", "de.s3", "de.spawn"), "n8": ("de.spawn",)}
    for mode in S6_MODES:
        for gate in ((True, False) if mode != "S6se" else (True,)):
            a = argparse.Namespace(mode=mode, quick=True, owner=True, rotor_at=None, seed=1, cmd_timeout=1.0,
                                   name="Local\\DeepEngineers_s6_selftest", selftest=True)
            r = S6Runner.__new__(S6Runner)
            r.LCH, r.L, r.S, r.log, r.a, r.mode, r.q = LCH, LCH.L, LCH.S, _Null(), a, mode, True
            r.sess, r.c7, r.se_tail, r.sn_tail = _Null(), _Null(), _Null(), _Null()
            r.sn, r.se, r.out, r.stamp = _Proc(), _Proc(), ".", "selftest"
            r.h = _FakeHarness(LCH, gate)
            r.sec = None
            r.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
            r.unsupported, r.gate_ok, r.grid, r.grid_sn, r.hull = set(), None, None, None, None
            r.wait = lambda seconds: None
            r.wait_rows = lambda tail, mark, pred, timeout, label: (pred([]), [])[1]
            errors = []
            for p in S6_PARTS[mode]:
                n0 = len(r.h.sent)
                try:
                    if p == "g1e3":
                        r.sess.events.clear()
                    getattr(r, "p_" + p)()
                except Exception as e:     # noqa: BLE001 - reported, the self-test continues
                    import traceback
                    errors.append("%s: %s %s" % (p, type(e).__name__, traceback.format_exc().splitlines()[-3:]))
                    continue
                cmds = {c for _s, c, _a in r.h.sent[n0:]}
                refused = any(e.get("ev") == "combat_refused" and e.get("part") == p for e in r.sess.events)
                if refused:
                    continue
                missing = [c for c in expect.get(p, ()) if c not in cmds]
                if missing:
                    errors.append("%s sent %s, not %s" % (p, sorted(cmds), missing))
            refused = sorted({e.get("part") for e in r.sess.events if e.get("ev") == "combat_refused"})
            want_refused = [] if gate else sorted(p for p in S6_PARTS[mode] if p in COMBAT_PARTS)
            check(not errors and refused == want_refused, "%s (gate %s): every part ran (%d commands), refused %s" % (
                mode, "pass" if gate else "FAIL", len(r.h.sent), refused or "none"), "; ".join(errors)[:600])
    for name, g in S6_MUTANT_GROUPS.items():
        a = argparse.Namespace(mutants="", mutant_group=name, world=None, parts="", owner=True, rotor_at=None,
                               sn_host_slot="slot9999")
        parts, mutants, world, probs, needs = resolve(LCH, log, a, g["mode"])
        ok = not probs and not needs and list(parts) == list(g["parts"]) and list(mutants) == list(g["mutants"])
        if g["mode"] in ("S6se", "S6b"):
            ok &= world == (g.get("world") or SURVIVAL_WORLD)
        check(ok, "group %s resolves to %s %s (world %s)" % (name, g["mode"], ",".join(parts), world),
              "; ".join(probs + needs))
    A = analyzer(LCH)
    check(A.bound_key(2852.0, 152.0) == bound_key(2852.0, 152.0) == (2900.0, 30.0),
          "the bound buckets of launcher and analyzer agree (2852 m, 152 deg -> 2900 m, 30 deg)")
    ok = all(results) and bool(results)
    print("S6 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

# ----------------------------------------------------------------------------------------------------------------
# argparse and dispatch (called by launcher.py)
# ----------------------------------------------------------------------------------------------------------------

def add_parsers(sub, LCH):
    p = sub.add_parser("s6", help="one scripted S6 'Collision B + combat' session (S6a: Subnautica + the stand-in SE; "
                                  "S6se: SE alone on the Survival copy + the stand-in Subnautica host; S6b: both games): "
                                  "the S6 oracle table")
    p.add_argument("--mode", choices=S6_MODES, default=None,
                   help="S6a | S6se | S6b (required for a real run; --dry-run without it prints all three)")
    p.add_argument("--parts", default="", help="comma list (aliases n2, n9 = selftest); S6a: %s; S6se: %s; S6b: %s" % (
        ",".join(S6_PARTS["S6a"]), ",".join(S6_PARTS["S6se"]), ",".join(S6_PARTS["S6b"])))
    p.add_argument("--mutants", default="", help="comma list of %s" % ",".join(ALL_MUTANTS))
    p.add_argument("--mutant-group", default=None, help="a runbook group (mode + mutants + parts): %s" %
                   ",".join(S6_MUTANT_GROUPS))
    p.add_argument("--list-mutant-groups", action="store_true", help="print the S6 seeded-failure groups and exit")
    p.add_argument("--selftest", action="store_true", help="every part of every mode against fake peers (no section, "
                                                           "process or game), the groups' resolution")
    p.add_argument("--world", default=None, help="S6se / S6b: the SE world copy LastSession points at (default %r; the "
                                                 "creative group %r)" % (SURVIVAL_WORLD, CREATIVE_WORLD))
    p.add_argument("--sn-host-slot", default=None, help="S6.M6 (hostcreative): the Creative throwaway host slot "
                                                        "(DEEPENGINEERS_SN_HOST_SLOT)")
    p.add_argument("--rotor-at", type=lambda v: tuple(float(x) for x in v.split(",")), default=None,
                   help="S6b n8: x,y,z (SN) of a rotor with a small-grid head already in the copy (no owner step)")
    p.add_argument("--owner", action="store_true", help="wait for the owner's steps (n8: build the rotor)")
    p.add_argument("--force-unmarked", action="store_true", help="review F15: accept an SE world copy without the DE_THROWAWAY.txt marker or with autosave on")
    p.add_argument("--seed", type=int, default=1, help="the stand-in host's plan seed (S6se)")
    p.add_argument("--quick", action="store_true", help="short holds and waits (x%.2f; smoke runs)" % QUICK)
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="print the plan (parts, commands, world checks); start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (S6se default: the stand-in host)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (S6a default: the stand-in producer)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--screen", default="2560x1440", help="stand-in producer screen (S6a)")
    p.add_argument("--fov", type=float, default=70.0, help="stand-in producer vertical FOV (S6a)")
    p.add_argument("--out", default=None, help="output folder (default tests/out/s6/<stamp>)")
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S6 oracles on a collected folder")

def run(LCH, log, a) -> int:
    if a.cmd == "s6":
        return s6(LCH, log, a)
    return 2
