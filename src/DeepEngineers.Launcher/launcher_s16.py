"""`launcher.py s16` (ROADMAP §14 "### S16 Depth progression"; docs/S16-BRIEF.md; CONTRACT §5.4 "S16 conventions").
Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`); the session
plumbing is launcher_s6's (S6Runner: the C11 harness, the row tails), as launcher_s11's.

 python -I src/DeepEngineers.Launcher/launcher.py s16 --mode S16se|S16b [--parts...] [--mutant-group NAME] [--world COPY]
 [--quick] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s16 --dry-run | --list-mutant-groups | --selftest | --notes
 python -I src/DeepEngineers.Launcher/launcher.py s16 --analyze tests/out/s16/<stamp>

Modes:
 S16se SE alone + the stand-in Subnautica host (tests/standins/fake_sn_host.py). Parts:
 selftest de.s16 status and ladder (the pin, the damage gate), then a wait for the 60 s load grace to end (S16.GRACE)
 heavy a 4 x 4 x 4 SmallHeavyBlockArmorBlock hull spawned at SN y -50: 56 heavy counted, 300 m (S16.LADDER)
 room a 5 x 5 x 5 LargeHeavyBlockArmorBlock shell around a dry room with a light armour block and an Air Vent
 against a wall inside: 98 heavy counted, the inner block not, 1,000 m (S16.LADDER: the air rule)
 station a static 3 x 3 x 3 LargeHeavyBlockArmorBlock grid at SN y -50: never tracked nor rated, no crush depth, no
 row names it (S16.STATIC; depth 9)
 light the nearest column >= 400 m deep around the parked character (de.s16 {deep: 400}: rings of 60 m to 15 km);
 a 4 x 4 x 4 SmallBlockArmorBlock hull spawned at SN y -100 there, watched (the HUD grid), descended
 kinematically (moved toward the planet centre each tick, velocities zeroed: no physics range, gravity or
 buoyancy involved) from 250 m at 10 m/s to 450 m and pinned 3 s (S16.LADDER, BREAK, CURVE, CREAK, VOICE,
 HUD): it creaks from 300 m, the voice at 350 m, blocks break past 350 m, the cap at 440 m (depth 10/11; the hull
 is mostly gone by the cap, so the descent is fast). The job is polled each second; the session logs
 s16_descent (reached or not) and the analyzer judges the descent rows MISSING when it fell short
 bare a small cockpit and a battery (nothing counted) from 300 m to 400 m at 5 m/s: its wet-faced blocks break
 past 350 m
 tier2 the nearest clear column >= 2,200 m deep (the throwaway world's void floor is 3,000 m); a 3 x 3 x 3
 LargeHeavyBlockArmorBlock hull with a DE_PressureHullMk2Large in its centre spawned at SN y -1,900, descended
 at 10 m/s to 2,060 m and pinned 18 s, so it stays past its limit for 24 s and the voice repeats once
 (S16.LADDER, T2, CURVE, CREAK, VOICE, HUD): nothing breaks before 2,000 m (depth 11: Mk2 is the top tier)
 teardown the test hulls removed, de.s16 watch 0, damage on, status
 S16b both games (the same parts; Subnautica's creaks and voice: S16.SN; its own readout, seated "depth m / crush m" without a hull panel: S16.SNHUD). Subnautica's log
 names every sound it plays on one line: "DE.S16 sound <creak|voice|damage> seq=<SE event> level=<n> played=<0|1>
 source=<field> path=<FMOD path> coalesced=<n>", and its HUD on "DE.S16 Subnautica HUD <diver|grid|off> …"

Owner-attended free play (`--notes` prints it). Output tests/out/s16/<stamp>/ (s16-session.jsonl, se-*/sn-*.jsonl, the game
logs) and the S16 ORACLE TABLE of tests/s16/analyze_s16.py (s16-table.txt, s16-results.json). This launcher never writes into a
game folder, a save or SE's LastSession; the world copy is the orchestrator's (tools/lab/make_survival_copy.py, SE closed)."""
from __future__ import annotations

import datetime
import json
import os
import sys
import time

import launcher_s6 as L6

S16_MODES = ("S16se", "S16b")
ALL_PARTS = ("selftest", "heavy", "room", "station", "light", "bare", "tier2", "teardown")
S16_PARTS = {"S16se": ALL_PARTS, "S16b": ALL_PARTS}
SE_MUTANTS = ("S16.M1", "S16.M2", "S16.M3", "S16.M4", "S16.M5", "S16.M6", "S16.M8", "S16.M9", "S16.M10")
SN_MUTANTS = ("S16.M7", "S16.M11")
ALL_MUTANTS = SE_MUTANTS + SN_MUTANTS
# S16.M9 (the mechanical group and docking ignored) needs grids linked by rotors, connectors or landing gear, which the
# harness cannot build: it is proven red by the analyzer's self-test only; the project's free-play scene shows the real rows
SELFTEST_ONLY = ("S16.M9",)
S16_MUTANT_GROUPS = {
    "tiers": {"mutants": ("S16.M1",), "mode": "S16se", "parts": ("selftest", "light", "tier2", "teardown"), "red": ("S16.LADDER", "S16.T2")},
    "early": {"mutants": ("S16.M2",), "mode": "S16se", "parts": ("selftest", "light", "bare", "tier2", "teardown"), "red": ("S16.BREAK", "S16.T2", "S16.BARE")},
    "cap": {"mutants": ("S16.M3",), "mode": "S16se", "parts": ("selftest", "light", "teardown"), "red": ("S16.CURVE",)},
    "band": {"mutants": ("S16.M4",), "mode": "S16se", "parts": ("selftest", "light", "teardown"), "red": ("S16.CREAK",)},
    "voice": {"mutants": ("S16.M5",), "mode": "S16se", "parts": ("selftest", "light", "teardown"), "red": ("S16.VOICE",)},
    "centre": {"mutants": ("S16.M6",), "mode": "S16se", "parts": ("selftest", "light", "teardown"), "red": ("S16.HUD",)},
    "snclock": {"mutants": ("S16.M7",), "mode": "S16b", "parts": ("selftest", "light", "teardown"), "red": ("S16.SN",)},
    "wrap": {"mutants": ("S16.M11",), "mode": "S16b", "parts": ("selftest", "light", "teardown"), "red": ("S16.SNHUD",)},
    "exposure": {"mutants": ("S16.M8",), "mode": "S16se", "parts": ("selftest", "heavy", "room", "light", "teardown"), "red": ("S16.LADDER",)},
    "fallback": {"mutants": ("S16.M10",), "mode": "S16se", "parts": ("selftest", "bare", "teardown"), "red": ("S16.BARE",)},
}
SURVIVAL_WORLD = L6.SURVIVAL_WORLD
# ext s16 depth 9 (light small 180 m, Mk3 2,500 m; depth 11: Mk2 2,000 m the top tier; the damage doubled on 8 blocks a second since depth 3): the light hull dives
# fast so it reaches its target (past the cap, limit + 90 m) before it is crushed; tier2 is pinned 18 s so the voice repeats
# (every 20 s past the limit); its column needs 2,700 m: the throwaway world's void floor is 3,000 m (the depth-8 sessions'
# columns read 3,000 m)
LIGHT = {"kind": "light", "y": -250.0, "to": 450.0, "rate": 10.0, "need": 600.0, "hold": 3.0}   # depth 10/11: light small 350 m (start above the creak band, pass the cap)
TIER2 = {"kind": "tier2", "y": -1900.0, "to": 2060.0, "rate": 10.0, "need": 2200.0, "hold": 18.0}   # depth 11: Mk2 2,000 m is the top tier
BARE = {"kind": "bare", "y": -300.0, "to": 400.0, "rate": 5.0, "need": 520.0, "hold": 3.0}   # depth 10/11: the bare craft is rated light small, 350 m
SHAPE_Y = -50.0
REACH_TOL_M = 5.0      # tests/s16/analyze_s16.py REACH_TOL_M: a descent within 5 m of its target reached it
GRACE_WAIT_S = 75.0
OWNER_NOTES = """S16 owner-attended free play (launcher.py run after the world mod and both plugins are deployed). Look at, and tell me:
 A  ladder   Sit in a ship under water: Subnautica's depth meter at the top centre shows the depth and the crush depth like
             the Cyclops helm ("250 m / 400m"), white when safe, amber while the hull creaks, red past the limit; no hull
             panel (above water, or if Subnautica cannot show it: SE's line "Depth N m | crush depth M m (light armour,
             small grid)"). A light
             armour small ship is rated 180 m, heavy armour small 300 m, light large 600 m, heavy large 1,000 m; one exposed
             light armour block on a heavy hull makes it light (the weakest exposed block counts); windows, doors and
             cockpits in the hull take the class of the armour around them; blocks inside a sealed, pressurised room do
             not count, nor do thrusters outside. A craft with no armour (cockpit + thrusters) is rated light and breaks.
             Rotor/hinge/piston parts share the best pressure hull of their ship; a small ship docked (connector,
             landing gear) inside a pressurised hangar takes no damage, docked outside it is rated at least as the carrier.
 B  upgrades G menu, category DeepEngineers: Pressure Hull Mk1 / Mk2 (both grid sizes; depth 11, no Mk3). The assembler
             makes their alloys: Hull Alloy Mk1 from cobalt, magnesium, silver; Mk2 from gold, platinum, uranium ingots.
             Weld one inside the ship: within 10 s the HUD's crush depth becomes 1,400 / 2,000 m (the best working one
             counts). A station (static grid) has no crush depth: seated in it, the depth only.
 C  descent  Dive below the limit slowly: from 50 m above it the hull creaks (Subnautica's base creaks, more often the
             deeper), at the limit the Cyclops voice says "Hull integrity critical" and again every 20 s while you stay
             below it (louder, the creaks and groans duck under it), the HUD turns red, and past it the 8 deepest hull
             blocks take damage every second (10 hp/s at the limit, +1 per metre, 100 from 90 m past: a light small craft
             50 m past loses blocks within seconds, a sub 100 m past dies within a minute). Subnautica's depth meter sits
             at the top centre of the screen, above SE's toolbar.
 D  diver    Swim without a ship: under water Subnautica's own depth meter shows your depth at the top centre; SE shows no
             text line for the diver (depth 5: above water nothing). Breathing x1.5 below 100 m, x2 below 200 m; the diver never
             takes pressure damage. Seated, SE's grid line appears only when Subnautica's readouts are not shown (above water).
 E  creative Creative with destructible blocks takes the same damage as Survival (depth 4: the gate is the world's
             destructible blocks, whatever the game mode); with destructible blocks off the HUD and the creaks work and the
             hull takes no damage ("no damage: indestructible blocks"); de.s16 damage off overrides in any world.
 F  load     Load an existing Survival world with ships deeper than their new limit: a red notice "pressure damage starts in
             55 s: N grid(s) past their crush depth"; nothing breaks for the first 60 s.
 Dev: /de s16 status | ladder | damage on|off (dev builds)."""
QUICK = L6.QUICK

class S16Runner(L6.S6Runner):
    """The S16 parts through launcher_s6's plumbing (the base runs in its S6 mode: S16b as S6b, S16se as S6se)."""

    def __init__(self, LCH, log, a, sess, sn, se, out, stamp, t_start, s16mode):
        self.s16mode = s16mode
        saved = a.mode
        a.mode = "S6b" if s16mode == "S16b" else "S6se"
        try:
            super().__init__(LCH, log, a, sess, sn, se, out, stamp, t_start)
        finally:
            a.mode = saved
        self.spawned = []

    def s16(self, args, label, timeout=10.0):
        return self.call("se", "de.s16", args, timeout=timeout, label=label)

    def mutants_on(self, mutants):
        for m in mutants:
            if m in SN_MUTANTS and self.s16mode == "S16b":
                self.call("sn", "de.mutant", {"name": m, "on": True}, label="mutant.sn.%s" % m)
            if m in SE_MUTANTS:
                self.call("se", "de.mutant", {"name": m, "on": True}, label="mutant.se.%s" % m)

    # ---- parts --------------------------------------------------------------------------------------------------
    def p_selftest(self):
        """de.s16 status and ladder (the conventions pin, the damage gate), then the wait for the 60 s load grace"""
        self.part("s16.selftest", "begin")
        self.s16({"status": True}, "selftest.status")
        self.s16({"ladder": True}, "selftest.ladder")
        self.s16({"damage": "on"}, "selftest.damage")
        t0 = time.monotonic()
        while time.monotonic() - t0 < GRACE_WAIT_S and self.alive():      #  no crush damage during the grace
            rep = self.s16({"status": True}, "selftest.grace")
            left = (((rep or {}).get("status") or {}).get("graceLeftS"))
            if left is not None and float(left) <= 0:
                break
            self.wait(min(5.0, max(1.0, float(left) if left is not None else 5.0)))
        self.part("s16.selftest", "end")

    def _shape(self, part, kind, settle):
        """spawn a shape at SN y -50 over a deep column, let SE scan it (and the gas system build its room), then remove it"""
        self.part(part, "begin")
        col = self.s16({"deep": 200.0}, "%s.deep" % part, timeout=20.0)
        if not self.ok(col):
            self.log.say("  S16 %s: no column (%s)" % (part, json.dumps(col)[:160] if col else "no reply"), ev="s16_no_column")
            self.part(part, "end")
            return
        rep = self.s16({"spawn": kind, "x": col["x"], "y": SHAPE_Y, "z": col["z"], "label": kind}, "%s.spawn" % part)
        gid = grid_of(rep)
        if not gid:
            self.log.say("  S16 %s: spawn refused (%s)" % (part, json.dumps(rep)[:200] if rep else "no reply"), ev="s16_spawn_failed")
            self.part(part, "end")
            return
        self.spawned.append(gid)
        self.s16({"descend": gid, "rate": 0.5, "to": abs(SHAPE_Y), "label": kind}, "%s.hold" % part)   # held in place (the buoyancy)
        self.wait(settle)
        self.s16({"job": gid}, "%s.final" % part)
        self.s16({"stop": gid}, "%s.stop" % part)
        self.s16({"remove": gid}, "%s.remove" % part)
        self.part(part, "end")

    def p_heavy(self):
        """a 4^3 heavy small hull: 56 heavy counted, 300 m (S16.LADDER)"""
        self._shape("heavy", "heavy", self.scale(6.0, 3.0))

    def p_room(self):
        """a 5^3 heavy large shell around a dry room (inner light block + Air Vent on a wall): 98 heavy, the inner block uncounted, 1,000 m"""
        self._shape("room", "room", self.scale(20.0, 10.0))

    def p_station(self):
        """a static 3^3 heavy large grid (a station): never tracked nor rated (S16.STATIC, depth 9)"""
        self.part("station", "begin")
        col = self.s16({"deep": 200.0}, "station.deep", timeout=20.0)
        if not self.ok(col):
            self.log.say("  S16 station: no column (%s)" % (json.dumps(col)[:160] if col else "no reply"), ev="s16_no_column")
            self.part("station", "end")
            return
        rep = self.s16({"spawn": "station", "x": col["x"], "y": SHAPE_Y, "z": col["z"], "label": "station"}, "station.spawn")
        gid = grid_of(rep)
        if not gid:
            self.log.say("  S16 station: spawn refused (%s)" % (json.dumps(rep)[:200] if rep else "no reply"), ev="s16_spawn_failed")
            self.part("station", "end")
            return
        self.spawned.append(gid)
        self.wait(self.scale(6.0, 3.0))   # three enumerations (every 120 ticks) and a rescan: a static grid must stay untracked
        j = ((self.s16({"job": gid}, "station.job") or {}).get("job") or {})
        self.sess.event("s16_static", part="station", grid=gid, tracked=j.get("tracked"), crush=j.get("crush"), scanned=j.get("scanned"),
                        static=j.get("static"), exists=j.get("exists"))
        self.log.say("  S16 station: grid %s static %s, tracked %s, crush %s" % (gid, j.get("static"), j.get("tracked"), j.get("crush")), ev="s16_static")
        self.s16({"remove": gid}, "station.remove")
        self.part("station", "end")

    def _descent(self, part, spec):
        self.part(part, "begin")
        col = self.s16({"deep": spec["need"]}, "%s.deep" % part, timeout=20.0)
        if not self.ok(col):
            self.log.say("  S16 %s: no column %d m deep (%s): its rows will be MISSING" % (part, spec["need"], json.dumps(col)[:160] if col else "no reply"),
                         ev="s16_no_column")
            self.part(part, "end")
            return
        x, z = col["x"], col["z"]
        self.log.say("  S16 %s: column SN (%s, %s), floor %.0f m, %s m from the character at %s" % (part, x, z, float(col.get("floor") or 0), col.get("ring"), col.get("near")),
                     ev="s16_column")
        self.sess.event("s16_column", part=part, x=x, z=z, floor=col.get("floor"), ring=col.get("ring"), near=col.get("near"))
        rep = self.s16({"spawn": spec["kind"], "x": x, "y": spec["y"], "z": z, "label": spec["kind"]}, "%s.spawn" % part)
        gid = grid_of(rep)
        if not gid:
            self.log.say("  S16 %s: spawn refused (%s)" % (part, json.dumps(rep)[:200] if rep else "no reply"), ev="s16_spawn_failed")
            self.part(part, "end")
            return
        self.spawned.append(gid)
        self.s16({"watch": gid}, "%s.watch" % part)
        self.wait(2.0)
        self.s16({"descend": gid, "rate": spec["rate"], "to": spec["to"], "label": spec["kind"]}, "%s.descend" % part)
        budget = abs(spec["to"] - abs(spec["y"])) / spec["rate"] + 30.0
        t0 = time.monotonic()
        deepest, j = None, {}
        while time.monotonic() - t0 < budget and self.alive():
            self.wait(1.0)      # the grid's depth once per second in the session log (SE writes s16.depth rows per second too)
            job = self.s16({"job": gid}, "%s.job" % part)
            j = (job or {}).get("job") or {}
            if j.get("depth") is not None:
                deepest = max(deepest if deepest is not None else -1e9, float(j["depth"]))
            if j.get("holding") or not j.get("exists", True):
                break
        reached = deepest is not None and deepest >= spec["to"] - REACH_TOL_M
        self.log.say("  S16 %s: descent %s %d m (deepest %s m, %s, watched %s)" % (part, "reached" if reached else "did NOT reach", spec["to"],
                     "%.1f" % deepest if deepest is not None else "-", "holding" if j.get("holding") else ("gone" if not j.get("exists", True) else "still descending"), j.get("watched")),
                     ev="s16_descent")
        self.sess.event("s16_descent", part=part, label=spec["kind"], to=spec["to"], deepest=deepest, reached=reached, holding=bool(j.get("holding")),
                        exists=j.get("exists"), watched=j.get("watched"))
        self.wait(spec.get("hold", 3.0))
        self.s16({"job": gid}, "%s.final" % part)
        self.s16({"stop": gid}, "%s.stop" % part)
        self.s16({"watch": 0}, "%s.unwatch" % part)
        self.s16({"remove": gid}, "%s.remove" % part)
        self.part(part, "end")

    def p_light(self):
        """a 4^3 light small hull from 250 m to 450 m at 10 m/s: creaks from 300 m, the voice at 350 m, blocks break past it"""
        self._descent("light", LIGHT)

    def p_bare(self):
        """a cockpit + battery small craft from 300 m to 400 m at 5 m/s: nothing counted, its wet blocks break past 350 m (S16.BARE)"""
        self._descent("bare", BARE)

    def p_tier2(self):
        """a 3^3 heavy large hull with a Mk2 pressure hull from 1,900 m to 2,060 m at 10 m/s, pinned 18 s: nothing breaks before 2,000 m, the voice repeats"""
        self._descent("tier2", TIER2)

    def p_teardown(self):
        """the test hulls removed, watch 0, damage on, de.s16 status"""
        self.part("s16.teardown", "begin")
        for gid in self.spawned:
            self.s16({"remove": gid}, "teardown.remove")
        self.s16({"watch": 0}, "teardown.watch")
        self.s16({"damage": "on"}, "teardown.damage")
        self.s16({"status": True}, "teardown.status")
        self.part("s16.teardown", "end")

    def keep_breathing(self):
        if self.s16mode != "S16b":
            return
        now = time.monotonic()
        if now - self._last_refill < L6.REFILL_S:
            return
        self._last_refill = now
        self.call("se", "de.swim", {"refill": True}, label="o2.refill")

# ----------------------------------------------------------------------------------------------------------------------
# plan, environment, world
# ----------------------------------------------------------------------------------------------------------------------

def grid_of(rep):
    """The spawned grid's id from a de.s16 spawn reply: the string "grid" (never "id", the C11 reply's own key; a string,
 because SE's MiniJson reads JSON numbers as doubles and an 18-digit entity id would round), None when refused."""
    if not L6.S6Runner.ok(rep):
        return None
    g = rep.get("grid")
    return str(g) if g not in (None, "", 0, "0") else None

def analyzer(LCH):
    p = os.path.join(LCH.REPO, "tests", "s16")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s16 as AN   # noqa: E402
    return AN

def env_for(mode, mutants):
    """(Subnautica env, SE env): S16 on (its default; set explicitly), S6 as the s11 sessions; the mutants on their side."""
    sn_env = {"DEEPENGINEERS_S6": "on"}
    se_env = {"DEEPENGINEERS_S5": "on", "DEEPENGINEERS_S6": "on", "DEEPENGINEERS_S16": "on"}
    sn_m = [m for m in mutants if m in SN_MUTANTS]
    se_m = [m for m in mutants if m in SE_MUTANTS]
    if sn_m and mode == "S16b":
        sn_env["DEEPENGINEERS_MUTANT"] = ",".join(sn_m)
    if se_m:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(se_m)
    return sn_env, se_env

def resolve(a, mode):
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    parts = None
    probs = []
    if a.mutant_group:
        g = S16_MUTANT_GROUPS[a.mutant_group]
        mutants, parts = list(g["mutants"]), list(g["parts"])
    if a.parts and a.parts.strip().lower() == "all":
        parts = list(S16_PARTS[mode])
    elif a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    if parts is None:
        parts = list(S16_PARTS[mode])
    unknown = [p for p in parts if p not in S16_PARTS[mode]]
    if unknown:
        probs.append("parts %s are not %s parts (known: %s)" % (unknown, mode, ",".join(S16_PARTS[mode])))
    bad = [m for m in mutants if m not in ALL_MUTANTS]
    if bad:
        probs.append("unknown mutants %s (known: %s)" % (bad, ",".join(ALL_MUTANTS)))
    sn_only = [m for m in mutants if mode == "S16se" and m in SN_MUTANTS]
    if sn_only:
        probs.append("mutants %s are Subnautica's: they need S16b (S16se runs the stand-in host)" % sn_only)
    return parts, mutants, a.world or SURVIVAL_WORLD, probs

def host_cmd(LCH, a):
    return [sys.executable, "-I", os.path.join(LCH.REPO, "tests", "standins", "fake_sn_host.py"), "--name", a.name,
            "--s1", "--s3", "--s6", "--damage-rate", "0", "--damage-seed", str(a.seed), "--quit-on-se-exit", "--quiet"]

def plan_lines(mode, parts, mutants, world):
    out = ["  world: %s (Survival, destructible blocks, the throwaway marker, autosave off; tools/lab/make_survival_copy.py;"
           " the world mod with Data/CubeBlocks_S16.sbc deployed)" % world,
           "  Subnautica: %s" % ("the stand-in host (fake_sn_host.py)" if mode == "S16se" else "the real game, Freedom host slot"),
           "  SE: the real game on the copy",
           "  env: %s" % json.dumps(env_for(mode, mutants))]
    for p in parts:
        out.append("  part %-9s %s" % (p, (getattr(S16Runner, "p_" + p).__doc__ or "").strip().split("\n")[0][:110]))
    return out

def s16_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    rows, _S = AN.evaluate(out)
    ok = AN.write_results(out, rows, strict=not a.lenient)
    print(AN.C.table(rows))
    log.say("S16 ORACLE TABLE: %s (%s)" % ("PASS" if ok else "FAIL", os.path.join(out, "s16-table.txt")), ev="s16_verdict", ok=ok)
    return 0 if ok else 1

def s16(LCH, log, a) -> int:
    S = LCH.S
    if a.notes:
        print(OWNER_NOTES)
        return 0
    if a.list_mutant_groups:
        for name, g in S16_MUTANT_GROUPS.items():
            print("  %-8s %-6s mutants %-8s parts %-30s red %s" % (name, g["mode"], ",".join(g["mutants"]), ",".join(g["parts"]), ",".join(g["red"])))
        return 0
    if a.analyze:
        return s16_analyze(LCH, log, a, a.analyze)
    if a.selftest:
        return s16_selftest(LCH, log)
    if a.mutant_group and a.mutant_group not in S16_MUTANT_GROUPS:
        log.say("unknown mutant group %s (known: %s)" % (a.mutant_group, ",".join(S16_MUTANT_GROUPS)))
        return 2
    modes = [a.mode] if a.mode else ([S16_MUTANT_GROUPS[a.mutant_group]["mode"]] if a.mutant_group else None)
    if modes is None:
        if not a.dry_run:
            log.say("s16: choose --mode S16se|S16b (or a --mutant-group); --dry-run without a mode prints both")
            return 2
        modes = list(S16_MODES)
    if a.dry_run:
        rc = 0
        for mode in modes:
            parts, mutants, world, probs = resolve(a, mode)
            print("S16 %s plan: parts %s%s%s" % (mode, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants else ""))
            for line in plan_lines(mode, parts, mutants, world):
                print(line)
            info, wprobs = L6.world_check(LCH, world, "Survival", getattr(a, "force_unmarked", False))
            for p in wprobs:
                print("  A REAL RUN NEEDS: world %r: %s" % (world, p))
            for p in probs:
                print("  PROBLEM: %s" % p)
                rc = 2
        print("  mutant groups: %s" % ", ".join("%s=%s(%s)" % (n, g["mode"], ",".join(g["mutants"])) for n, g in S16_MUTANT_GROUPS.items()))
        log.say("dry run: nothing started")
        return rc
    mode = modes[0]
    parts, mutants, world, probs = resolve(a, mode)
    if probs:
        for p in probs:
            log.say("REFUSE s16: %s" % p)
        return 2
    info, wprobs = L6.world_check(LCH, world, "Survival", getattr(a, "force_unmarked", False))
    if wprobs:
        for p in wprobs:
            log.say("REFUSE s16 %s: world %r: %s (throwaway-copy rule)" % (mode, world, p))
        return 2
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s16", stamp)
    sn_env, se_env = env_for(mode, mutants)
    a.cont = True
    if mode == "S16se" and not a.sn_cmd:
        a.sn_cmd = json.dumps(host_cmd(LCH, a))
    sn_cmd, se_cmd = LCH.session_commands(a)
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s16-session.jsonl"))
    sess.event("start", kind="s16", stamp=stamp, mode=mode, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:],
               quick=a.quick, standin={"sn": bool(a.sn_cmd), "se": False}, snCmd=sn_cmd, seCmd=se_cmd,
               qpcFrequency=S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env, world=world)
    log.say("S16 session %s (%s): parts %s mutants %s; out %s" % (stamp, mode, ",".join(parts), ",".join(mutants) or "none", out), ev="s16_plan")
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            runner = S16Runner(LCH, log, a, sess, sn, se, out, stamp, t_start, mode)
            ok, why = runner.attach()
            if not ok:
                log.say("S16: harness not usable: %s" % why, ev="s16_harness_refused")
                rc = 1
            else:
                if mode == "S16b":
                    runner.park()
                runner.mutants_on(mutants)
                for p in parts:
                    if not runner.alive():
                        log.say("S16: a game exited before part %s" % p, ev="s16_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:  # noqa: BLE001 - a part error is logged, the session goes on
                        log.say("S16 part %s failed: %r" % (p, e), ev="s16_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if not a.keep_running:
            LCH.close_session(log, a, sn, se, False, bool(a.sn_cmd))
        S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None,
                   unsupported=sorted("%s.%s" % k for k in runner.unsupported) if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, sn, bool(a.sn_cmd), False, stamp)
    res = s16_analyze(LCH, log, a, out)
    return rc if rc is not None else res

def run(LCH, log, a) -> int:
    return s16(LCH, log, a)

# ----------------------------------------------------------------------------------------------------------------------
# self-test (no game, no section, no process)
# ----------------------------------------------------------------------------------------------------------------------

def s16_selftest(LCH, log) -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S16 launcher self-test (launcher.py s16 --selftest)")

    class A:
        mutants = ""
        parts = ""
        world = None
        mutant_group = None

    for mode in S16_MODES:
        a = A()
        parts, mutants, world, probs = resolve(a, mode)
        check(parts == list(S16_PARTS[mode]) and not probs and world == SURVIVAL_WORLD, "%s default parts %s on %s" % (mode, ",".join(parts), world))
        check(all(hasattr(S16Runner, "p_" + p) for p in S16_PARTS[mode]), "%s: every part has a runner method" % mode)
    AN = analyzer(LCH)
    for name, g in S16_MUTANT_GROUPS.items():
        a = A()
        a.mutant_group = name
        parts, mutants, world, probs = resolve(a, g["mode"])
        sn_env, se_env = env_for(g["mode"], mutants)
        se_side = [m for m in mutants if m in SE_MUTANTS]
        sn_side = [m for m in mutants if m in SN_MUTANTS]
        ok = not probs and parts == list(g["parts"]) and (not se_side or se_env.get("DEEPENGINEERS_MUTANTS") == ",".join(se_side))
        ok = ok and (not sn_side or sn_env.get("DEEPENGINEERS_MUTANT") == ",".join(sn_side)) and se_env["DEEPENGINEERS_S16"] == "on"
        ok = ok and tuple(g["red"]) == tuple(AN.MUTANT_ROWS[g["mutants"][0]])
        need = {"S16.LADDER": "light", "S16.T2": "tier2", "S16.BREAK": "light", "S16.CURVE": "light", "S16.CREAK": "light",
                "S16.VOICE": "light", "S16.HUD": "light", "S16.SN": "light", "S16.BARE": "bare", "S16.SNHUD": "light"}
        ok = ok and all(need[r] in parts for r in g["red"])
        check(ok, "group %s: %s %s, red %s (the analyzer's MUTANT_ROWS), env %s / %s" % (name, g["mode"], ",".join(mutants), ",".join(g["red"]),
                                                                                           sn_env.get("DEEPENGINEERS_MUTANT"), se_env.get("DEEPENGINEERS_MUTANTS")))
    grouped = {m for g in S16_MUTANT_GROUPS.values() for m in g["mutants"]}
    check(set(AN.MUTANT_ROWS) == set(ALL_MUTANTS) and grouped | set(SELFTEST_ONLY) == set(ALL_MUTANTS), "every analyzer mutant has a group (S16.M9: the analyzer's self-test)")
    a = A()
    a.mutants = "S16.M7"
    _p, _m, _w, probs = resolve(a, "S16se")
    check(bool(probs), "a Subnautica mutant refused in S16se (the stand-in host has no S16)")
    a = A()
    a.parts = "danger"
    _p, _m, _w, probs = resolve(a, "S16se")
    check(bool(probs), "danger is not an S16 part")
    lim = AN.T.hull_crush(AN.T.SMALL, AN.T.LIGHT)
    check(-LIGHT["y"] < lim - AN.T.K["BandM"] and LIGHT["to"] > lim + 90, "the light descent starts above the creak band and passes the cap (limit + 90 m)")
    check(BARE["to"] > lim and -BARE["y"] < lim, "the bare descent crosses %d m" % lim)
    check((TIER2["to"] - AN.T.tier_crush(2)) / TIER2["rate"] + TIER2["hold"] > AN.T.V["RepeatMs"] / 1000.0 + 2, "tier2 stays past its limit long enough for a voice repeat")
    check(GRACE_WAIT_S > AN.T.D["GraceTicks"] / 60.0, "the selftest part outwaits the load grace")
    check({lab: s["to"] for lab, s in (("light", LIGHT), ("bare", BARE), ("tier2", TIER2))} == AN.DESCENT_TO and REACH_TOL_M == AN.REACH_TOL_M,
          "the analyzer's descent targets (S16 'descent did not reach N m') are the launcher's")
    check(grid_of({"id": 20, "ok": True, "grid": "129626897907186171", "floor": 3000.7}) == "129626897907186171"
          and grid_of({"id": 129626897907186171, "ok": True}) is None and grid_of({"id": 20, "ok": False, "error": "x"}) is None,
          "the spawn reply's grid id is the string 'grid' (never the C11 reply's own 'id')")
    l3 = AN.T.tier_crush(2)
    check(-l3 < TIER2["y"] <= -(l3 - 100) and TIER2["to"] > l3 and TIER2["need"] > TIER2["to"] + 100,
          "the tier-3 descent starts within 100 m above %d m, passes it, on a column deep enough" % l3)
    check(AN.selftest() == 0, "tests/s16/analyze_s16.py --selftest")
    check("Hull integrity critical" in OWNER_NOTES and "Pressure Hull Mk1" in OWNER_NOTES, "owner notes name the voice and the blocks")
    ok = all(results)
    print("S16 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def add_parsers(sub, LCH):
    p = sub.add_parser("s16", help="S16 depth progression: modes S16se (SE + stand-in host) and S16b (both games)")
    p.add_argument("--mode", choices=S16_MODES, default=None)
    p.add_argument("--parts", default="", help="comma list (default: every part of the mode), or 'all'")
    p.add_argument("--mutants", default="", help="S16.M1..S16.M11 (dev builds)")
    p.add_argument("--mutant-group", default=None, choices=sorted(S16_MUTANT_GROUPS))
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--world", default=None, help="the Survival throwaway copy (default %r)" % SURVIVAL_WORLD)
    p.add_argument("--force-unmarked", action="store_true", help="accept a copy without the throwaway marker (owner-approved)")
    p.add_argument("--quick", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--notes", action="store_true", help="print the owner-attended free-play notes")
    p.add_argument("--analyze", default=None, metavar="DIR")
    p.add_argument("--lenient", action="store_true", help="MISSING rows do not fail the table")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--seed", type=int, default=16)
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    # the session plumbing's options (as launcher_s6's)
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (S16se default: the stand-in host)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--screen", default="2560x1440")
    p.add_argument("--fov", type=float, default=70.0)
    p.add_argument("--owner", action="store_true", help="accepted for symmetry with s6 (S16 has no scripted owner step: --notes)")
