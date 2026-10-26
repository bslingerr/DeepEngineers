"""Fake Subnautica host: a Python stand-in for the SN plugin's bridge (ROADMAP §5.8, S0).

Creates the section exactly like the SN plugin (CONTRACT §6): bridge token check, mutex `<name>_sn`, ctypes
CreateFileMappingW with the CONTRACT security descriptor (ConvertStringSecurityDescriptorToSecurityDescriptorW, the
current user's SID), creator rules 1-3; then publishes the SN header block (PID, generation, heartbeat every frame from
GetTickCount64, phases), C7 SnStatus every frame (seqlock), answers the hello (helloAckEpoch) and link epochs
(linkAckEpoch + counters), consumes C3/C4 with full validation, produces C8 damage records (overflow queue of 4,096,
oldest dropped), and serves C11 control commands (de.ping, de.state, de.selftest, de.stallSn, de.quit) when
devControl is non-zero.

Events are JSON lines on stdout (`t` = QPC seconds, one clock for every process). Exit codes: 0 normal, 2 dormant, 3 refused (creator rules or second instance), 4 failed.

Mutants (S0 seeded failures):
 --mutant magic creates the section with magic "DEEQ" (S0.M1, run 1)
 --mutant wire2 creates the section with wireVersion 2 (S0.M1, run 2)
 --mutant layout2 pre-creates the section with layoutMajor 2, no mutex (S0.M6)
 --mutant size pre-creates a 64 MiB section, no mutex (S0.M6)
 --mutant zero-heads re-initialisation zeroes every producer head (S0.M7)

S1 (ROADMAP S1 "Stand-ins": the fake SN host consumes the real SE snapshots and checks the WM numbers, so SE can be
tested without Subnautica). With --s1:
- phases Booting -> Menu -> LoadingSave (the host-save load, --load-seconds) -> ReadyIdle (the launcher's S1 ready
 phase) -> Active once SE is in world and its hello is acknowledged; C7 worldLoaded, uiHidden, runInBackgroundForced,
 areaBuilt/streamingSettled (emulated: cleared for --settle-seconds after every camera snap), perfModeApplied,
 frameMsP50/P99, resamplesLastFrame = 1;
- the camera of ARCHITECTURE §5.8 / CONTRACT §2.8 at --fps (default 144 in S1): C1 read by seqlock every frame (torn
 reads counted), the last 4 snapshots kept, render time t = qpcNow - 1.25 SE ticks, position lerped between the two
 snapshots bracketing t (extrapolated for at most one tick, else held), orientation and FOV from the newest snapshot,
 snap on the teleport flag or a jump > 20 m / > 45 deg in one tick; C7 presentedSeTick = the newer bracket tick,
 written at the end of the frame; one `s1.cam` event per frame and `s1.camStats` every 300 frames;
- `s1.probe` once per second (Ocean.GetDepthOf = max(0, -y) with the ocean level 0; biome emulated from a landmark
 table and flagged "emulated"); `s1.still` when the camera has been still for 2 s, with the error to the nearest S1
 target of tests/s1/check_wm.py (live WM monitor);
- C11 (CtlToSn) de.pixel {pointSn, minSeTick, label}: the pixel of an SN point through the emulated camera (Unity
 projection, reported from the top-left corner, y down); C10 capture requests answered with a JSON sidecar (no
 pixels: a stand-in renders nothing) under --captures-dir;
- --fmod-every S emits a synthetic `s1.fmod` WaterAmbience event every S seconds while Active (tests fmod_events.py);
 --fmod-pda-at S emits one PDA voice event (its seeded failure);
- --s1-check [SE_LOG|auto]: at exit, runs tests/s1/check_wm.py on the SE log (auto: the newest se-*.jsonl in
 %LOCALAPPDATA%\\DeepEngineers\\logs written during this run) and this host's own S1 events, and emits the rows as an
 `s1_check` event (the WM numbers of the real SE against worldmap_ref, without Subnautica).

S2 (ROADMAP S2 "Stand-ins": the SE plugin's S2 code can be driven without Subnautica). With --s2 (implies --s1):
- C5 drives the host like the SN plugin's S2Host: perfMode Idle -> snPhase ReadyIdle, Track -> WarmingUp, Full ->
 Active (while SE is in world with an acknowledged hello); the recipe pairs (vSyncCount, targetFrameRate) Idle (0, 10),
 Track (0, 10), Full (1, -1) are published as C7 perfModeApplied / vSyncCountApplied / targetFrameRateApplied and the
 frame loop runs at 10 fps in Idle and Track, at --fps in Full; cameras are "on" only in Full (presentedSeTick is
 stamped only then, as MainCamera is disabled in Idle and Track); C7 frozen = Idle or C5 Paused or SE stalled/exited;
- snHwnd / snRect / rectOk: --s2-window creates a real top-level window on its own thread (WS_POPUP,
 WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW, shown without activation; default rectangle OFF-SCREEN at -32000,-32000 so
 nothing appears on a monitor; --s2-rect x,y,w,h places it) and publishes its handle in C0 snHwnd and C7 snHwnd;
 snRect is its window rectangle, or without a window the C5 seRect (a well-placed window); rectOk = snRect == seRect;
- the mix: the bus volume follows C5 snBusTarget over C5 crossfadeMs (0 while frozen or C5 Unfocused); `s2.mix`,
 `s2.freeze` on changes and `s2.perf` once per second (snFrame, fps, perfModeApplied, vSyncCount, targetFrameRate,
 frozen, timeScale, camerasOn, busVolume, rectOk), the schema of tests/s2/s2_common.py;
- C11 de.calib {"colour": "r,g,b"|[r,g,b]|"#rrggbb"|"off"} (the window shows the colour; reply calib on/off) and de.s2
 (status), the SN plugin's S2 commands.

S3 (ROADMAP S3 "Stand-ins": the S3a fake producer's or the SE plugin's C2/C3/C4 traffic checked without Subnautica).
With --s3 (implies --s1): C3/C4 records of real models are accepted and validated as the SN plugin does (stream_py.py
"S3"; the S0 catalogue keeps its exact checks); C7 pendingParts counts parts whose model has not ended or whose submesh
materials are undefined; and, with no rendering, the plugin's analyser lines as JSON events:
- `s3.winding` per completed model: the C3 measure of the mirrored mesh (x, y, -z) with Unity triangles (I0, I2, I1)
 (wire_ref.classify_triangle, the census rule), the raw fraction before any flip, flipped = below 1 % (S3.C3);
- `s3.mirror` when every part of a grid has its model and materials: parts, verts, tris, gridModelVerts/Tris and the
 drawn triangle total gridTris (each model triangle once: Wire's coverage rule) (S3.C1);
- `s3.placement` whenever a built grid's C2 pose (o, q, s, k) changes: per corner direction i the drawn vertex whose SE-local position (part rows applied to the
 received model position, no mirror) is the most extreme, its SE-local position (localCorners) and the SN world
 position of its drawn (Unity-local, mirrored) position through this host's Frame/Entity; formerly the 8 corners of the
 grid's SE-local AABB (union
 of the parts' model bounds through their PartAdd matrices; corner i: bit 0 max x, bit 1 max y, bit 2 max z) placed by
 this host's own Apply = o + S*(q*(k*Mirror(l))) from the C2 floats, with the C2 tick (tests/s3/check_placement.py's SN
 schema: S3.C2);
- C11 de.s3 {action: status | placement}; every other de.s3 action (probe, lab, shear, frametime, count, teardown) and
 de.time answer "unknown" (a stand-in renders nothing: those rows stay MISSING).
--s3-mutant M1 drops the Z mirror (S3.C2 and S3.C3 red), M2 the triangle swap (S3.C3 red).

S6 (ROADMAP S6 "tools/tests": the fake SN host that sends synthetic and malformed damage records to the real SE; class
S6Host, tests/standins/s6_stream.py). With --s6 (implied by --damage, --malformed or --damage-plan):
- C4 type 6 SkinSet is consumed as the SN plugin does: decoded and checked (Wire SkinCodec's rules), a version <= the
 applied one counted stale, sections diffed by id and content hash; one s6.skin row per message {entity, version,
 sections, boxes, created, updated, removed, stale, bad, cls, detail}; GridAdd gives each grid's targetClass and parent,
 C1 (characterId, flags) or C4 CharacterAdd the character; EntityRemove / ClearAll drop the skins;
- C8: the S0 synthetic stream is off; damage plans go through the overflow queue (4,096, oldest dropped) as Subnautica
 would write them (dedupSeq 1, 2, 3... per generation, the acknowledged hello's worldEpoch, the GridAdd class's
 seScaleExpected, hit points at box centres in the target's own frame, FrameSubgrid for subgrid targets):
 --damage n1 (S6.N1: 30 unique bites per target, amounts on a 1/8 grid in [1, 3] SN_HP on grids and [1, 2] on the
 character, plus 10 duplicates in total), --damage n10 / --malformed (S6.N10: 100 malformed records with fresh ids, each
 tagged with the DamageReject reason SE must give it: Generation, Epoch, Sequence, Unit, Amount, Kind, Type, Entity,
 Section, Frame, HitPoint), --damage stream (valid records at --damage-stream-rate). A plan runs on de.s6 {action:
 damage, mode, targets: auto | [{entity, kind}] | {small, large, character}, unique, dups, seed}, from --damage-plan
 <json> (polled; run when it appears or changes), or by itself with --damage-auto (3 s after the last new skin). Each
 plan is one s6.plan row (targets with their ids, amounts, sums and expected SE amounts, duplicate ids; n10: every id
 with its expected reason): the S6 analyzer's ground truth for S6.N1 / S6.N10;
- C7 flags bit 10 damageSelfTestPass and capability bit 1 set (emulated); s6.gate and s6.selftest rows (emulated: the
 analyzer does not judge S6.N2 / S6.N9 on them); C11 de.s6 {action: status | skins | damage | selftest} and de.spawn
 (answered, nothing spawned). The launcher passes --damage-rate 0 with a real SE (the S0 stream is off under --s6 anyway).

Examples (python -I):
 set DEEPENGINEERS_BRIDGE=1 & python -I tests/standins/fake_sn_host.py --hold-seconds 60
 python -I tests/standins/fake_sn_host.py --no-token-check --mutant magic --passive
 set DEEPENGINEERS_BRIDGE=1 & python -I tests/standins/fake_sn_host.py --s1 --quit-on-se-exit --s1-check auto > sn.jsonl
"""
from __future__ import annotations

import argparse
import collections
import glob
import json
import math
import os
import struct
import sys
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_WIRE = os.path.normpath(os.path.join(_HERE, "..", "wire"))
_REPO = os.path.normpath(os.path.join(_HERE, "..", ".."))
_S1 = os.path.join(_REPO, "tests", "s1")
for _p in (_WIRE, _HERE, _S1):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402
import stream_py as ST  # noqa: E402

EXIT_OK, EXIT_DORMANT, EXIT_REFUSED, EXIT_FAILED = 0, 2, 3, 4
MAGIC_DEEQ = 0x51454544
MUTANT_SIZE = 0x04000000
FOREIGN_CREATOR_MUTANTS = ("layout2", "size")
TICK_S = 1.0 / 60.0
SNAP_M, SNAP_DEG = 20.0, 45.0
# Emulated biome lookup (stand-in only): landmark spots of tools/worldmap/worldmap_ref.LANDMARKS_SN, horizontal radius
EMULATED_BIOMES = (((-1029.0, -1116.0), 300.0, "grandReef"),)

def _q_rot(q, v):
    u = (q[0], q[1], q[2])
    t = (2 * (u[1] * v[2] - u[2] * v[1]), 2 * (u[2] * v[0] - u[0] * v[2]), 2 * (u[0] * v[1] - u[1] * v[0]))
    c = (u[1] * t[2] - u[2] * t[1], u[2] * t[0] - u[0] * t[2], u[0] * t[1] - u[1] * t[0])
    return (v[0] + q[3] * t[0] + c[0], v[1] + q[3] * t[1] + c[1], v[2] + q[3] * t[2] + c[2])

def _q_angle_deg(a, b) -> float:
    d = abs(sum(x * y for x, y in zip(a, b)))
    return math.degrees(2.0 * math.acos(min(1.0, d)))

def _f32(x):
    import struct
    return struct.unpack("<f", struct.pack("<f", x))[0]

class S1View:
    """The Subnautica side of S1 in the stand-in: C1 snapshots -> emulated camera (ARCHITECTURE §5.8)."""

    def __init__(self, host, a):
        self.host, self.a = host, a
        self.reader = None
        self.snaps = collections.deque(maxlen=4)
        self.counts = collections.Counter()
        self.window = collections.Counter()
        self.cam = None              # dict: pos, rot, fov, near, tickOld, tickNew, tickRot, alpha, outcome
        self.presented = 0
        self.settled_at = 0.0
        self.area_tick = 0
        self.area_pos = (0.0, 0.0, 0.0)
        self.frame_ms = collections.deque(maxlen=300)
        self.last_frame_t = None
        self.last_probe = 0.0
        self.pending_pixels = []
        self.capture_last_req = None
        self.capture_pending = None
        self.still = None            # [tick0, t0, pos, rot, reported]
        self.fmod_next = None
        self.fmod_pda_done = False
        self.underwater_mode = "off"     # de.underwater (S1.N6 variants): emulated IsUnderwater for the s1.fmod events
        self.events = []             # own S1 events, for --s1-check
        self.rig_logged = False
        self.fov_applied = None
        self.t0 = time.perf_counter()
        self.W, self.H = a.screen
        self.tick_qpc = S.QPC_FREQUENCY * TICK_S
        self.delay_qpc = a.interp_delay_ticks * self.tick_qpc
        try:
            import check_wm   # noqa: E402
            p = check_wm.PLAN
            self.targets = [("wm2", tuple(p["wm2"]["sn"]))] + [("wm4." + n, tuple(pt)) for n, pt in p["wm4"]["points"]]
        except ImportError:
            self.targets = []

    def emit(self, ev, msg=None, **kw):
        self.host.emit(ev, msg, **kw)
        if self.a.s1_check:
            kw = dict(kw)
            kw["ev"] = ev
            self.events.append(kw)

    def attach(self, sec):
        self.reader = S.SeqlockReader(sec, L.REGION["C1"].offset, L.SE_FRAME)
        self.snaps.clear()

    # ---- per frame ------------------------------------------------------------------------------------------------
    def frame(self, frame_no, active, cameras_on=True):
        now = time.perf_counter()
        if self.last_frame_t is not None:
            self.frame_ms.append((now - self.last_frame_t) * 1000.0)
        self.last_frame_t = now
        if self.reader is None:
            return
        st = self.reader.poll()
        torn = st == S.SEQ_BUSY
        if st == S.SEQ_FRESH:
            v = self.reader.value
            if self.snaps and v["tick"] < self.snaps[-1]["tick"]:
                self.snaps.clear()           # a new SE generation or world: ticks restart
            if not self.snaps or v["tick"] != self.snaps[-1]["tick"]:
                self.snaps.append(v)
        if not self.snaps or not (self.snaps[-1]["flags"] & (1 << L.SE_FRAME_FLAGS["inWorld"])):
            return
        q_used = S.qpc()
        t = q_used - self.delay_qpc
        snaps = list(self.snaps)
        newest = snaps[-1]
        old = new = None
        alpha, outcome = 1.0, "held"
        for i in range(len(snaps) - 1):
            if snaps[i]["qpcCapture"] <= t <= snaps[i + 1]["qpcCapture"]:
                old, new = snaps[i], snaps[i + 1]
                span = max(1, new["qpcCapture"] - old["qpcCapture"])
                alpha, outcome = (t - old["qpcCapture"]) / span, "bracketed"
                break
        if old is None:
            if t > newest["qpcCapture"] and len(snaps) >= 2 and t - newest["qpcCapture"] <= self.tick_qpc:
                old, new = snaps[-2], newest
                span = max(1, new["qpcCapture"] - old["qpcCapture"])
                alpha, outcome = 1.0 + (t - new["qpcCapture"]) / span, "extrapolated"
            elif t < snaps[0]["qpcCapture"]:
                old = new = snaps[0]
            else:
                old = new = newest
        # snap (CONTRACT 2.8): a jump > 20 m or > 45 deg in one tick, or the SE teleport flag, between the older
 # bracket snapshot and the newest -> show the newest snapshot at once
        span = [s for s in snaps if old["tick"] <= s["tick"] <= newest["tick"]]
        snap = False
        for s0, s1 in zip(span, span[1:]):
            if s1["flags"] & (1 << L.SE_FRAME_FLAGS["teleported"]) or math.dist(s0["camPos"], s1["camPos"]) > SNAP_M \
                    or _q_angle_deg(s0["camRot"], s1["camRot"]) > SNAP_DEG:
                snap = True
                break
        if snap:
            old = new = newest
            alpha, outcome = 1.0, "snap"
            self.settled_at = now + self.a.settle_seconds
        if torn:
            outcome = "torn"          # the seqlock read failed this frame: the last coherent copies are used
            self.counts["tornReads"] += 1
        pos = tuple(_f32(o + (n - o) * alpha) for o, n in zip(old["camPos"], new["camPos"]))
        self.cam = {"pos": pos, "rot": tuple(newest["camRot"]), "fov": newest["vFovDeg"], "near": newest["near"],
                    "tickOld": old["tick"], "tickNew": new["tick"], "tickRot": newest["tick"], "alpha": alpha,
                    "outcome": outcome, "qpcUsed": q_used, "epoch": newest["worldEpoch"]}
        self.counts[outcome] += 1
        self.window[outcome] += 1
        self.window["frames"] += 1
        if cameras_on:               # S2: MainCamera disabled in Idle and Track -> no presented frame, no stamp
            self.presented = new["tick"]
        if now >= self.settled_at and self.area_tick != new["tick"]:
            self.area_tick, self.area_pos = new["tick"], pos
        if not self.rig_logged:
            # the SN plugin's DE.Cam rig line: Subnautica's options FOV when the rig was taken (S1.N7 precondition)
            self.rig_logged = True
            self.emit("s1.camRig", optionsFov=self.a.options_fov, fov=self.a.options_fov, emulated=True)
        # emulated camera: the transform is placed exactly (commanded pose = transform) and nothing else moves the FOV
 # (fovSeen = the C1 FOV applied), as the SN plugin traces them since the review fixes (findings 1 and 7)
        fov_applied = self.fov_applied if self.fov_applied is not None else -1.0
        self.fov_applied = newest["vFovDeg"]
        self.emit("s1.cam", frame=frame_no, qpcUsed=q_used, tickOld=old["tick"], tickNew=new["tick"],
                  tickRot=newest["tick"], alpha=round(alpha, 6), outcome=outcome, qpcCaptureNew=new["qpcCapture"],
                  qpcCaptureRot=newest["qpcCapture"], worldEpoch=newest["worldEpoch"], camPos=list(pos),
                  camRot=list(newest["camRot"]), camPosCmd=list(pos), camRotCmd=list(newest["camRot"]),
                  fov=newest["vFovDeg"], fovSeen=fov_applied if fov_applied > 0 else newest["vFovDeg"],
                  fovApplied=fov_applied, c1Fov=newest["vFovDeg"], near=newest["near"], screen=[self.W, self.H])
        if self.window["frames"] >= 300:
            w = self.window
            self.emit("s1.camStats", frames=w["frames"], bracketed=w["bracketed"], extrapolated=w["extrapolated"],
                      held=w["held"], snap=w["snap"], torn=w["torn"], tornReads=self.counts["tornReads"],
                      resamplesMax=1, fovDrift=0, gated=0)
            self.window = collections.Counter()
        if now - self.last_probe >= 1.0:
            self.last_probe = now
            self.probe(frame_no)
        self.track_still(now)
        self.serve_pixels(frame_no)
        if active:
            self.fmod(now, frame_no)

    def biome(self, pos):
        for (x, z), r, name in EMULATED_BIOMES:
            if math.hypot(pos[0] - x, pos[2] - z) <= r and pos[1] < 0:
                return name
        return "standin-unknown"

    def probe(self, frame_no):
        c = self.cam
        self.emit("s1.probe", frame=frame_no, seTick=c["tickNew"], camPos=list(c["pos"]), depthOf=max(0.0, -c["pos"][1]),
                  oceanLevel=0.0, biome=self.biome(c["pos"]), isUnderwater=False, cameraInside=False,
                  playerPos=list(c["pos"]), playerToCamM=0.0, statsFrozen=True, emulated=True)

    def track_still(self, now):
        c = self.cam
        s = self.still
        if s is None or math.dist(s[2], c["pos"]) > 1e-3 or _q_angle_deg(s[3], c["rot"]) > 1e-3:
            self.still = [c["tickNew"], now, c["pos"], c["rot"], False]
            return
        if not s[4] and now - s[1] >= 2.0:
            s[4] = True
            fwd = _q_rot(c["rot"], (0.0, 0.0, 1.0))
            near = min(self.targets, key=lambda t: math.dist(t[1], c["pos"])) if self.targets else None
            err = math.dist(near[1], c["pos"]) if near else None
            axes = {"N": (0, 0, 1), "E": (1, 0, 0), "down": (0, -1, 0), "S": (0, 0, -1), "W": (-1, 0, 0)}
            ax = min(axes, key=lambda k: math.degrees(math.acos(max(-1.0, min(1.0, sum(a * b for a, b in
                                                                                        zip(axes[k], fwd)))))))
            ax_err = math.degrees(math.acos(max(-1.0, min(1.0, sum(a * b for a, b in zip(axes[ax], fwd))))))
            self.emit("s1.still", "camera still 2 s at (%.4f, %.4f, %.4f), nearest target %s err %s m, forward %s "
                      "err %.4f deg" % (c["pos"][0], c["pos"][1], c["pos"][2], near[0] if near else "-",
                                        "%.4f" % err if err is not None else "-", ax, ax_err),
                      seTick=s[0], camPos=list(c["pos"]), forward=list(fwd), target=near[0] if near else None,
                      targetErrM=err, axis=ax, axisErrDeg=ax_err, fov=c["fov"])

    def fmod(self, now, frame_no):
        a = self.a
        if a.fmod_every > 0:
            if self.fmod_next is None:
                self.fmod_next = now + a.fmod_every
            elif now >= self.fmod_next:
                self.fmod_next += a.fmod_every
                under = self.underwater_mode == "global" and self.cam is not None and self.cam["pos"][1] < 0
                self.emit("s1.fmod", frame=frame_no, qpc=S.qpc(), path="event:/standin/water_ambience_loop",
                          source="WaterAmbience", isUnderwater=under, variant=self.underwater_mode, emulated=True)
        if a.fmod_pda_at > 0 and not self.fmod_pda_done and now - self.t0 >= a.fmod_pda_at:
            self.fmod_pda_done = True
            self.emit("s1.fmod", frame=frame_no, qpc=S.qpc(), path="event:/standin/pda_voice_line", source="PDASounds",
                      isUnderwater=False, emulated=True)

    # ---- de.pixel (CtlToSn) ---------------------------------------------------------------------------------------
    def pixel_of(self, w):
        """Unity Camera.WorldToScreenPoint of the emulated camera, y flipped to the top-left convention."""
        c = self.cam
        q = c["rot"]
        qi = (-q[0], -q[1], -q[2], q[3])
        d = tuple(x - y for x, y in zip(w, c["pos"]))
        lx, ly, lz = _q_rot(qi, d)
        if lz <= 0:
            return None
        t = math.tan(math.radians(c["fov"]) / 2.0)
        aspect = self.W / float(self.H)
        nx, ny = lx / (lz * t * aspect), ly / (lz * t)
        return [(nx + 1.0) / 2.0 * self.W, (1.0 - ny) / 2.0 * self.H]

    def add_pixel_request(self, rid, args):
        self.pending_pixels.append({"rid": rid, "args": args, "t0": time.perf_counter()})

    def serve_pixels(self, frame_no):
        keep = []
        for r in self.pending_pixels:
            args = r["args"]
            ready = self.cam is not None and self.cam["tickNew"] >= int(args.get("minSeTick", 0) or 0) and \
                self.cam["outcome"] != "snap"
            if not ready:
                if time.perf_counter() - r["t0"] > 2.0:
                    self.host.reply({"id": r["rid"], "ok": False, "error": "no frame with SE tick >= %s within 2 s" %
                                     args.get("minSeTick")})
                else:
                    keep.append(r)
                continue
            pt = args.get("pointSn")
            px = self.pixel_of(tuple(float(x) for x in pt)) if isinstance(pt, (list, tuple)) and len(pt) == 3 else None
            if px is None:
                self.host.reply({"id": r["rid"], "ok": False, "error": "pointSn missing or behind the camera"})
                continue
            ev = {"label": args.get("label", ""), "frame": frame_no, "seTick": self.cam["tickNew"], "pointSn": list(pt),
                  "snPx": px, "screen": [self.W, self.H], "fov": self.cam["fov"], "camPos": list(self.cam["pos"]),
                  "camRot": list(self.cam["rot"])}
            self.emit("s1.pixel", **ev)
            rep = {"id": r["rid"], "ok": True}
            rep.update(ev)
            self.host.reply(rep)
        self.pending_pixels = keep

    # ---- C10 capture (stand-in: a JSON sidecar, no pixels) ------------------------------------------------------
    def capture(self, sec, frame_no):
        off = L.REGION["C10"].offset
        st, _seq, data = S.seqlock_read(sec, off + L.C10_REQUEST_OFFSET, L.CAPTURE_REQUEST.size)
        if st == S.SEQ_FRESH:
            req = L.CAPTURE_REQUEST.unpack(data)
            if req["reqId"] and req["reqId"] != self.capture_last_req:
                self.capture_last_req = req["reqId"]
                self.capture_pending = req
        req = self.capture_pending
        if not req or self.cam is None:
            return
        if req["kind"] == L.CONSTS["CaptureKind"]["AtSeTick"] and self.cam["tickNew"] < req["atSeTick"]:
            return
        self.capture_pending = None
        rel = bytes(req["path"][:req["pathLength"]]).decode("utf-8", "replace")
        root = self.a.captures_dir or os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
                                                   "DeepEngineers", "captures")
        path = os.path.normpath(os.path.join(root, rel.replace("/", os.sep)))
        ok = True
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path + ".json", "w", encoding="utf-8") as f:
                json.dump({"slice": "S1", "reqId": req["reqId"], "frame": frame_no, "seTick": self.cam["tickNew"],
                           "camPos": list(self.cam["pos"]), "camRot": list(self.cam["rot"]), "fov": self.cam["fov"],
                           "standin": True, "note": "fake_sn_host stand-in: sidecar only, no pixels"}, f, indent=1)
        except OSError:
            ok = False
        pb = rel.encode("utf-8")[:222]
        res = L.CAPTURE_RESULT.pack(reqId=req["reqId"], status=L.CONSTS["CaptureStatus"]["Ok" if ok else "Failed"],
                                    alpha=1.0, snFrame=frame_no, seTickUsed=self.cam["tickNew"], pathLength=len(pb),
                                    path=pb)
        S.seqlock_write(sec, off + L.C10_RESULT_OFFSET, res)
        label = os.path.splitext(os.path.basename(rel))[0]
        self.emit("s1.capture", label=label, path=rel, sidecar=path + ".json", ok=ok, frame=frame_no,
                  seTick=self.cam["tickNew"], standin=True)

    def frame_percentiles(self):
        v = sorted(self.frame_ms)
        if not v:
            return 0.0, 0.0
        return v[len(v) // 2], v[min(len(v) - 1, int(len(v) * 0.99))]

class StandinWindow:
    """A real top-level stand-in window on its own message thread: WS_POPUP, WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW, shown
 without activation (never takes the foreground; no taskbar button). The default rectangle is off-screen, so the
 z-order tools (tools/display/walker.py) see a real window while nothing appears on a monitor."""

    def __init__(self, title, rect, colour=(10, 40, 80)):
        self.title, self.rect, self.colour = title, tuple(rect), tuple(colour)
        self.hwnd = 0
        self._ready = None
        self._thread = None

    def start(self, timeout=5.0) -> int:
        import threading
        self._ready = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        self._ready.wait(timeout)
        return self.hwnd

    def _run(self):
        import ctypes
        import ctypes.wintypes as wt
        u, k, g = ctypes.windll.user32, ctypes.windll.kernel32, ctypes.windll.gdi32
        LRESULT = ctypes.c_ssize_t
        WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
        u.DefWindowProcW.restype = LRESULT
        u.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
        g.CreateSolidBrush.restype = wt.HBRUSH
        u.SetClassLongPtrW.restype = ctypes.c_size_t
        u.SetClassLongPtrW.argtypes = [wt.HWND, ctypes.c_int, ctypes.c_ssize_t]
        u.CreateWindowExW.restype = wt.HWND
        u.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, ctypes.c_int, ctypes.c_int,
                                      ctypes.c_int, ctypes.c_int, wt.HWND, wt.HMENU, wt.HINSTANCE, wt.LPVOID]

        def proc(h, m, w, l):
            if m == 0x0002:          # WM_DESTROY
                u.PostQuitMessage(0)
                return 0
            return u.DefWindowProcW(h, m, w, l)
        self._proc = WNDPROC(proc)

        class WNDCLASSW(ctypes.Structure):
            _fields_ = [("style", wt.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
                        ("cbWndExtra", ctypes.c_int), ("hInstance", wt.HINSTANCE), ("hIcon", wt.HICON),
                        ("hCursor", wt.HANDLE), ("hbrBackground", wt.HBRUSH), ("lpszMenuName", wt.LPCWSTR),
                        ("lpszClassName", wt.LPCWSTR)]
        cls = "DE_StandinWindow_%d_%d" % (S.current_pid(), id(self))
        wc = WNDCLASSW()
        wc.lpfnWndProc = self._proc
        wc.hInstance = k.GetModuleHandleW(None)
        wc.hbrBackground = g.CreateSolidBrush(self._rgb(self.colour))
        wc.lpszClassName = cls
        u.RegisterClassW(ctypes.byref(wc))
        x, y, w_, h_ = self.rect
        hwnd = u.CreateWindowExW(0x08000000 | 0x00000080, cls, self.title, 0x80000000, x, y, w_, h_, None, None,
                                 wc.hInstance, None)
        self.hwnd = int(hwnd or 0)
        self._u = u
        self._g = g
        if hwnd:
            u.ShowWindow(hwnd, 4)    # SW_SHOWNOACTIVATE
        self._ready.set()
        msg = wt.MSG()
        while u.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            u.TranslateMessage(ctypes.byref(msg))
            u.DispatchMessageW(ctypes.byref(msg))

    @staticmethod
    def _rgb(c):
        return int(c[0]) | (int(c[1]) << 8) | (int(c[2]) << 16)

    def set_colour(self, colour):
        """Changes the window's colour (the class background brush), repainted at once."""
        self.colour = tuple(colour)
        if self.hwnd:
            b = self._g.CreateSolidBrush(self._rgb(self.colour))
            self._u.SetClassLongPtrW(self.hwnd, -10, b)          # GCLP_HBRBACKGROUND
            self._u.InvalidateRect(self.hwnd, None, True)

    def window_rect(self):
        import ctypes
        import ctypes.wintypes as wt
        r = wt.RECT()
        if not self.hwnd or not ctypes.windll.user32.GetWindowRect(self.hwnd, ctypes.byref(r)):
            return None
        return (r.left, r.top, r.right - r.left, r.bottom - r.top)

    def close(self):
        if self.hwnd:
            import ctypes
            ctypes.windll.user32.PostMessageW(self.hwnd, 0x0010, 0, 0)     # WM_CLOSE
            if self._thread:
                self._thread.join(2.0)
            self.hwnd = 0

def _parse_colour(v):
    """de.calib colour: [r,g,b], "r,g,b", "#rrggbb"/"rrggbb", or "off" -> (off, rgb)."""
    if v is None or (isinstance(v, str) and v.strip().lower() in ("off", "none", "")):
        return True, None
    if isinstance(v, (list, tuple)) and len(v) == 3:
        return False, tuple(int(x) for x in v)
    t = str(v).strip().lstrip("#")
    if "," in t:
        return False, tuple(int(x) for x in t.split(","))
    if len(t) == 6:
        return False, (int(t[0:2], 16), int(t[2:4], 16), int(t[4:6], 16))
    raise ValueError("colour: [r,g,b], 'r,g,b', '#rrggbb' or 'off'")

class S2Host:
    """The SN plugin's S2 behaviour in the stand-in (see the module docstring, S2)."""
    PERF = {0: "Idle", 1: "Track", 2: "Full"}
    PHASE_OF = {0: "ReadyIdle", 1: "WarmingUp", 2: "Active"}

    def __init__(self, host, a):
        self.host, self.a = host, a
        self.c5 = None
        self.perf = 0
        self.frozen = True
        self.bus = 0.0
        self.t = time.perf_counter()
        self.win = None
        self.hwnd = 0
        self.rect = (0, 0, 0, 0)
        self.rect_ok = False
        self.calib = None
        self.last = {}
        self.sec_t = time.perf_counter()
        self.sec_frame = 0

    def start(self, sec):
        if self.a.s2_window:
            self.win = StandinWindow("DE_STANDIN_SN", self.a.s2_rect, (10, 40, 80))
            self.hwnd = self.win.start()
        sec.set_hdr("snHwnd", self.hwnd)
        self.host.emit("s2.window", "S2 stand-in window %s" % (hex(self.hwnd) if self.hwnd else "none"),
                       hwnd=self.hwnd, rect=list(self.a.s2_rect) if self.hwnd else None)

    def on_c5(self, v):
        self.c5 = v

    def recipe(self):
        return {0: (0, 10), 1: (0, 10), 2: (1, -1)}[self.perf]

    def fps(self):
        return self.a.fps if self.perf == 2 else 10.0

    def cameras_on(self):
        return self.perf == 2

    def phase(self, live):
        return self.PHASE_OF[self.perf] if live else "ReadyIdle"

    def frame(self, frame_no):
        v = self.c5
        h = self.host
        now = time.perf_counter()
        dt, self.t = now - self.t, now
        if v is None:
            return
        self.perf = int(v["perfMode"]) if v["perfMode"] in (0, 1, 2) else 0
        ov = int(v["overlays"])
        paused = bool(ov & (1 << L.OVERLAYS["Paused"]))
        unfocused = bool(ov & (1 << L.OVERLAYS["Unfocused"]))
        stalled = h.se_stalled or h.se_exited
        self.frozen = self.perf == 0 or paused or stalled
        target = 0.0 if (self.frozen or unfocused) else float(v["snBusTarget"])
        step = dt * 1000.0 / max(1.0, float(v["crossfadeMs"] or 300.0))
        self.bus = min(target, self.bus + step) if target > self.bus else max(target, self.bus - step)
        se_rect = tuple(v["seRect"])
        if self.win:
            self.rect = self.win.window_rect() or (0, 0, 0, 0)
        else:
            self.rect = se_rect
        self.rect_ok = tuple(self.rect) == se_rect and se_rect[2] > 0
        q = S.qpc()
        for key, val, ev in (("target", target, "s2.mix"), ("frozen", self.frozen, "s2.freeze"),
                             ("perf", self.perf, "s2.perf")):
            if self.last.get(key) != val:
                self.last[key] = val
                if ev == "s2.mix":
                    h.emit("s2.mix", qpc=q, busVolume=round(self.bus, 4), target=target, muted=target == 0.0,
                           paused=self.frozen, freeze=self.frozen)
                elif ev == "s2.freeze":
                    h.emit("s2.freeze", qpc=q, frozen=self.frozen, paused=paused, stalled=stalled)
                else:
                    self.second(q, frame_no)
        if now - self.sec_t >= 1.0:
            self.second(q, frame_no)

    def second(self, q, frame_no):
        now = time.perf_counter()
        fps = (frame_no - self.sec_frame) / max(1e-6, now - self.sec_t)
        self.sec_t, self.sec_frame = now, frame_no
        vs, tf = self.recipe()
        self.host.emit("s2.perf", qpc=q, snFrame=frame_no, fps=round(fps, 2), perfModeApplied=self.PERF[self.perf],
                       vSyncCount=vs, targetFrameRate=tf, frozen=self.frozen, timeScale=0 if self.frozen else 1,
                       camerasOn=self.cameras_on(), busVolume=round(self.bus, 4), rectOk=self.rect_ok,
                       snRect=list(self.rect), calib=self.calib)

    def c7_fields(self) -> dict:
        vs, tf = self.recipe()
        return {"perfModeApplied": self.perf, "vSyncCountApplied": vs, "targetFrameRateApplied": tf,
                "snHwnd": self.hwnd, "snRect": tuple(self.rect)}

    def command(self, cmd, args, rid):
        if cmd == "de.calib":
            try:
                off, rgb = _parse_colour((args or {}).get("colour") if isinstance(args, dict) else None)
            except (ValueError, TypeError) as e:
                return {"id": rid, "ok": False, "cmd": cmd, "error": str(e)}
            self.calib = None if off else list(rgb)
            if self.win:
                self.win.set_colour(rgb if not off else (10, 40, 80))
            self.host.emit("s2.calib", "DE.Calib %s" % ("OFF" if off else "ON colour=%s" % (rgb,)), qpc=S.qpc(),
                           on=not off, colour=self.calib)
            rep = {"id": rid, "ok": True, "cmd": cmd, "calib": "off" if off else "on"}
            if not off:
                rep.update(colour="#%02x%02x%02x" % rgb, r=rgb[0], g=rgb[1], b=rgb[2])
            return rep
        if cmd == "de.s2":
            vs, tf = self.recipe()
            return {"id": rid, "ok": True, "cmd": cmd, "perfMode": self.PERF[self.perf], "applied": [vs, tf],
                    "frozen": self.frozen, "busVolume": round(self.bus, 4), "snHwnd": hex(self.hwnd),
                    "snRect": list(self.rect), "rectOk": self.rect_ok, "calib": self.calib, "standin": True}
        if cmd == "de.biome":
            # review fix (finding 8): the S2.N6 direct query; emulated (no LargeWorld behind it)
            a = args if isinstance(args, dict) else {}
            try:
                x, y, z = float(a["x"]), float(a["y"]), float(a["z"])
            except (KeyError, TypeError, ValueError):
                return {"id": rid, "ok": False, "cmd": cmd, "error": "args {x, y, z}"}
            outside = x < -2048.0 or x >= 2048.0 or z < -2048.0 or z >= 2048.0
            return {"id": rid, "ok": True, "cmd": cmd, "x": x, "y": y, "z": z,
                    "biome": "void" if outside else "(stand-in)", "outsideTile": outside, "patched": True,
                    "answeredByPatch": outside, "emulated": True, "standin": True}
        return None

    def close(self):
        if self.win:
            self.win.close()

class S3View:
    """--s3: the SN plugin's S3 mirror bookkeeping without rendering (module docstring "S3"). The analyser lines are the
 events of tests/s3/analyze_s3.py's log contract (s3.winding, s3.mirror) and check_placement.py's SN schema
 (s3.placement); the winding rule is wire_ref's (the census rule), the placement is this class's own Apply."""

    C2_MAX_READ = 16                 # entities read per frame (the stand-in scenes have one or two grids)
    MAX_PLACEMENT_LINES = 400

    def __init__(self, host, a):
        wm = os.path.join(_REPO, "tools", "worldmap")
        if wm not in sys.path:
            sys.path.insert(0, wm)
        import wire_ref                   # noqa: E402 (the classify rule of CONTRACT 2.5 / S3.C3)
        self.wire_ref = wire_ref
        self.host, self.a = host, a
        self.mutant = a.s3_mutant
        self.mirror_on = a.s3_mutant != "M1"
        self.swap_on = a.s3_mutant != "M2"
        self.mirror_version = -1
        self.boxes = {}                   # grid id -> (SE-local min, max) of the probes of a built grid
        self.probes = {}                  # grid id -> 8 x (SE-local position, drawn Unity-local position)
        self.built = {}                   # grid id -> part signature last logged in s3.mirror
        self.last_pose = {}               # grid id -> pose of the last s3.placement line
        self.placements = 0
        self.windings = []
        self.c2 = {"fresh": 0, "busy": 0, "never": 0, "entities": 0, "tick": None}

    def emit(self, ev, msg=None, **kw):
        self.host.emit(ev, msg, **kw)

    # ---- per frame ------------------------------------------------------------------------------------------------
    def frame(self):
        h = self.host
        while h.models.completed_real:
            key = h.models.completed_real.pop(0)
            m = h.models.models.get(key)
            if m:
                self.on_model(m)
        if h.mirror.version != self.mirror_version or self.pending_grids():
            self.mirror_version = h.mirror.version
            self.on_mirror()
        self.read_c2()

    def pending_grids(self) -> bool:
        return any(g not in self.boxes for g in self.host.mirror.grids)

    def to_unity(self, m):
        """The mesh as the plugin draws it: positions and normals mirrored (x, y, -z), triangles (I0, I2, I1)."""
        mir = (lambda v: (v[0], v[1], -v[2])) if self.mirror_on else (lambda v: tuple(v))
        pos = [mir(p) for p in m["positions"]]
        nrm = [mir(n) for n in m["normals"]]
        tris = [(t[0], t[2], t[1]) for t in m["triangles"]] if self.swap_on else list(m["triangles"])
        return pos, nrm, tris

    @staticmethod
    def tangent_census(m):
        """S3.N1t: per non-degenerate triangle, whether the stored tangents run along +dP/du
 and the binormals along +dP/dv (the SN plugin's TangentCensus): (measured, tangentAlongU, binormalAlongV)."""
        pos, tan, bn, uv = m.get("positions"), m.get("tangents"), m.get("binormals"), m.get("uvs")
        if not (pos and tan and bn and uv):
            return 0, 0, 0
        meas = tu = bv = 0
        for a, b, c in m["triangles"]:
            e1 = [pos[b][k] - pos[a][k] for k in range(3)]
            e2 = [pos[c][k] - pos[a][k] for k in range(3)]
            du1, dv1 = uv[b][0] - uv[a][0], uv[b][1] - uv[a][1]
            du2, dv2 = uv[c][0] - uv[a][0], uv[c][1] - uv[a][1]
            det = du1 * dv2 - du2 * dv1
            if abs(det) < 1e-12:
                continue
            t = [(e1[k] * dv2 - e2[k] * dv1) / det for k in range(3)]
            bb = [(e2[k] * du1 - e1[k] * du2) / det for k in range(3)]
            tl, bl = math.sqrt(sum(x * x for x in t)), math.sqrt(sum(x * x for x in bb))
            if tl < 1e-12 or bl < 1e-12:
                continue
            st = sum(sum(tan[v][k] * t[k] for k in range(3)) for v in (a, b, c))
            sb = sum(sum(bn[v][k] * bb[k] for k in range(3)) for v in (a, b, c))
            if abs(st) < 1e-9 * tl and abs(sb) < 1e-9 * bl:
                continue
            meas += 1
            tu += 1 if st > 0 else 0
            bv += 1 if sb > 0 else 0
        return meas, tu, bv

    def on_model(self, m):
        pos, nrm, tris = self.to_unity(m)
        agree, disagree, degen = self.wire_ref.winding_measure(pos, nrm, tris)
        frac = self.wire_ref.winding_fraction((agree, disagree, degen))
        flipped = frac < 0.01
        self.windings.append(frac)
        meas, tu, bv = self.tangent_census(m)
        tfrac = tu / float(meas) if meas else None
        bfrac = bv / float(meas) if meas else None
        self.emit("s3.winding", "DE.Mirror winding model=0x%016x agree=%d total=%d frac=%.4f flipped=%s tangentU=%s "
                  "binormalV=%s tangentTris=%d (raw measure, before the automatic flip; stand-in)" % (
                      m["key"], agree, agree + disagree, frac, flipped, "-" if tfrac is None else "%.4f" % tfrac,
                      "-" if bfrac is None else "%.4f" % bfrac, meas),
                  model="0x%016x" % m["key"], agree=agree, disagree=disagree, degenerate=degen, total=agree + disagree,
                  frac=frac, flipped=flipped, verts=m["verts"], tris=m["tris"], submeshes=len(m["submeshes"]),
                  patternScale=m["patternScale"], mirrorZ=self.mirror_on, triSwap=self.swap_on, mutant=self.mutant,
                  tangentTris=meas, tangentFrac=tfrac, binormalFrac=bfrac)

    def grid_parts(self, gid):
        return sorted(((pk, info) for (e, pk), info in self.host.mirror.part_info.items() if e == gid),
                      key=lambda x: x[0])

    def part_ready(self, info) -> bool:
        mm = self.host.models
        m = mm.models.get(info["modelKey"])
        return m is not None and all(sm["materialKey"] in mm.materials for sm in m["submeshes"])

    def on_mirror(self):
        mm = self.host.models
        for gid in list(self.boxes):
            if gid not in self.host.mirror.grids:
                del self.boxes[gid]
                self.built.pop(gid, None)
                self.last_pose.pop(gid, None)
        for gid in list(self.host.mirror.grids):
            parts = self.grid_parts(gid)
            if not parts or not all(self.part_ready(info) for _pk, info in parts):
                continue
            sig = tuple((pk, info["modelKey"], tuple(info["local"])) for pk, info in parts)
            if self.built.get(gid) == sig:
                continue
            verts = tris = drawn = 0
            probes = [None] * 8              # (score, SE-local, drawn Unity-local) per corner direction
            mir = (lambda v: (v[0], v[1], -v[2])) if self.mirror_on else (lambda v: tuple(v))
            for _pk, info in parts:
                m = mm.models[info["modelKey"]]
                verts += m["verts"]
                tris += m["tris"]
                drawn += self.drawn_triangles(m)
                r = info["local"]
                for c in m["positions"]:
                    # SE-local (independent of the drawing: rows applied in SE space) and drawn (mirrored unless S3.M1)
                    p = tuple(c[0] * r[k] + c[1] * r[3 + k] + c[2] * r[6 + k] + r[9 + k] for k in range(3))
                    d = mir(p)
                    for i in range(8):
                        sg = (1 if i & 1 else -1, 1 if i & 2 else -1, 1 if i & 4 else -1)
                        score = p[0] * sg[0] + p[1] * sg[1] + p[2] * sg[2]
                        if probes[i] is None or score > probes[i][0] + 1e-9:
                            probes[i] = (score, p, d)
            lo = tuple(min(pr[1][k] for pr in probes) for k in range(3))
            hi = tuple(max(pr[1][k] for pr in probes) for k in range(3))
            self.built[gid] = sig
            self.boxes[gid] = (lo, hi)
            self.probes[gid] = [(pr[1], pr[2]) for pr in probes]
            self.last_pose.pop(gid, None)
            self.emit("s3.mirror", "DE.Mirror built grid=0x%016x parts=%d verts=%d tris=%d gridParts=%d gridVerts=%d "
                      "gridTris=%d gridModelVerts=%d gridModelTris=%d (stand-in: drawn triangles counted by Wire's "
                      "coverage rule, no mesh drawn)" % (gid, len(parts), verts, tris, len(parts), verts, drawn, verts,
                                                         tris),
                      grid="0x%016x" % gid, parts=len(parts), verts=verts, tris=tris, gridParts=len(parts),
                      gridVerts=verts, gridTris=drawn, gridModelVerts=verts, gridModelTris=tris, localMin=list(lo),
                      localMax=list(hi), mutant=self.mutant)

    @staticmethod
    def drawn_triangles(m):
        """Triangles drawn by Wire's coverage rule (MeshConventions.UnityMeshBuilder): each model triangle exactly once
 (the first submesh of an overlap; triangles outside every range with material 0)."""
        covered = [False] * m["tris"]
        for sm in m["submeshes"]:
            for t in range(sm["triStart"], min(m["tris"], sm["triStart"] + sm["triCount"])):
                covered[t] = True
        return sum(1 for c in covered if c) + sum(1 for c in covered if not c)

    @staticmethod
    def frame_entity(o, q, s, k, u):
        """The Frame (o, scale (s, 1, s)) / Entity (q, k) hierarchy of CONTRACT 2.3 applied to a Unity-local (drawn,
 already mirrored) point, computed here (not wire_ref) from the C2 floats: o + S*(q*(k*u))."""
        v = (u[0] * k, u[1] * k, u[2] * k)
        r = _q_rot(q, v)
        return (o[0] + s * r[0], o[1] + r[1], o[2] + s * r[2])

    def read_c2(self):
        h = self.host
        size = 0x40 + self.C2_MAX_READ * 96
        st, _seq, d = S.seqlock_read(h.sec, L.REGION["C2"].offset, size)
        self.c2[st if st in self.c2 else "busy"] = self.c2.get(st, 0) + 1
        if not d:
            return
        hdr = L.RECORDS["SeEntitiesHeader"].unpack(d)
        self.c2["tick"] = hdr["tick"]
        for i in range(min(hdr["entityCount"], self.C2_MAX_READ)):
            e = L.RECORDS["SeEntity"].unpack(d, 0x40 + 96 * i)
            gid = e["entityId"]
            box = self.boxes.get(gid)
            if box is None:
                continue
            self.c2["entities"] += 1
            q = tuple(e["q"])
            n = math.sqrt(sum(x * x for x in q)) or 1.0
            q = tuple(x / n for x in q)
            k = e["k"] if e["k"] else 1.0
            pose = (tuple(e["o"]), q, e["s"], k)
            if self.last_pose.get(gid) == pose or self.placements >= self.MAX_PLACEMENT_LINES:
                continue
            self.last_pose[gid] = pose
            self.placements += 1
            lo, hi = box
            probes = self.probes.get(gid) or []
            # the drawn vertices through this host's Frame/Entity (no mirror here: the drawn positions carry it)
            corners = [list(self.frame_entity(pose[0], q, pose[2], k, d)) for _l, d in probes]
            self.emit("s3.placement", entityId=str(gid), grid="0x%016x" % gid, seTick=hdr["tick"], corners=corners,
                      localCorners=[list(l_) for l_, _d in probes], localMin=list(lo), localMax=list(hi),
                      pose={"o": list(pose[0]), "q": list(q), "s": pose[2], "k": k}, mutant=self.mutant)

    # ---- C11 and exit ---------------------------------------------------------------------------------------------
    def command(self, args, rid):
        args = args if isinstance(args, dict) else {}
        action = str(args.get("action", "")).lower()
        h = self.host
        if action == "status":
            return {"id": rid, "ok": True, "line": "de.s3 status (stand-in)", "emulated": True,
                    "grids": len(h.mirror.grids), "parts": len(h.mirror.parts), "pendingParts": h.pending(),
                    "models": len(h.models.models), "materials": len(h.models.materials),
                    "misparse": h.models.misparse + h.mirror.misparse, "c3Bytes": h.c3_bytes,
                    "built": len(self.boxes), "placementLines": self.placements, "c2": dict(self.c2),
                    "mutant": self.mutant}
        if action == "placement":
            self.last_pose.clear()          # one placement line per built grid at the next C2 read
            return {"id": rid, "ok": True, "line": "de.s3 placement (stand-in)", "grids": len(self.boxes),
                    "emulated": True}
        return {"id": rid, "ok": False, "error": "unknown de.s3 action %s (the stand-in host renders nothing: no probe, "
                                                 "lab, shear, frametime, count or teardown)" % (action or
                                                                                               sorted(args))}

    def summary(self):
        h = self.host
        self.emit("s3.summary", "DE.S3 stand-in host summary: models=%d materials=%d grids=%d parts=%d pending=%d "
                  "misparse=%d placementLines=%d" % (len(h.models.models), len(h.models.materials), len(h.mirror.grids),
                                                     len(h.mirror.parts), h.pending(),
                                                     h.models.misparse + h.mirror.misparse, self.placements),
                  models=len(h.models.models), materials=len(h.models.materials), grids=len(h.mirror.grids),
                  parts=len(h.mirror.parts), pending=h.pending(), misparse=h.models.misparse + h.mirror.misparse,
                  details=(h.models.details + h.mirror.details)[:10], placementLines=self.placements,
                  windings=self.windings, c2=dict(self.c2), mutant=self.mutant)

class S6Host:
    """The Subnautica side of S6 in the stand-in (module docstring S6): the C4 type 6 SkinSet consumer (decoded and
 validated with tests/standins/s6_stream.py, diffed by section id and content hash, stale versions counted), the
 targets it knows (GridAdd targetClass and parent, the C1 / CharacterAdd character), the C8 damage producer of the
 plans n1 / n10 / stream through the overflow queue, the emulated Freedom gate and self-check, and de.s6 / de.spawn."""

    def __init__(self, host, a):
        import s6_stream
        self.host, self.a, self.X = host, a, s6_stream
        self.grids = {}              # entityId -> {"cls", "parent", "epoch"}
        self.skins = {}              # entityId -> {"version", "sections"}
        self.counts = {"skinMessages": 0, "skinStale": 0, "skinBad": 0, "created": 0, "updated": 0, "removed": 0,
                       "kind2Ignored": 0, "plans": 0, "planned": 0, "streamed": 0}
        self.c1 = None
        self.character = None        # (id, present, dead, seated)
        self.char_add = None
        self.c1_tick = 0
        self.last_skin_t = None
        self.plan_mtime = None
        self.auto_done = False
        self.stream_acc = 0.0
        self.gate_logged = False
        self.plans = []

    def attach(self, sec):
        self.c1 = S.SeqlockReader(sec, L.REGION["C1"].offset, L.SE_FRAME)

    def emit(self, ev, msg=None, **kw):
        self.host.emit(ev, msg, **kw)

    # ---- C4 -------------------------------------------------------------------------------------------------------
    def on_c4(self, mtype, payload):
        t = L.C4_TYPES
        R = L.RECORDS
        if mtype == t["GridAdd"] and len(payload) == 32:
            v = R["GridAdd"].unpack(payload)
            self.grids[v["entityId"]] = {"cls": v["targetClass"], "parent": v["parentEntityId"], "epoch": v["worldEpoch"]}
        elif mtype == t["EntityRemove"] and len(payload) == 8:
            gid = struct.unpack_from("<Q", payload, 0)[0]
            self.grids.pop(gid, None)
            if self.skins.pop(gid, None) is not None:
                self.emit("s6.skin", entity="0x%016x" % gid, version=0, sections=0, boxes=0, removed=-1, why="remove")
        elif mtype == t["ClearAll"]:
            self.grids.clear()
            self.skins.clear()
        elif mtype == t["CharacterAdd"] and len(payload) == 24:
            self.char_add = R["CharacterAdd"].unpack(payload)["entityId"]
        elif mtype == t["SkinSet"]:
            self.on_skin(payload)

    def on_skin(self, payload):
        X = self.X
        self.counts["skinMessages"] += 1
        h, secs, err = X.decode_skin(payload)
        if err:
            self.counts["skinBad"] += 1
            self.emit("s6.skin", "DE.S6 skin BAD %s" % err, entity="0x%016x" % h["entityId"] if h else None,
                      version=h["skinVersion"] if h else None, bad=True, error=err, length=len(payload))
            return
        gid = h["entityId"]
        old = self.skins.get(gid)
        if old is not None and h["skinVersion"] <= old["version"]:
            self.counts["skinStale"] += 1
            self.emit("s6.skin", entity="0x%016x" % gid, version=h["skinVersion"], stale=True,
                      applied=old["version"], sections=len(secs), boxes=sum(len(s["boxes"]) for s in secs))
            return
        before = {s["id"]: s["hash"] for s in old["sections"]} if old else {}
        after = {s["id"]: s["hash"] for s in secs}
        created = sum(1 for k in after if k not in before)
        updated = sum(1 for k in after if k in before and before[k] != after[k])
        removed = sum(1 for k in before if k not in after)
        self.counts["created"] += created
        self.counts["updated"] += updated
        self.counts["removed"] += removed
        self.skins[gid] = {"version": h["skinVersion"], "sections": secs}
        self.last_skin_t = time.monotonic()
        g = self.grids.get(gid)
        self.emit("s6.skin", entity="0x%016x" % gid, version=h["skinVersion"], sections=len(secs),
                  boxes=sum(len(s["boxes"]) for s in secs), created=created, updated=updated, removed=removed,
                  stale=False, bad=False, bytes=len(payload), known=g is not None,
                  cls=g["cls"] if g else None, parent="0x%016x" % g["parent"] if g and g["parent"] else None,
                  detail=[{"id": "0x%08x" % s["id"], "n": len(s["boxes"]), "hash": "0x%016x" % s["hash"]} for s in secs])

    # ---- targets ----------------------------------------------------------------------------------------------------
    def poll_c1(self):
        if not self.c1:
            return
        if self.c1.poll() == S.SEQ_FRESH and self.c1.value:
            v = self.c1.value
            f = v["flags"]
            bit = lambda n: bool(f & (1 << L.SE_FRAME_FLAGS[n]))  # noqa: E731
            self.c1_tick = v["tick"]
            cid = v["characterId"] or 0
            self.character = (cid, bit("characterPresent") or bool(cid), bit("characterDead"), bit("characterSeated"))

    def character_id(self):
        if self.character and self.character[0]:
            return self.character[0]
        return self.char_add or 0

    def target(self, entity, kind=None):
        X = self.X
        eid = _int_id(entity)
        if kind == "character" or (kind is None and eid == self.character_id() and eid):
            if not eid:
                raise ValueError("no character id (C1 characterId 0 and no CharacterAdd)")
            return X.Target(eid, "character")
        if eid not in self.skins:
            raise ValueError("entity 0x%016x has no SkinSet here (known: %s)" % (eid, self.known()))
        g = self.grids.get(eid, {"cls": 1, "parent": 0})
        return X.Target(eid, "grid", g["cls"], g["parent"], self.skins[eid]["sections"])

    def known(self):
        return ["0x%016x cls %s" % (gid, (self.grids.get(gid) or {}).get("cls")) for gid in sorted(self.skins)]

    def auto_targets(self, with_char=True):
        out = []
        for want in (2, 1):          # one small, one large grid (the S6.N1 set)
            gid = next((g for g in sorted(self.skins) if (self.grids.get(g) or {}).get("cls") == want), None)
            if gid is not None:
                out.append(self.target(gid, "grid"))
        if with_char and self.character_id():
            out.append(self.X.Target(self.character_id(), "character"))
        return out

    def resolve_targets(self, spec):
        """targets: "auto" | [{entity, kind} | "<id>"] | {small, large, character: id | "auto" | true}."""
        if spec in (None, "auto"):
            return self.auto_targets()
        out = []
        if isinstance(spec, dict):
            for role in ("small", "large", "character"):
                v = spec.get(role)
                if v in (None, False, ""):
                    continue
                if role == "character":
                    out.append(self.target(self.character_id() if v in (True, "auto") else v, "character"))
                elif v == "auto":
                    cls = 2 if role == "small" else 1
                    gid = next((g for g in sorted(self.skins) if (self.grids.get(g) or {}).get("cls") == cls), None)
                    if gid is None:
                        raise ValueError("no %s grid with a skin (known: %s)" % (role, self.known()))
                    out.append(self.target(gid, "grid"))
                else:
                    out.append(self.target(v, "grid"))
            return out
        for item in spec:
            if isinstance(item, dict):
                out.append(self.target(item.get("entity"), item.get("kind")))
            else:
                out.append(self.target(item))
        return out

    # ---- plans --------------------------------------------------------------------------------------------------------
    def next_seq(self):
        self.host.damage_seq += 1
        return self.host.damage_seq

    def run_plan(self, mode, spec=None, unique=30, dups=None, seed=None, origin="command"):
        X = self.X
        h = self.host
        seed = self.a.damage_seed if seed is None else int(seed)
        if mode == "n1":
            targets = self.resolve_targets(spec)
            if not targets:
                raise ValueError("n1: no target (known skins: %s, character %s)" % (self.known(), self.character_id()))
            if isinstance(dups, dict):
                # keys: a role (small | large | character) or an entity id
                roles = {"small": 2, "large": 1}
                dd = {}
                for t in targets:
                    for k, v in dups.items():
                        if k == "character":
                            hit = t.kind == "character"
                        elif k in roles:
                            hit = t.kind == "grid" and roles[k] == t.cls
                        else:
                            try:
                                hit = _int_id(k) == t.entity
                            except ValueError:
                                hit = False
                        if hit:
                            dd[t.entity] = int(v)
                dups = dd
            recs, plan = X.plan_n1(targets, h.generation, h.world_epoch, self.next_seq, seed=seed, unique=int(unique),
                                   dups=dups, tick=self.c1_tick, sn_time=time.perf_counter())
        elif mode == "n10":
            grids = [t for t in self.resolve_targets(spec) if t.kind == "grid"] if spec not in (None, "auto") else \
                [self.target(g, "grid") for g in sorted(self.skins)]
            cid = self.character_id()
            char = X.Target(cid, "character") if cid else None
            recs, plan = X.plan_n10(grids, char, h.generation, h.world_epoch, self.next_seq, seed=seed,
                                    tick=self.c1_tick, sn_time=time.perf_counter())
        else:
            raise ValueError("mode n1 | n10 (stream runs from --damage stream)")
        for rec, _m in recs:
            h.damage_q.enqueue(rec)
        self.counts["plans"] += 1
        self.counts["planned"] += len(recs)
        plan.update(origin=origin, queued=len(recs), qpc=S.qpc())
        self.plans.append(plan)
        self.emit("s6.plan", "DE.S6 stand-in plan %s: %d records (%s)" % (mode, len(recs), origin), **plan)
        return plan

    # ---- per frame --------------------------------------------------------------------------------------------------
    def frame(self):
        a, h = self.a, self.host
        self.poll_c1()
        if not self.gate_logged and h.phase >= L.SN_PHASE["ReadyIdle"] and h.phase != L.SN_PHASE["Quitting"]:
            self.gate_logged = True
            self.emit("s6.gate", "DE.S6 freedom mode=Freedom invisible=false PASS (stand-in emulation)", mode="Freedom",
                      cheats=0, invisible=False, emulated=True, **{"pass": True})
            self.emit("s6.selftest", "DE.S6 damage self-check PASS (stand-in emulation)", sectionRecords=1,
                      playerRecords=1, emulated=True, **{"pass": True})
        live = h.hello_ack and not h.se_exited and h.world_epoch
        if live and a.damage_plan:
            try:
                mt = os.path.getmtime(a.damage_plan)
            except OSError:
                mt = None
            if mt is not None and mt != self.plan_mtime:
                self.plan_mtime = mt
                try:
                    with open(a.damage_plan, encoding="utf-8") as f:
                        spec = json.load(f)
                    self.run_plan(str(spec.get("mode", a.damage or "n1")), spec.get("targets", "auto"),
                                  spec.get("unique", 30), spec.get("dups"), spec.get("seed"), origin="plan file")
                except (OSError, ValueError, TypeError, KeyError) as e:
                    self.emit("s6.plan", "DE.S6 stand-in plan file %s refused: %s" % (a.damage_plan, e), error=str(e))
        if live and a.damage_auto and not self.auto_done and a.damage in ("n1", "n10") and self.skins and \
                self.last_skin_t is not None and time.monotonic() - self.last_skin_t >= 3.0:
            self.auto_done = True
            try:
                self.run_plan(a.damage, "auto", origin="auto")
            except ValueError as e:
                self.emit("s6.plan", "DE.S6 stand-in auto plan refused: %s" % e, error=str(e))
        if live and a.damage == "stream" and self.skins:
            self.stream_acc += a.damage_stream_rate / h.a.fps
            targets = None
            while self.stream_acc >= 1.0:
                self.stream_acc -= 1.0
                targets = targets or self.auto_targets()
                if not targets:
                    break
                t = targets[self.counts["streamed"] % len(targets)]
                recs, _p = self.X.plan_n1([t], h.generation, h.world_epoch, self.next_seq, seed=self.counts["streamed"],
                                          unique=1, dups=0, tick=self.c1_tick, sn_time=time.perf_counter())
                h.damage_q.enqueue(recs[0][0])
                self.counts["streamed"] += 1
        h.damage_sent += h.damage_q.flush_to(h.c8)

    def command(self, args, rid):
        args = args if isinstance(args, dict) else {}
        act = str(args.get("action", "status"))
        try:
            if act == "status":
                cid = self.character_id()
                return {"id": rid, "ok": True, "line": "de.s6 status", "emulated": True,
                        "gate": {"mode": "Freedom", "invisible": False, "pass": True, "emulated": True},
                        "selfTest": True, "skins": len(self.skins), "counts": dict(self.counts),
                        "character": {"id": "0x%016x" % cid if cid else None, "present": bool(self.character and
                                                                                             self.character[1])},
                        "damage": {"sent": self.host.damage_sent, "queued": len(self.host.damage_q.items),
                                   "dropped": self.host.damage_q.dropped, "seq": self.host.damage_seq}}
            if act == "skins":
                out = []
                for gid in sorted(self.skins):
                    g = self.grids.get(gid) or {}
                    sk = self.skins[gid]
                    out.append({"entity": "0x%016x" % gid, "cls": g.get("cls"), "parent": "0x%016x" % g.get("parent", 0),
                                "large": g.get("cls") == 1, "version": sk["version"], "sections": [
                                    {"id": "0x%08x" % s["id"], "c": list(s["centre"]), "n": len(s["boxes"]),
                                     "hash": "0x%016x" % s["hash"]} for s in sk["sections"]],
                                "boxes": sum(len(s["boxes"]) for s in sk["sections"])})
                cid = self.character_id()
                return {"id": rid, "ok": True, "line": "de.s6 skins", "skins": out,
                        "character": "0x%016x" % cid if cid else None}
            if act == "damage":
                plan = self.run_plan(str(args.get("mode", "n1")), args.get("targets", "auto"), args.get("unique", 30),
                                     args.get("dups"), args.get("seed"))
                return {"id": rid, "ok": True, "line": "de.s6 damage", "plan": plan}
            if act == "selftest":
                self.emit("s6.selftest", "DE.S6 damage self-check PASS (stand-in emulation)", sectionRecords=1,
                          playerRecords=1, emulated=True, **{"pass": True})
                return {"id": rid, "ok": True, "line": "de.s6 selftest", "pass": True, "emulated": True}
            return {"id": rid, "ok": False, "line": "de.s6 standin",
                    "error": "de.s6 %s: the stand-in host takes status | skins | damage | selftest" % act}
        except (ValueError, TypeError, KeyError) as e:
            return {"id": rid, "ok": False, "line": "de.s6 %s" % act, "error": str(e)}

    def summary(self):
        self.emit("s6.summary", "DE.S6 stand-in host: %d skins, %d plans, %d records planned, %d sent, %d dropped" % (
            len(self.skins), self.counts["plans"], self.counts["planned"], self.host.damage_sent,
            self.host.damage_q.dropped), counts=dict(self.counts), sent=self.host.damage_sent,
            dropped=self.host.damage_q.dropped)

def _int_id(v) -> int:
    if v is None or isinstance(v, bool):
        return 0
    if isinstance(v, int):
        return v & 0xFFFFFFFFFFFFFFFF
    if isinstance(v, float):
        return int(v)
    s = str(v).strip()
    return (int(s, 16) if s.lower().startswith("0x") else int(s)) & 0xFFFFFFFFFFFFFFFF

class Host:
    def __init__(self, a):
        self.a = a
        self.emit = ST.Emitter("sn", quiet=a.quiet, human=a.human)
        self.name = a.name
        self.mutex_sn, _mutex_se, self.launcher_object = L.mutex_names(a.name)
        self.pid = a.pid_override or S.current_pid()
        self.sec = None
        self.mutex = None
        self.generation = 0
        self.phase = 0
        self.t0 = time.monotonic()
        self.frame_no = 0
        # SE peer tracking
        self.se_key = None
        self.se_watch = None
        self.se_exited = False
        self.se_stalled = False
        self.hello_ack = 0
        self.link_ack = None
        self.c5 = None
        self.c5_overlays = 0
        self.world_epoch = 0
        # rings
        self.c3 = self.c4 = self.c8 = self.c11c = self.c11d = None
        self.models = ST.ModelAssembler(real=a.s3)
        self.mirror = ST.Mirror(real=a.s3)
        self.damage_q = S.OverflowQueue()
        self.damage_seq = 0
        self.damage_acc = 0.0
        self.damage_sent = 0
        self.c11_handled = 0
        self.quit_requested = False
        self.quit_at = None   # S8-standin: --quit-delay
        self.stall_request = 0.0
        self.last_hb_qpc = None
        self.last_stats = 0.0
        self.c3_bytes = 0
        self.view = S1View(self, a) if a.s1 else None
        self.s2 = S2Host(self, a) if a.s2 else None
        self.s3 = S3View(self, a) if a.s3 else None
        self.s6 = S6Host(self, a) if a.s6 else None
        self.se_in_world = False
        self.start_wall = time.time()

    # ------------------------------------------------------------------------------------------------- start-up
    def start(self) -> int:
        a = self.a
        self.emit("hello", "DE.Hello fake_sn_host wire v%d layout %d.%d hash %s site v%d" % (
            L.WIRE_VERSION, L.LAYOUT_MAJOR, L.LAYOUT_MINOR, L.hex64(L.layout_hash()), L.SITE_VERSION),
            name=self.name, layoutHash=L.hex64(L.layout_hash()), mutant=a.mutant)
        errs = L.selftest(verbose=False)
        if errs:
            self.emit("failed", "DE.Bridge FAILED layout selftest: %s" % errs[0])
            return EXIT_FAILED
        if not a.no_token_check:
            env = os.environ.get(L.BRIDGE_ENV)
            present, err = S.named_object_present(self.launcher_object)
            if env == "1":
                which = "env %s=1" % L.BRIDGE_ENV
            elif present:
                which = "named object %s" % self.launcher_object
            else:
                self.emit("dormant", "DE.Bridge DORMANT (no bridge token)", objectError=err)
                return EXIT_DORMANT
            self.emit("token", "DE.Bridge token: %s" % which)
        if a.mutant not in FOREIGN_CREATOR_MUTANTS:
            st, m, err = S.NamedMutex.try_acquire(self.mutex_sn)
            if st == "held":
                self.emit("refuse", "DE.Link REFUSE second instance", reason="mutex", mutex=self.mutex_sn)
                return EXIT_REFUSED
            if st == "error":
                self.emit("refuse", "DE.Link REFUSE mutex error %d" % err, reason="mutex-error", err=err)
                return EXIT_REFUSED
            self.mutex = m
            self.emit("mutex", "mutex %s %s" % (self.mutex_sn, st), mutex=self.mutex_sn, status=st)
        rc = self.create_or_attach()
        if rc != EXIT_OK:
            self.release()
            return rc
        self.c3 = S.ByteRing(self.sec, "C3")
        self.c4 = S.ByteRing(self.sec, "C4")
        self.c11c = S.ByteRing(self.sec, "C11c")
        self.c11d = S.ByteRing(self.sec, "C11d")
        self.c8 = S.EntryRing(self.sec, "C8")
        self.models.on_resync()
        if self.s6:
            self.s6.attach(self.sec)
        if self.view:
            self.view.attach(self.sec)
        if self.s2:
            self.s2.start(self.sec)
        return EXIT_OK

    def create_or_attach(self) -> int:
        a = self.a
        size = MUTANT_SIZE if a.mutant == "size" else a.size
        try:
            sddl = S.section_sddl_for_current_user()
            self.sec, existed = S.Section.create(self.name, size, sddl)
        except OSError as e:
            self.emit("failed", "DE.Link FAILED %s" % e)
            return EXIT_FAILED
        try:
            sd = S.handle_sddl(self.sec.handle)
        except OSError as e:
            sd = "unreadable (%s)" % e
        if not existed:
            self.init_new(size)
            self.emit("created", "DE.Link created %s generation=%d" % (self.name, self.generation),
                      generation=self.generation, sddl=sd, mappedSize=self.sec.mapped_size,
                      heads=self.sec.ring_heads(), tails=self.sec.ring_tails())
            return EXIT_OK
        refusal = self.check_existing()
        if refusal:
            field, exp, found, why = refusal
            self.emit("refuse", "DE.Link REFUSE field=%s expected=%s found=%s%s" % (
                field, exp, found, (" (%s)" % why) if why else ""), field=field, expected=exp, found=found)
            self.sec.close()
            self.sec = None
            return EXIT_REFUSED
        before_heads = self.sec.ring_heads()
        self.reinitialise()
        self.emit("reinitialised", "DE.Link reinitialised %s generation=%d" % (self.name, self.generation),
                  generation=self.generation, sddl=sd, headsBefore=before_heads, heads=self.sec.ring_heads(),
                  tails=self.sec.ring_tails())
        return EXIT_OK

    @property
    def wire_version(self) -> int:
        """This host's own wireVersion (2 for the S0.M1 `wire2` seeded host)."""
        return 2 if self.a.mutant == "wire2" else L.WIRE_VERSION

    def _write_identity(self, size):
        a, s = self.a, self.sec
        s.set_hdr("layoutMajor", 2 if a.mutant == "layout2" else L.LAYOUT_MAJOR)
        s.set_hdr("layoutMinor", L.LAYOUT_MINOR)
        s.set_hdr("byteOrderMark", L.BYTE_ORDER_MARK)
        s.set_hdr("headerSize", L.HEADER_SIZE)
        s.set_hdr("sectionSize", size)
        s.set_hdr("layoutHash", L.layout_hash())
        s.set_hdr("wireVersion", self.wire_version)
        s.set_hdr("siteVersion", L.SITE_VERSION)
        s.set_hdr("damageScaleVersion", L.DAMAGE_SCALE_VERSION)
        s.set_hdr("regionCount", len(L.REGIONS))
        s.set_hdr("qpcFrequency", S.QPC_FREQUENCY)
        s.set_hdr("devControl", 0)
        s.write(L.REGION_TABLE_OFFSET, L.region_table_bytes())

    def _write_sn_block(self, generation):
        s = self.sec
        s.set_hdr("snHeartbeatMs", S.tick64())
        s.set_identity("sn", self.pid, generation)    # PID + generation as one u64
        s.set_hdr("snFlags", 1 << L.SN_FLAGS["bridgeMode"])
        s.set_hdr("snHwnd", 0)

    def _publish_magic(self):
        self.sec.set_hdr("magic", MAGIC_DEEQ if self.a.mutant == "magic" else L.MAGIC)

    def init_new(self, size):
        """Rule 1: zero-filled by the OS; header and region table, SN block, magic last."""
        self._write_identity(size)
        self.set_phase(L.SN_PHASE["Booting"], emit=False)
        self.generation = 1
        self._write_sn_block(self.generation)
        self.rewrite_own_records()
        self._publish_magic()

    def check_existing(self):
        """Rule 2: magic, layoutMajor, sectionSize (word and mapped size), layoutHash must match; then every other
 identity word of validation steps 1-5 (byteOrderMark, headerSize, wireVersion, siteVersion, damageScaleVersion,
 qpcFrequency) and the region table must be this host's own;
 a live recorded snPid refuses. Reads only. A recorded snPid equal to our own PID is a reused PID (that process
 is dead)."""
        s = self.sec
        if s.mapped_size < L.HEADER_SIZE:
            return "sectionSize", hex(L.SECTION_SIZE), hex(s.mapped_size) + " (mapped)", ""
        m = s.hdr("magic")
        if m != L.MAGIC:
            return "magic", hex(L.MAGIC), hex(m), ""
        major = s.hdr("layoutMajor")
        if major != L.LAYOUT_MAJOR:
            return "layoutMajor", str(L.LAYOUT_MAJOR), str(major), ""
        size = s.hdr("sectionSize")
        if size != L.SECTION_SIZE:
            return "sectionSize", hex(L.SECTION_SIZE), hex(size), ""
        if s.mapped_size != L.SECTION_SIZE:
            return "sectionSize", hex(L.SECTION_SIZE), hex(s.mapped_size) + " (mapped)", ""
        h = s.hdr("layoutHash")
        if h != L.layout_hash():
            return "layoutHash", L.hex64(L.layout_hash()), L.hex64(h), ""
        if s.hdr("wireVersion") != self.wire_version:
            return "wireVersion", str(self.wire_version), str(s.hdr("wireVersion")), \
                "a creator of another version holds the section"
        r = S.validate(s, selftest_pass=True, check_region_table=True)
        if r["outcome"] != "ok" and not (r["field"] == "wireVersion" and s.hdr("wireVersion") == self.wire_version):
            return r["field"], r["expected"], r["found"], "validation step %d on the existing section" % r["step"]
        pid = s.hdr("snPid")
        if pid and pid != self.pid and S.is_process_alive(pid):
            return "snPid", "a dead process", "%d (alive)" % pid, "the recorded Subnautica process is alive"
        return None

    def reinitialise(self):
        """Rule 3: snPhase Booting, magic:= 0, SN block (generation found + 1), tails of C3/C4/C11c:= heads, keep
 every producer head and seqlock seq, rewrite own seqlock records in place, magic last."""
        s = self.sec
        self.set_phase(L.SN_PHASE["Booting"], emit=False)
        s.set_hdr("magic", 0)
        found = s.hdr("snGeneration")
        self.generation = (found + 1) & S.MASK32 or 1
        self._write_sn_block(self.generation)
        if self.a.mutant == "zero-heads":   # S0.M7: the forbidden reset
            for r in L.RINGS:
                s.set_u64(L.ring_head_offset(r.key), 0)
        s.resync_tails(L.SN_CONSUMES)
        self.rewrite_own_records()
        self._publish_magic()

    def rewrite_own_records(self):
        s = self.sec
        S.seqlock_write(s, L.REGION["C7"].offset, self.c7_bytes())
        S.seqlock_write(s, L.REGION["C9"].offset, L.SN_CREATURES_HEADER.pack(snFrame=0))
        S.seqlock_write(s, L.REGION["C10"].offset + L.C10_RESULT_OFFSET, L.CAPTURE_RESULT.pack())

    def release(self):
        if self.sec:
            self.sec.close()
            self.sec = None
        if self.mutex:
            self.mutex.release()
            self.mutex = None

    # ---------------------------------------------------------------------------------------------------- state
    def set_phase(self, p, emit=True):
        self.phase = p
        if self.sec:
            self.sec.set_hdr("snPhase", p)
        if emit:
            self.emit("phase", "snPhase=%s" % L.SN_PHASES[p], phase=p, name=L.SN_PHASES[p])

    def c7_bytes(self) -> bytes:
        flags = (1 << L.SN_STATUS_FLAGS["wireSelfTestPass"]) | (1 << L.SN_STATUS_FLAGS["combatEnabled"])
        if self.s6:
            flags |= 1 << L.SN_STATUS_FLAGS["damageSelfTestPass"]     # the emulated S6 self-check passed
        caps = (1 << L.CAPABILITIES["underwaterPicture"]) | (1 << L.CAPABILITIES["creaturesAndDamage"])
        v = self.view
        if v is None:
            return L.SN_STATUS.pack(
                flags=flags, snPhase=self.phase, perfModeApplied=0, snFrame=self.frame_no, presentedSeTick=0,
                helloAckEpoch=self.hello_ack, linkAckEpoch=self.link_ack or 0,
                mirroredEntityCount=len(self.mirror.grids), pendingParts=self.pending(),
                damageDropped=self.damage_q.dropped & S.MASK32, c3BytesDrained=self.c3_bytes,
                damageOverflowDepth=len(self.damage_q.items), hostKind=L.HOST_KIND_SUBNAUTICA1, capabilities=caps,
                vSyncCountApplied=0, targetFrameRateApplied=int(self.a.fps), fixedDeltaTime=0.02)
        # S1 fields (CONTRACT 5.4 C7): the stand-in's emulated host state
        bit = lambda n: 1 << L.SN_STATUS_FLAGS[n]  # noqa: E731
        if self.phase >= L.SN_PHASE["ReadyIdle"] and self.phase != L.SN_PHASE["Quitting"]:
            flags |= bit("worldLoaded") | bit("uiHidden") | bit("runInBackgroundForced")
        active = self.phase == L.SN_PHASE["Active"]
        if not active:
            flags |= bit("frozen")
        if v.cam is not None and time.perf_counter() >= v.settled_at:
            flags |= bit("areaBuilt") | bit("streamingSettled")
        s2f = {}
        if self.s2:
            flags &= ~bit("frozen")
            if self.s2.frozen:
                flags |= bit("frozen")
            if self.s2.rect_ok:
                flags |= bit("rectOk")
            s2f = self.s2.c7_fields()
        p50, p99 = v.frame_percentiles()
        return L.SN_STATUS.pack(
            flags=flags, snPhase=self.phase,
            perfModeApplied=s2f.get("perfModeApplied", L.CONSTS["PerfMode"]["Full" if active else "Idle"]),
            snFrame=self.frame_no, presentedSeTick=v.presented, areaBuiltSeTick=v.area_tick,
            areaBuiltPos=v.area_pos, helloAckEpoch=self.hello_ack, linkAckEpoch=self.link_ack or 0,
            mirroredEntityCount=len(self.mirror.grids), pendingParts=self.pending(),
            damageDropped=self.damage_q.dropped & S.MASK32, c3BytesDrained=self.c3_bytes,
            damageOverflowDepth=len(self.damage_q.items), frameMsP50=p50, frameMsP99=p99,
            hostKind=L.HOST_KIND_SUBNAUTICA1, capabilities=caps | (1 << L.CAPABILITIES["capture"]),
            vSyncCountApplied=s2f.get("vSyncCountApplied", 0),
            targetFrameRateApplied=s2f.get("targetFrameRateApplied", int(self.a.fps)), fixedDeltaTime=0.1,
            resamplesLastFrame=1 if v.cam is not None else 0, **{k: s2f[k] for k in ("snHwnd", "snRect") if k in s2f})

    def pending(self) -> int:
        """C7 pendingParts: parts whose model has not ended (S0); with --s3 also parts with undefined materials."""
        if self.s3:
            return self.mirror.pending_parts(self.models.complete, self.models.models, self.models.materials)
        return self.mirror.pending_parts(self.models.complete)

    def counters(self) -> dict:
        extra = {"materials": len(self.models.materials), "realModels": len(self.models.models)} if self.s3 else {}
        return dict(extra, **{"grids": len(self.mirror.grids), "parts": len(self.mirror.parts),
                "pending": self.pending(), "modelsComplete": len(self.models.complete),
                "c3Messages": self.models.messages, "c4Messages": self.mirror.messages,
                "misparse": self.models.misparse + self.mirror.misparse, "stale": self.models.stale,
                "orphans": self.models.orphans + self.mirror.orphans, "abandoned": self.models.abandoned,
                "ringCorrupt": self.c3.corrupt + self.c4.corrupt if self.c3 else 0,
                "clearAlls": self.mirror.clear_alls, "damageSent": self.damage_sent,
                "damageDropped": self.damage_q.dropped, "damageQueued": len(self.damage_q.items),
                "c11Handled": self.c11_handled, "s6": dict(self.s6.counts, skins=len(self.s6.skins)) if self.s6 else None})

    def resync_consumed(self, why: str):
        tails = {"C3": self.c3.resync_tail(), "C4": self.c4.resync_tail(), "C11c": self.c11c.resync_tail()}
        self.models.on_resync()
        return tails

    # ------------------------------------------------------------------------------------------------- the loop
    def run(self) -> int:
        a = self.a
        next_t = time.perf_counter()
        boot_done = False
        stalled_once = False
        exited_once = False
        while True:
            el = time.monotonic() - self.t0
            if a.hold_seconds and el >= a.hold_seconds:
                return self.quit("hold time over")
            if a.exit_after and el >= a.exit_after and not exited_once:
                exited_once = True
                if a.exit_mode == "abrupt":
                    self.emit("abrupt_exit", "abrupt exit (no cleanup)", counters=self.counters())
                    os._exit(9)
                return self.quit("exit-after")
            if a.stall_after and el >= a.stall_after and not stalled_once:
                stalled_once = True
                self.stall(a.stall_for)
            if not boot_done and el >= a.boot_seconds:
                boot_done = True
                self.set_phase(L.SN_PHASE["Menu"])
            if a.ready_after and boot_done and self.phase == L.SN_PHASE["Menu"] and el >= a.ready_after:
                self.set_phase(L.SN_PHASE["LoadingSave"])
            if a.ready_after and self.phase == L.SN_PHASE["LoadingSave"] and el >= a.ready_after + a.load_seconds:
                if self.view:
                    self.emit("hostsave", "DE.HostSave mode=Freedom invisible=false (stand-in emulation, no save "
                                          "loaded)", mode="Freedom", invisible=False, emulated=True)
                self.set_phase(L.SN_PHASE["ReadyIdle"])
            if self.s2 and self.phase in (L.SN_PHASE["ReadyIdle"], L.SN_PHASE["WarmingUp"], L.SN_PHASE["Active"]):
                # S2: the phase follows C5 perfMode (Idle -> ReadyIdle, Track -> WarmingUp, Full -> Active) in world
                live = self.se_in_world and self.hello_ack and not self.se_exited
                want = L.SN_PHASE[self.s2.phase(live)]
                if want != self.phase:
                    self.set_phase(want)
            elif self.view:
                # S1: Active while SE is in world with an acknowledged hello, ReadyIdle otherwise
                live = self.se_in_world and self.hello_ack and not self.se_exited
                if self.phase == L.SN_PHASE["ReadyIdle"] and live:
                    self.set_phase(L.SN_PHASE["Active"])
                elif self.phase == L.SN_PHASE["Active"] and not live:
                    self.set_phase(L.SN_PHASE["ReadyIdle"])
            self.frame()
            if self.quit_requested:
                # S8-standin: --quit-delay emulates the game's own quit time (Unity's teardown) after the request
                if self.quit_at is None:
                    self.quit_at = time.monotonic() + getattr(a, "quit_delay", 0.0)
                if time.monotonic() >= self.quit_at:
                    return self.quit("de.quit")
            if self.stall_request:
                d, self.stall_request = self.stall_request, 0.0
                self.stall(d)
                next_t = time.perf_counter()
            next_t += 1.0 / (self.s2.fps() if self.s2 else a.fps)
            d = next_t - time.perf_counter()
            if d > 0:
                time.sleep(d)
            else:
                next_t = time.perf_counter()

    def frame(self):
        s = self.sec
        self.frame_no += 1
        now = S.tick64()
        s.set_hdr("snHeartbeatMs", now)
        self.last_hb_qpc = S.now_s()
        self.watch_se(now)
        self.read_c5()
        t_end = time.monotonic() + 0.008
        n = 0
        while n < 400 and time.monotonic() < t_end:
            st, mtype, payload = self.c3.try_read()
            if st == S.RR_EMPTY:
                break
            if st == S.RR_CORRUPT:
                self.models._bad("C3 ring framing corrupt (tail=%d head=%d)" % (self.c3.consumer_tail,
                                                                                  self.sec.ring_head("C3")))
                self.emit("misparse", "C3 framing corrupt: re-sync", ring="C3")
                self.resync_consumed("corrupt")
                break
            self.c3_bytes += len(payload) + 8
            self.models.feed(mtype, payload)
            n += 1
        n = 0
        while n < 2000 and time.monotonic() < t_end + 0.004:
            st, mtype, payload = self.c4.try_read()
            if st == S.RR_EMPTY:
                break
            if st == S.RR_CORRUPT:
                self.mirror._bad("C4 ring framing corrupt")
                self.emit("misparse", "C4 framing corrupt: re-sync", ring="C4")
                self.c4.resync_tail()
                break
            if self.s6:
                self.s6.on_c4(mtype, payload)      # S6: GridAdd classes, SkinSet, removals (before the mirror)
            r = self.mirror.feed(mtype, payload, self.world_epoch)
            if isinstance(r, dict):
                self.emit("clear_all", "ClearAll worldEpoch=%d linkEpoch=%d" % (r["worldEpoch"], r["linkEpoch"]),
                          **r)
            n += 1
        if self.s3:
            self.s3.frame()
        if s.hdr("devControl") & 3:
            self.serve_c11()
        self.produce_damage()
        if self.view:
            self.se_in_world = s.hdr("sePhase") == L.SE_PHASE["InWorld"] and not self.se_exited
            if self.s2:
                self.s2.frame(self.frame_no)
            self.view.frame(self.frame_no, self.phase == L.SN_PHASE["Active"],
                            self.s2.cameras_on() if self.s2 else True)
            self.view.capture(s, self.frame_no)
        S.seqlock_write(s, L.REGION["C7"].offset, self.c7_bytes())   # end of frame: presentedSeTick of this frame
        el = time.monotonic()
        if el - self.last_stats >= 1.0:
            self.last_stats = el
            c = self.counters()
            if self.models.details or self.mirror.details:
                c["details"] = (self.models.details + self.mirror.details)[:10]
            self.emit("stats", generation=self.generation, phase=self.phase, frame=self.frame_no,
                      heads=s.ring_heads(), tails=s.ring_tails(), **c)

    def watch_se(self, now):
        s = self.sec
        pid, gen = s.identity("se")      # one u64 read
        if pid and (pid, gen) != self.se_key:
            prev = self.se_key
            self.se_key = (pid, gen)
            if self.se_watch:
                self.se_watch.close()
            self.se_watch = S.ProcessWatch(pid)
            self.se_exited = False
            self.se_stalled = False
            tails = self.resync_consumed("new SE generation")
            self.mirror.clear()
            self.models.reset()
            self.hello_ack = 0
            self.link_ack = None
            self.c5 = S.SeqlockReader(s, L.REGION["C5"].offset, L.SE_STATE)
            self.emit("se_new_generation", "new SE generation %d (pid %d): tails re-synced, waiting for the hello" % (
                gen, pid), sePid=pid, seGeneration=gen, previous=prev, tails=tails)
        if not self.se_key:
            return
        if not self.se_exited and self.se_watch and self.se_watch.exited():
            self.se_exited = True
            self.emit("se_exited", "SE process %d exited" % self.se_key[0])
            if self.a.quit_on_se_exit:
                self.quit_requested = True
        hb = s.hdr("seHeartbeatMs")
        se_phase = s.hdr("sePhase")
        loading = se_phase in L.SE_LOADING_PHASES or self.phase in L.SN_LOADING_PHASES
        limit = (L.STALL_LOADING_S if loading else L.STALL_PLAY_S) * 1000
        age = (now - hb) if now >= hb else 0
        if not self.se_exited:
            if not self.se_stalled and age >= limit:
                self.se_stalled = True
                self.emit("se_stalled", "SE stalled (heartbeat %d ms old)" % age, ageMs=age)
            elif self.se_stalled and age < limit:
                self.se_stalled = False
                self.emit("se_resumed", "SE heartbeat fresh again")
        if se_phase == L.SE_PHASE["Exiting"] and not self.se_exited:
            self.se_exited = True
            self.emit("se_exited", "SE reports Exiting")
            if self.a.quit_on_se_exit:
                self.quit_requested = True

    def read_c5(self):
        if not self.c5:
            return
        st = self.c5.poll()
        v = self.c5.value
        if v is None:
            return
        self.world_epoch = v["worldEpoch"]
        self.c5_overlays = v["overlays"]
        if self.s2:
            self.s2.on_c5(v)
        if v["helloEpoch"] and v["helloEpoch"] != self.hello_ack:
            self.mirror.clear()
            self.hello_ack = v["helloEpoch"]
            self.emit("hello_ack", "hello epoch %d acknowledged (Rs=%.8f siteHash=%s siteVersion=%d)" % (
                v["helloEpoch"], v["Rs"], L.hex64(v["siteHash"]), v["siteVersion"]), helloEpoch=v["helloEpoch"],
                Rs=v["Rs"], siteHash=L.hex64(v["siteHash"]), siteVersion=v["siteVersion"],
                siteHashOk=v["siteHash"] == L.SITE_HASH_V1)
        if self.link_ack is None:
            self.link_ack = v["linkEpoch"]
        elif v["linkEpoch"] != self.link_ack:
            tails = self.resync_consumed("link epoch")
            prev = self.link_ack
            self.link_ack = v["linkEpoch"]
            self.emit("link_ack", "link epoch %d acknowledged (re-synced; grids=%d pending=%d)" % (
                self.link_ack, len(self.mirror.grids), self.pending()),
                linkEpoch=self.link_ack, previous=prev, tails=tails, counters=self.counters())

    def produce_damage(self):
        if self.s6:
            self.s6.frame()       # S6: the plans' records (the S0 synthetic stream is off)
            return
        if self.a.damage_rate <= 0:
            return
        self.damage_acc += self.a.damage_rate / self.a.fps
        while self.damage_acc >= 1.0:
            self.damage_acc -= 1.0
            self.damage_seq += 1
            self.damage_q.enqueue(ST.damage_record(self.generation, self.damage_seq, self.world_epoch))
        self.damage_sent += self.damage_q.flush_to(self.c8)

    def serve_c11(self):
        for _ in range(16):
            st, mtype, payload = self.c11c.try_read()
            if st == S.RR_EMPTY:
                return
            if st == S.RR_CORRUPT:
                self.c11c.resync_tail()
                return
            self.c11_handled += 1
            try:
                req = json.loads(payload.decode("utf-8"))
                rid, cmd, args = req.get("id"), req.get("cmd"), req.get("args") or {}
            except (ValueError, AttributeError):
                self.reply({"id": None, "ok": False, "error": "bad json"})
                continue
            self.emit("c11", "C11 %s" % cmd, id=rid, cmd=cmd, args=args)
            if cmd == "de.ping":
                self.reply({"id": rid, "ok": True, "pong": True, "snGeneration": self.generation})
            elif cmd == "de.state":
                rep = {"id": rid, "ok": True, "snPhase": L.SN_PHASES[self.phase], "snGeneration": self.generation,
                       "counters": self.counters()}
                if self.view and self.view.cam:
                    c = self.view.cam
                    rep["camera"] = {"pos": list(c["pos"]), "rot": list(c["rot"]), "fov": c["fov"],
                                     "presentedSeTick": self.view.presented, "outcomes": dict(self.view.counts)}
                self.reply(rep)
            elif cmd == "de.pixel" and self.view:
                self.view.add_pixel_request(rid, args if isinstance(args, dict) else {})
            elif cmd in ("de.trace", "de.underwater", "de.testSave") and self.view:
                # stand-in emulation of the SN plugin's S1 test commands (no game state behind them)
                if cmd == "de.underwater":
                    self.view.underwater_mode = str((args or {}).get("mode", "off"))
                if cmd == "de.testSave":
                    self.emit("savetest", "DE.SaveBlocked IngameMenu.SaveGame (stand-in emulation of the S1.N4 "
                                          "trigger; nothing to save)", emulated=True)
                self.reply({"id": rid, "ok": True, "cmd": cmd, "emulated": True})
            elif cmd == "de.s6" and self.s6:
                self.reply(self.s6.command(args, rid))
            elif cmd == "de.spawn" and self.s6:
                self.emit("s6.spawn", "de.spawn %s (stand-in: no creatures)" % json.dumps(args)[:120], args=args,
                          ok=True, emulated=True)
                self.reply({"id": rid, "ok": True, "line": "de.spawn", "emulated": True, "spawned": 0})
            elif cmd == "de.s3" and self.s3:
                self.reply(self.s3.command(args, rid))
            elif cmd in ("de.calib", "de.s2", "de.biome") and self.s2:
                self.reply(self.s2.command(cmd, args, rid))
            elif cmd == "de.selftest":
                self.reply({"id": rid, "ok": True, "wireSelfTest": "PASS (python stand-in, layout only)"})
            elif cmd == "de.stallSn":
                secs = args.get("seconds") if isinstance(args, dict) else (args[0] if args else None)
                try:
                    secs = float(secs)
                except (TypeError, ValueError):
                    self.reply({"id": rid, "ok": False, "error": "seconds"})
                    continue
                self.reply({"id": rid, "ok": True, "stallSeconds": secs})
                self.stall_request = secs
                return
            elif cmd == "de.quit":
                self.reply({"id": rid, "ok": True, "quitting": True})
                self.quit_requested = True
                return
            else:
                self.reply({"id": rid, "ok": False, "error": "unknown command %s" % cmd})

    def reply(self, obj):
        self.c11d.try_write_or_drop(L.C11_TYPES["Json"], json.dumps(obj, separators=(",", ":")).encode("utf-8"))

    def stall(self, seconds: float):
        """A hung main thread: nothing is written for `seconds` (no heartbeat, no C7, no ring progress)."""
        self.emit("stall_begin", "stalling %.2f s (no writes)" % seconds, seconds=seconds, lastHeartbeatT=self.last_hb_qpc,
                  lastHeartbeatMs=self.sec.hdr("snHeartbeatMs"))
        time.sleep(seconds)
        self.emit("stall_end", "stall over")

    def quit(self, why: str) -> int:
        """Quitting phase, wait <= 100 ms for SE's acknowledgement (C5 overlay PeerExited), then leave."""
        self.set_phase(L.SN_PHASE["Quitting"])
        S.seqlock_write(self.sec, L.REGION["C7"].offset, self.c7_bytes())
        t0 = time.monotonic()
        acked = False
        if self.se_key and not self.se_exited:
            while time.monotonic() - t0 < L.QUITTING_ACK_WAIT_S:
                if self.c5 and self.c5.poll() and self.c5.value and \
                        self.c5.value["overlays"] & (1 << L.OVERLAYS["PeerExited"]):
                    acked = True
                    break
                time.sleep(0.005)
        self.emit("quit", "quitting (%s); SE ack %s after %.0f ms" % (why, "seen" if acked else "not seen",
                                                                        (time.monotonic() - t0) * 1000),
                  reason=why, seAck=acked, counters=self.counters(),
                  cameraOutcomes=dict(self.view.counts) if self.view else None)
        if self.s3:
            self.s3.summary()
        if self.s6:
            self.s6.summary()
        if self.s2:
            self.s2.close()
        self.release()
        if self.view and self.a.s1_check:
            self.s1_check()
        return EXIT_OK

    def s1_check(self):
        """--s1-check: the WM numbers of the SE log (real SE or stand-in) against worldmap_ref, with this host's own
 S1 events as the Subnautica side (tests/s1/check_wm.py)."""
        try:
            import check_wm   # noqa: E402
        except ImportError as e:
            self.emit("s1_check", "S1 check unavailable: %s" % e)
            return
        path = self.a.s1_check
        if path == "auto":
            logs = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers", "logs")
            cands = [p for p in glob.glob(os.path.join(logs, "se-*.jsonl")) if os.path.getmtime(p) >= self.start_wall]
            path = max(cands, key=os.path.getmtime) if cands else None
        if not path or not os.path.exists(path):
            self.emit("s1_check", "S1 check: no SE log found (%s)" % self.a.s1_check)
            return
        r = check_wm.evaluate(check_wm.read_jsonl(path), self.view.events, (), check_wm.plan(self.a.s1_quick),
                              strict=False)
        sys.stderr.write(check_wm.table(r["rows"]) + "\nCHECK_WM %s (SE log %s)\n" % ("PASS" if r["ok"] else "FAIL", path))
        self.emit("s1_check", "CHECK_WM %s on %s" % ("PASS" if r["ok"] else "FAIL", path), ok=r["ok"], seLog=path,
                  rows=r["rows"], summary=r["summary"])

    def passive_hold(self) -> int:
        """Mutant seeds: write once, then hold the section without any further write."""
        self.emit("passive", "holding the section without writes", seconds=self.a.hold_seconds)
        end = time.monotonic() + (self.a.hold_seconds or 3600 * 24)
        while time.monotonic() < end:
            time.sleep(0.05)
        self.release()
        return EXIT_OK

def parse(argv):
    ap = argparse.ArgumentParser(description="Fake Subnautica host (S0 stand-in)")
    ap.add_argument("--name", default=L.SECTION_NAME, help="section name (tests use their own)")
    ap.add_argument("--size", type=lambda x: int(x, 0), default=L.SECTION_SIZE)
    ap.add_argument("--fps", type=float, default=None, help="frame rate (default 60; 144 with --s1)")
    ap.add_argument("--boot-seconds", type=float, default=0.3, help="Booting -> Menu after this")
    ap.add_argument("--ready-after", type=float, default=0.0, help="Menu -> LoadingSave -> ReadyIdle (0: stay at Menu)")
    ap.add_argument("--hold-seconds", type=float, default=0.0, help="quit after this (0: run until told)")
    ap.add_argument("--exit-after", type=float, default=0.0)
    ap.add_argument("--exit-mode", choices=("abrupt", "quit"), default="abrupt")
    ap.add_argument("--stall-after", type=float, default=0.0)
    ap.add_argument("--stall-for", type=float, default=5.0)
    ap.add_argument("--damage-rate", type=float, default=20.0, help="C8 records per second")
    ap.add_argument("--no-token-check", action="store_true")
    ap.add_argument("--pid-override", type=int, default=0, help="write this PID as snPid (simulates PID reuse)")
    ap.add_argument("--quit-on-se-exit", action="store_true")
    ap.add_argument("--quit-delay", type=float, default=0.0, help="S8: seconds between a quit request and the quit (Unity's own quit time)")
    ap.add_argument("--passive", action="store_true", help="after creating, hold without writing (mutant seeds)")
    ap.add_argument("--mutant", choices=("magic", "wire2", "layout2", "size", "zero-heads"))
    ap.add_argument("--quiet", action="store_true", help="no stats lines")
    ap.add_argument("--human", action="store_true", help="plain text instead of JSON lines")
    ap.add_argument("--load-seconds", type=float, default=None, help="LoadingSave -> ReadyIdle after this (S0 0.2, S1 0.5)")
    g = ap.add_argument_group("S1 (emulated host camera)")
    g.add_argument("--s1", action="store_true", help="S1: ReadyIdle/Active phases, C1 camera emulation, S1 events")
    g.add_argument("--screen", type=lambda s: tuple(int(x) for x in s.lower().split("x")), default=(2560, 1440))
    g.add_argument("--interp-delay-ticks", type=float, default=1.25, help="render delay D in SE ticks")
    g.add_argument("--settle-seconds", type=float, default=0.5, help="emulated streaming settle time after a snap")
    g.add_argument("--captures-dir", default=None, help="root of C10 capture paths (default %%LOCALAPPDATA%%\\"
                                                        "DeepEngineers\\captures)")
    g.add_argument("--fmod-every", type=float, default=0.0, help="synthetic WaterAmbience s1.fmod event every N s")
    g.add_argument("--options-fov", type=float, default=60.0,
                   help="emulated Subnautica options FOV logged as s1.camRig (S1.N7 precondition: differs from SE's)")
    g.add_argument("--fmod-pda-at", type=float, default=0.0, help="one synthetic PDA voice s1.fmod at N s (mutant)")
    g.add_argument("--s1-check", default=None, help="SE JSONL to check at exit with check_wm.py, or 'auto'")
    g.add_argument("--s1-quick", action="store_true", help="--s1-check uses the quick plan")
    g = ap.add_argument_group("S2 (C5-driven recipes, freeze, mix, window; see the module docstring)")
    g.add_argument("--s2", action="store_true", help="S2 host behaviour (implies --s1)")
    g.add_argument("--s2-window", action="store_true", help="a real stand-in window (snHwnd); off-screen by default")
    g.add_argument("--s2-rect", type=lambda s: tuple(int(x) for x in s.split(",")), default=(-32000, -32000, 320, 180),
                   help="x,y,w,h of the stand-in window (default off-screen)")
    g = ap.add_argument_group("S3 (real C3/C4 models, C2 placement; see the module docstring)")
    g.add_argument("--s3", action="store_true", help="S3 mirror bookkeeping and analyser events (implies --s1)")
    g.add_argument("--s3-mutant", choices=("M1", "M2"), default=None,
                   help="M1: Z mirror dropped (S3.C2, S3.C3 red); M2: triangle swap dropped (S3.C3 red)")
    g = ap.add_argument_group("S6 (SkinSet consumer, C8 damage plans; see the module docstring S6)")
    g.add_argument("--s6", action="store_true", help="S6 host behaviour (implied by --damage / --malformed)")
    g.add_argument("--damage", choices=("n1", "n10", "stream"), default=None,
                   help="n1: S6.N1 bites (30 unique per target + 10 duplicates in total); n10: S6.N10 malformed "
                        "records; stream: valid records at --damage-stream-rate. Plans run on de.s6 {action: damage}, "
                        "from --damage-plan, or by themselves with --damage-auto")
    g.add_argument("--malformed", action="store_true", help="alias of --damage n10")
    g.add_argument("--damage-plan", default=None, metavar="JSON",
                   help="a plan file polled every frame (run when it appears or changes): {mode, targets: auto | "
                        "[{entity, kind}] | {small, large, character}, unique, dups, seed}")
    g.add_argument("--damage-seed", type=int, default=1)
    g.add_argument("--damage-auto", action="store_true", help="run the --damage plan once 3 s after the last new "
                                                              "SkinSet (targets: one small grid, one large grid, the "
                                                              "character when C1 has one)")
    g.add_argument("--damage-stream-rate", type=float, default=10.0, help="--damage stream: records per second")
    a = ap.parse_args(argv)
    if a.malformed:
        a.damage = a.damage or "n10"
    if a.damage or a.damage_plan:
        a.s6 = True
    if a.s3_mutant and not a.s3:
        ap.error("--s3-mutant needs --s3")
    if a.s2 or a.s3:
        a.s1 = True
    if a.fps is None:
        a.fps = 144.0 if a.s1 else 60.0
    if a.s1 and a.ready_after == 0.0:
        a.ready_after = 1.0
    if a.load_seconds is None:
        a.load_seconds = 0.5 if a.s1 else 0.2
    return a

def main(argv=None) -> int:
    a = parse(sys.argv[1:] if argv is None else argv)
    if a.mutant in FOREIGN_CREATOR_MUTANTS:
        a.passive = True   # a foreign (non-SN) creator: it seeds the section and never runs the SN loop
    S.fine_timer(True)
    h = Host(a)
    try:
        rc = h.start()
        if rc != EXIT_OK:
            return rc
        if a.passive:
            return h.passive_hold()
        return h.run()
    except KeyboardInterrupt:
        return h.quit("interrupted") if h.sec else EXIT_OK
    finally:
        S.fine_timer(False)

if __name__ == "__main__":
    sys.exit(main())
