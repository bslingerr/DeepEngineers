"""Fake Space Engineers side: a Python stand-in for the SE plugin's bridge (ROADMAP §5.8, S0).

Opens the section like SE (CONTRACT §6 opener rules): ctypes OpenFileMappingW, open only, never creates; retries once
per second (every 5 s after the first minute) with at most one `DE.Link WAIT` per 10 s; validates (§6 steps 1-6) ->
`DE.Link UP`, `WAIT` or `REJECT` (a rejecting SE writes nothing); takes `<name>_se` (held elsewhere -> `DE.Link REFUSE
second instance`); writes the SE block (PID, generation = found + 1, heartbeat every tick, phases) and C1/C5/C6 every
tick; publishes the hello (helloEpoch = worldEpoch, Rs, siteHash, siteVersion) and streams C3/C4 only after the ack.

Peer detector (CONTRACT §5.1 C0 row, §6, §7; ARCHITECTURE §4.3, §5.14):
- PeerExited: process exit (OpenProcess(SYNCHRONIZE) + WaitForSingleObject(h, 0), polled every tick by default),
 snPhase in {Quitting, None}, or a changed snPid/snGeneration;
- PeerStalled: heartbeat older than 3 s (120 s while either side is in a loading phase);
- a stall that ends with the same PID and generation -> linkEpoch + 1, consumer tails re-synced, wait for
 linkAckEpoch, then resend everything when the stall lasted >= 10 s or the counters disagree, else continue;
- a new peer (generation) -> consumer tails (C8, C12, C11a):= heads, wait for the new host's hello ack, resend
 everything (ClearAll, grids, parts, models).
Mutant switches (S0.M5): --mutant no-exit-poll (M5a: no WaitForSingleObject), timeout30 (M5b: 30 s instead of 3 s),
heartbeat-liveness (M5c: liveness = "heartbeat value changed", identity = PID only).

S1 (ROADMAP S1 "Stand-ins": the fake SE producer drives the real Subnautica plugin along scripted WM-2/3/4 paths):
--s1 turns on the SE camera director. Every tick in world it writes a real C1 SeFrame (tick, qpcCapture = QPC at the
write, simTimeSec, flags inWorld/teleported, camPos = F(P) and camRot = LookRotation(mapdir(Forward), mapdir(Up)) at P
for an SE-world spectator camera P in doubles, vFovDeg, near 0.05, aspect, cameraMode 4 spectator) at 60 Hz, and logs
the S1 events of tests/s1/check_wm.py (s1.site, s1.frame, s1.cmd, s1.yawStep, s1.pixel, s1.hold). The map is the
reference (tools/worldmap/worldmap_ref.py with the site_v1 anchor, Rs = 60000.00390625, planet centre --planet-centre,
default site_v1.TEST_C); the stand-in is the SE side, the C# WireMath is not involved. It serves the S1 C11 commands
on CtlToSe (de.teleport, de.look, de.path, de.yawStep, de.pixel, de.capture through the C10 request/result pair,
de.state, de.ping), so `launcher.py s1` can run the whole S1 session against it, and --s1-script runs the scripted
paths by itself (wm2,hold,wm3,wm4,yaw,g1e5 or all; G1-E5 only logs the SE half there) so the real Subnautica plugin
can be driven without SE. S1 seeded failures (--mutant): mapdir-origin (mapdir taken at the site entity origin, the
anchor frame E0/U0/N0, instead of at the camera P: the S0.M4 mutant applied to the camera), naive-map (S1.M1, naive
tangent-plane map and its inverse), prev-tick. C5 carries the camera's zone (CORE / TILE VOID /
OUTER VOID / FAR with the 7,800/7,900 m hysteresis, off-site without a hello), perfMode and camSnY, as the SE plugin's
FrameWriter + S1Perf, so the Subnautica plugin's drive gate sees what the real SE sends.

S2 (ROADMAP S2 "Stand-ins": "the fake SE producer drives the Subnautica plugin through all states"). --s2 (implies
--s1) runs a Python model of the view state machine of ARCHITECTURE 4.2 on the director's camera every tick (Space,
Approach/Track, Approach/Prime, Underwater, Surfacing; waterline -0.20 in / -0.05 out with a 0.25 s dwell, Prime at
+1 m or +5 m descending or a crossing predicted within 2 s, back to Track after 3 s quiet, Approach below +300 m / above
+350 m, zone 7,800/7,900 m, snReady from C7: Active, areaBuilt, streamingSettled after a teleport, presentedSeTick >=
tick - 4, rectOk, uiHidden; crossingTick = the tick of the downward crossing of -0.20 m) and publishes C5 viewState,
approachMode, perfMode (Idle/Track/Full), targetFps (10/10/0), snBusTarget (1 underwater), crossfadeMs 300, seRect
(--s2-se-rect), camVelSn, crossingTick and the flags seForeground, seeThrough, marginsOn, zOrderOk, seMixActive
(the forced see-through of de.display toggle / seeThrough on|off is OR-ed with the machine's request, as in the SE
plugin's DisplayController). The
display is emulated with the crossing order (margins on and presented before the scene is skipped; scene drawn and
presented before the margins drop) and logged in the SE plugin's own line formats (DE.View A -> B..., DE.Display
margins ON|OFF hr=, DE.Display toggle #N..., DE.Display see-through first skipped frame presented..., DE.Display
alpha..., DE.Audio gate=...), plus structured s2.state / s2.display events and 1 Hz "sample" records (gameplayTick,
frameTimeAvgMs/MaxMs) for tests/s2/analyze_*.py. --s2-script states replaces the machine by a fixed timeline that
loops Space -> Track -> Prime -> Underwater -> Surfacing -> Prime -> Track (crossingTick set at each entry), so the
real Subnautica plugin can be driven through every state without SE or a camera path. C11 (devControl bit 0): the
SE plugin's S2 commands de.display {toggle | seeThrough | margins | skip on|off|auto | readback once | probe | state},
de.view {force}, de.audio, de.zorder {reassert}, and the proposed motion command de.motion {kind: bob, x, z, centreY,
amplitudeM, hz, seconds} (a smooth vertical sine, no teleport flag) | {kind: line, fromSn, toSn, speedMps} (a straight
segment) - a new name, because SE's S1 de.path ignores unknown keys.
--s2-window creates a real stand-in SE window (off-screen by default, never activated) published as C0 seHwnd; when
C0 snHwnd is a window, the stand-in places it directly under its own (SetWindowPos(snHwnd, seHwnd,...,
NOACTIVATE|ASYNCWINDOWPOS)) and verifies it with tools/display/walker.py (C5 zOrderOk). Seeded failures:
--s2-mutant M1 (margins reported, DWM call "not made": the log line carries the S2.M1 text), M4 (no band, no dwell).

S3a (ROADMAP S3 "tools/tests": the first block without SE). --s3a (implies --s1; `launcher.py s3 --mode S3a` adds --s2 so
that Subnautica gets its Full recipe underwater) is the SE plugin's S3 side in Python: a procedural 2.5 m cube in SE-local
space (24 vertices, 12 triangles in the MyModel.Triangles convention, normals, tangent = dP/du, binormal = dP/dv, UVs over
the square left half [0, 0.5] x [0, 1] of SE's 2:1 armor atlases, v = 0 at the first DDS row), streamed once on C3 as
MaterialDef (technique 0 = MyMeshDrawTechnique.MESH; the four absolute paths of SE's real <set>_cm/_ng/_add.dds, resolved
under --se-content and read in place: only the 148-byte headers are read, for the log) + ModelBegin/ModelChunk/ModelEnd
(payload: 56-byte vertices, u32 indices, one 24-byte submesh; payloadHash = FNV-1a 64 of the chunk data after the 16-byte
chunk headers; modelKey = FNV-1a 64 of the lower-case normalised synthetic path continued over the LOD as a u32), and on
C4 as GridAdd + one PartAdd (identity part matrix: the block centre is the grid origin, colorMaskHSV --s3a-hsv). The pose:
the grid's SE matrix at F^-1(lab point) (level, Forward along --s3a-heading, Right = Forward x Up) and P* = the grid box
point nearest to the camera (tools/worldmap/wire_ref.py pivot_nearest + wire_pose: the reference, not WireMath) published
in C2 every tick before C1 with the same tick (one static root grid, k = 1). C11 de.s3 (the proposal the SE plugin's S3b
side is expected to share): {place: {sn: [x, y, z], yawDeg, glyph, label}} (block centre at the SN point; the reply carries
gridId/entityId, seMatrix, pivot, o/q/s/k, bounds, verts/tris, the 8 corners in SN space (Apply and exact F), the faces
and texture paths; glyph adds the N1 block at grid cell (1, 0, 0) with BC7 maps written under %LOCALAPPDATA%\\DeepEngineers
\\s3\\glyph: a 4-quadrant colour map and an 8 x 8-cell bump map: per colour quadrant an up/down checker over the top
two cell rows and a right/left checker over the bottom two, see glyph_cell_kind), {pixel: {corner: i} | {sn: [x, y, z]} | {}} (the SE
pinhole pixel of a corner, a point or the centre: S3.N3), {gps: on|off} (an s3.gps event per tick: the GPS-marker pixel of
the block centre, S3.N2), {clear: true}, {} (state); de.motion {kind: yaw, amplitudeDeg, hz, seconds} (the 0.5 Hz sine of
S3.N2, also under --s2). Events: s3.place, s3.model (producer counts, S3.C1), s3.pose (whenever the pose changes: entityId,
tick, seMatrix, pivot, k, site, wire {o, q, s} - the schema of tests/s3/check_placement.py), s3.gps, s3.pixel, s3.record,
s3.resend, s3.summary. A new Subnautica generation or a link-epoch resend sends ClearAll and the scene again. --s3-mutant
M4 (S3.M4) publishes the previous tick's snapshot (C1 camera and C2 entity record) stamped with the current tick, while
the GPS marker uses the current camera. --s3a-selftest checks all of it without a peer (private test sections).

S5 (ROADMAP S5 "tools/tests": the stream replayer and the stand-in at scale). --s5 (implied by --replay or --synth;
implies --s1; `launcher.py s5 --mode S5a` adds --s2) is the SE plugin's S5 producer in Python (section "S5" below):
--replay <name | file.derec | folder> replays a recorded SE stream (CONTRACT 5.4 "Stream recording": C3/C4 in order with
the epochs rewritten, the recorded C2/C1 re-stamped, else synthesised root poses) after the hello ack with the S5
budgets (C4 <= 512 KiB per tick by class, C3 <= 16 MiB/s, a model's messages never interleaved, back-pressure below
1 MiB / 8 MiB free); --synth 500,2000,5000 (alias --synth-ship) builds ships of N blocks from SE's real models read in
place (tests/standins/s5_stream.py Ship) before the link, spawns one on de.s5 spawn (or --s5-spawn-at), poses it in C2
every tick (root entity + subgrid rows + dynamic subpart rows) and serves de.s5 {action: status | spawn | teardown |
count | parts | edit | rotor | piston | door | sub | fly | cockpit | coh | loader | camera} and de.s5 {record: {name, c1,
c2} | "stop"} (a.derec of what it writes) plus de.mutant {name: S5.M1|S5.M3, on}; the SE plugin's own de.s5 syntax
(status, export {entityId, sample, seed}, models {count, send}, coh {entityId, point, seconds}, trace seconds,
character, set) is translated onto those actions, so `launcher.py s5` drives both with one table. SE-side rows: s5.grid, s5.part,
s5.pose, s5.edit, s5.sub, s5.model, s5.coh, s5.budget (schema: tests/s5/analyze_s5.py). Seeded failures: --s5-mutant M1
(C2 one tick behind the camera, the stand-in's form of S5.M1) and M3 (PartRemove not written, counted as sent).
--s5-selftest runs the scene against a private test section (no peer).

S6 (ROADMAP S6 "tools/tests": the B-T1 scene without SE). --s6 (implied by --skin; implies --s1; `launcher.py s6 --mode
S6a` adds --s2) is the SE plugin's S6 producer duties in Python (class S6Scene): a synthetic hull of
tests/standins/s6_stream.py (--skin synth:bt1 = the B-T1 proxy 20 x 6 x 6 m, 50 boxes, 2 sections, looping at 5 m/s;
bt1k = the same envelope in 1,000 boxes for B-T1 (3); rotated = rotated boxes for G1-E3; hull100 = a static 100 m hull
for N4; rotor = a large root grid with a small-grid head turning at 10 rpm as a subgrid; none = the bt1 hull without a
skin) streamed as a mirrored grid (procedural box models with SE's armor textures read in place on C3, GridAdd and one
PartAdd per box on C4, the root's C2 entity record and the subgrid rows every tick) followed by its C4 type 6 SkinSet
(CONTRACT §5.4: version 1 after the GridAdd, +1 per de.s6 resend, back to 1 after a ClearAll); --skin-spawn-at x,y,z
spawns it at the hello ack (the loop centre when moving: --skin-speed, --skin-radius; --skin-heading for static hulls),
else de.s6 {action: spawn, skin, sn, yawDeg, speedMps, radiusM}. C8 is drained into s6.c8 rows (the record's fields,
id gen:seq, the dedup twin's verdict) instead of the S0 content check. de.s6 also takes {action: status | teardown |
motion {speedMps, radiusM} | pose} and SE's syntax {status: true} | {skin: "<id>"|"all", rows} | {resend: "<id>"|"all"}
(replies carry "line": "de.s6 <action>"; {damage} and {selftest} are refused: the stand-in has no damage consumer).
SE-side rows: s6.skin (the SE schema of the S6 status document, standin true), s6.spawn, s6.pose, s6.c8, s6.stats.
--s6-selftest runs every hull against a private test section (no peer).

Events are JSON lines on stdout (`t` = QPC seconds). Exit codes: 0 normal, 3 refused (second instance) or rejected
with --exit-on-reject, 5 never linked within --give-up-after.
"""
from __future__ import annotations

import argparse
import collections
import json
import math
import os
import struct
import sys
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_WIRE = os.path.normpath(os.path.join(_HERE, "..", "wire"))
_REPO = os.path.normpath(os.path.join(_HERE, "..", ".."))
_WORLDMAP = os.path.join(_REPO, "tools", "worldmap")
_S1 = os.path.join(_REPO, "tests", "s1")
for _p in (_WIRE, _HERE, _WORLDMAP, _S1):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402
import stream_py as ST  # noqa: E402

EXIT_OK, EXIT_REFUSED, EXIT_NEVER_LINKED = 0, 3, 5
BIT = lambda name, table: 1 << table[name]  # noqa: E731
S1_MUTANTS = ("mapdir-origin", "naive-map", "prev-tick")
S1_COMMANDS = ("de.teleport", "de.look", "de.path", "de.yawStep", "de.pixel", "de.capture")
GRAVITY = 9.81
SNAP_M = 20.0
S2_COMMANDS = ("de.display", "de.view", "de.audio", "de.zorder", "de.motion")

def _v_add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def _v_sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def _v_mul(a, k): return (a[0] * k, a[1] * k, a[2] * k)
def _v_dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def _v_cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])

def _v_unit(a):
    n = math.sqrt(_v_dot(a, a))
    return (a[0] / n, a[1] / n, a[2] / n)

class S1Camera:
    """The SE side of S1 in the stand-in: a spectator camera in SE world (doubles), the scripted moves of the S1 test
 commands, and the C1 publication (camera pose mapped with the reference map at the camera point P)."""

    def __init__(self, se, a):
        import site_v1   # noqa: E402 (tools/worldmap, stdlib)
        import worldmap_ref as wm   # noqa: E402
        import wire_ref   # noqa: E402
        self.se, self.a = se, a
        self.wire_ref = wire_ref
        self.site_v1 = site_v1
        anc = site_v1.ANCHOR
        C = tuple(a.planet_centre) if a.planet_centre else tuple(site_v1.TEST_C)
        self.site = wm.Site(C=C, Rs=site_v1.RS_EXPECTED, U0=anc["U0"], N0=anc["N0"], psi_deg=anc["psi_deg"],
                            O=anc["O"])
        self.C, self.Rs = self.site.C, self.site.Rs
        self.mutant = a.mutant if a.mutant in S1_MUTANTS else None
        self.W, self.H = a.screen
        self.fov = a.fov
        self.near = 0.05
        self.label = ""
        self.T = self.fwd = self.up = None
        self.teleport_flag = False
        self.path = None
        self.prev_state = None
        self.site_logged = None
        self.zone_in = False
        self.zone = L.CONSTS["Zone"]["OffSite"]
        self.cam_sn_y = 0.0
        self.motion = None             # S2: ("line" | "bob", parameters) advanced every tick
        self.last_qpc = None           # qpcCapture of the last C1 publication (S3.N2 GPS stamps)
        self.published_teleport = False
        self.camera_mode = None        # S5: C1 cameraMode override (cockpit, first/third person); None = spectator
        self.teleport((0.0, 5.0, 0.0), 0.0, 0.0, None, "")

    # ---- the map (reference) and the seeded mutants --------------------------------------------------------------
    def frame(self, T):
        if self.mutant in ("mapdir-origin", "naive-map"):
            return self.site.E0, self.site.U0, self.site.N0
        return self.site.frame_at(T)

    def F(self, T):
        if self.mutant == "naive-map":
            v = _v_sub(T, self.C)
            return (_v_dot(v, self.site.E0), _v_dot(v, self.site.U0) - self.Rs, _v_dot(v, self.site.N0))
        return self.site.se_to_sn(T)

    def Finv(self, Q):
        if self.mutant == "naive-map":
            s = self.site
            return _v_add(self.C, _v_add(_v_add(_v_mul(s.E0, Q[0]), _v_mul(s.U0, self.Rs + Q[1])), _v_mul(s.N0, Q[2])))
        return self.site.sn_to_se(tuple(float(x) for x in Q))

    def mapdir(self, T, w):
        E, U, N = self.frame(T)
        return (_v_dot(w, E), _v_dot(w, U), _v_dot(w, N))

    def mapdir_inv(self, T, d):
        E, U, N = self.frame(T)
        return _v_add(_v_add(_v_mul(E, d[0]), _v_mul(U, d[1])), _v_mul(N, d[2]))

    def orient(self, T, heading_deg, pitch_deg):
        h, p = math.radians(heading_deg), math.radians(pitch_deg)
        d = (math.sin(h) * math.cos(p), math.sin(p), math.cos(h) * math.cos(p))
        u = (-math.sin(h) * math.sin(p), math.cos(p), -math.cos(h) * math.sin(p))
        fwd = _v_unit(self.mapdir_inv(T, d))
        up = self.mapdir_inv(T, u)
        up = _v_unit(_v_sub(up, _v_mul(fwd, _v_dot(up, fwd))))
        return fwd, up

    # ---- commands -------------------------------------------------------------------------------------------------
    def teleport(self, sn, heading, pitch, fov, label):
        self.path = None
        self.T = self.Finv(sn)
        self.fwd, self.up = self.orient(self.T, heading, pitch)
        if fov is not None:
            self.fov = float(fov)
        self.label = label
        self.teleport_flag = True

    def look(self, d, label):
        E, U, N = self.frame(self.T)
        self.path = None
        self.fwd, self.up = {"E": (E, U), "N": (N, U), "down": (_v_mul(U, -1.0), N)}[d]
        self.label = label

    def heading_of(self, w) -> float:
        d = self.mapdir(self.T, w)
        return math.degrees(math.atan2(d[0], d[2]))

    def yaw_step(self, deg):
        """Rotates the camera about SN +Y by `deg` (clockwise seen from above: heading + deg)."""
        a = math.radians(deg)

        def rot(v):
            return (v[0] * math.cos(a) + v[2] * math.sin(a), v[1], -v[0] * math.sin(a) + v[2] * math.cos(a))
        h0 = self.heading_of(self.fwd)
        self.fwd = _v_unit(self.mapdir_inv(self.T, rot(self.mapdir(self.T, self.fwd))))
        self.up = _v_unit(self.mapdir_inv(self.T, rot(self.mapdir(self.T, self.up))))
        return h0, h0 + deg

    def pitch_step(self, deg, label):
        """Turns the view up by `deg` about the camera's right axis (Forward x Up)."""
        a = math.radians(deg)
        f, u = self.fwd, self.up
        self.fwd = _v_unit(_v_add(_v_mul(f, math.cos(a)), _v_mul(u, math.sin(a))))
        self.up = _v_unit(_v_sub(_v_mul(u, math.cos(a)), _v_mul(f, math.sin(a))))
        self.label = label
        self.path = None

    def path_start(self, args, tick):
        """A great circle at constant altitude, defined in SE space (not through the map), heading `headingDeg` from
 the start point: P(k) = C + r (cos t uA + sin t DA), t = (lengthM / Rs) k / N, N = samples * ticksPerSample
 ticks; the camera moves every tick and looks along the path."""
        A = self.Finv(args.get("fromSn") or (0.0, -100.0, 0.0))
        h = math.radians(float(args.get("headingDeg", 90.0)))
        E, U, N = self.site.frame_at(A)
        DA = _v_unit(_v_add(_v_mul(E, math.sin(h)), _v_mul(N, math.cos(h))))
        r = math.sqrt(_v_dot(_v_sub(A, self.C), _v_sub(A, self.C)))
        n = int(args.get("samples", 100)) * int(args.get("ticksPerSample", 6))
        self.path = {"start": tick, "n": max(1, n), "uA": _v_unit(_v_sub(A, self.C)), "DA": DA, "r": r,
                     "theta": float(args.get("lengthM", 2000.0)) / self.Rs, "label": args.get("label", "path")}
        self.label = self.path["label"]
        self.teleport_flag = True
        self.advance(tick)
        return tick, tick + self.path["n"]

    def advance(self, tick):
        p = self.path
        if not p:
            return None
        k = min(p["n"], max(0, tick - p["start"]))
        th = p["theta"] * k / p["n"]
        c, s = math.cos(th), math.sin(th)
        radial = _v_add(_v_mul(p["uA"], c), _v_mul(p["DA"], s))
        self.T = _v_add(self.C, _v_mul(radial, p["r"]))
        self.fwd = _v_unit(_v_add(_v_mul(p["uA"], -s), _v_mul(p["DA"], c)))
        self.up = radial
        if k >= p["n"]:
            self.path = None
            return "done"
        return None

    def motion_start(self, kind, tick, **kw):
        """S2 stand-in motions: "line" from fromSn to toSn at speedMps; "bob" a vertical sine at (x, z); S3.N2 "yaw": the
 view turns about SN +Y by amplitudeDeg * sin(2 pi hz t) around the current heading for `seconds`, position fixed,
 then returns to the start heading (no teleport flag)."""
        self.path = None
        if kind == "yaw":
            self.motion = {"kind": "yaw", "start": tick, "n": max(1, int(round(float(kw.get("seconds", 10.0)) * 60.0))),
                           "amp": float(kw.get("amplitudeDeg", 30.0)), "hz": float(kw.get("hz", 0.5)),
                           "d0": self.mapdir(self.T, self.fwd), "u0": self.mapdir(self.T, self.up),
                           "h0": self.heading_of(self.fwd)}
            self.label = kw.get("label", "yaw")
            self.motion_advance(tick)
            return tick, tick + self.motion["n"]
        if kind == "line":
            A, B = tuple(float(v) for v in kw["fromSn"]), tuple(float(v) for v in kw["toSn"])
            n = max(1, int(round(math.dist(A, B) / max(1e-3, float(kw.get("speedMps", 5.0))) * 60.0)))
            self.motion = {"kind": "line", "start": tick, "n": n, "A": A, "B": B}
            first = A
        else:
            self.motion = {"kind": "bob", "start": tick, "n": int(float(kw.get("seconds", 30.0)) * 60.0),
                           "x": float(kw.get("x", 0.0)), "z": float(kw.get("z", 0.0)),
                           "c": float(kw.get("centreY", -0.125)), "amp": float(kw.get("amplitudeM", 0.05)),
                           "hz": float(kw.get("hz", 1.0))}
            first = (self.motion["x"], self.motion["c"], self.motion["z"])
        here = self.F(self.T)
        self.label = kw.get("label", kind)
        if math.dist(here, first) > SNAP_M:
            self.teleport_flag = True
        self.motion_advance(tick)
        return tick, tick + self.motion["n"]

    def motion_advance(self, tick):
        m = self.motion
        if not m:
            return None
        k = min(m["n"], max(0, tick - m["start"]))
        if m["kind"] == "yaw":
            t = k / 60.0
            ang = 0.0 if k >= m["n"] else math.radians(m["amp"]) * math.sin(2.0 * math.pi * m["hz"] * t)
            ca, sa = math.cos(ang), math.sin(ang)

            def rot(v):   # about SN +Y, clockwise seen from above (heading + angle), as yaw_step
                return (v[0] * ca + v[2] * sa, v[1], -v[0] * sa + v[2] * ca)
            self.fwd = _v_unit(self.mapdir_inv(self.T, rot(m["d0"])))
            self.up = _v_unit(self.mapdir_inv(self.T, rot(m["u0"])))
            if k >= m["n"]:
                self.motion = None
                return "done"
            return None
        if m["kind"] == "line":
            a = k / float(m["n"])
            Q = tuple(x + (y - x) * a for x, y in zip(m["A"], m["B"]))
        else:
            t = k / 60.0
            Q = (m["x"], m["c"] + m["amp"] * math.sin(2.0 * math.pi * m["hz"] * t), m["z"])
        T = self.Finv(Q)
        # keep the heading and pitch: re-express the view in the local frame at the new point
        d, u = self.mapdir(self.T, self.fwd), self.mapdir(self.T, self.up)
        self.T = T
        self.fwd, self.up = _v_unit(self.mapdir_inv(T, d)), _v_unit(self.mapdir_inv(T, u))
        if k >= m["n"]:
            self.motion = None
            return "done"
        return None

    def project_se(self, X):
        """SE pixel of an SE-world point through the current SE pinhole camera (the GPS marker of S3.N2, the corner
 pixels of S3.N3): Right = Forward x Up, pixels from the top-left corner, y down. None behind the camera."""
        T, fwd, up = self.T, self.fwd, self.up
        R = _v_cross(fwd, up)
        d = _v_sub(X, T)
        lx, ly, lz = _v_dot(d, R), _v_dot(d, up), _v_dot(d, fwd)
        if lz <= 1e-6:
            return None
        t = math.tan(math.radians(self.fov) / 2.0)
        aspect = self.W / float(self.H)
        nx, ny = lx / (lz * t * aspect), ly / (lz * t)
        return {"px": [(nx + 1.0) / 2.0 * self.W, (1.0 - ny) / 2.0 * self.H], "depth": lz}

    def pixel(self, ahead, label, tick):
        """SE pixel of the point `ahead` m along the view axis through an SE pinhole camera (Right = Forward x Up,
 Backward = -Forward; pixels from the top-left corner, y down); pointSn = F(point)."""
        T, fwd, up = self.T, self.fwd, self.up
        X = _v_add(T, _v_mul(fwd, ahead))
        R = _v_cross(fwd, up)
        d = _v_sub(X, T)
        lx, ly, lz = _v_dot(d, R), _v_dot(d, up), -_v_dot(d, fwd)
        t = math.tan(math.radians(self.fov) / 2.0)
        aspect = self.W / float(self.H)
        nx, ny = lx / (-lz * t * aspect), ly / (-lz * t)
        px = [(nx + 1.0) / 2.0 * self.W, (1.0 - ny) / 2.0 * self.H]
        return {"label": label, "tick": tick, "sePx": px, "screen": [self.W, self.H], "vFovDeg": self.fov,
                "aheadM": ahead, "camSe": list(T), "fwdSe": list(fwd), "upSe": list(up), "pointSe": list(X),
                "pointSn": list(self.F(X)), "s": self.Rs / math.sqrt(_v_dot(_v_sub(T, self.C), _v_sub(T, self.C)))}

    # ---- C5 zone (SE plugin ZoneTracker + S1Perf) -------------------------------------------------------------------
    def update_zone(self, cam_pos, in_world):
        Z = L.CONSTS["Zone"]
        self.cam_sn_y = float(cam_pos[1])
        if not in_world:
            self.zone_in, self.zone = False, Z["OffSite"]
            return
        rho = max(abs(cam_pos[0]), abs(cam_pos[2]))
        self.zone_in = rho <= (7900.0 if self.zone_in else 7800.0)
        if not self.zone_in:
            self.zone = Z["Far"]
        elif rho <= 1950.0:
            self.zone = Z["Core"]
        elif rho <= 2048.0:
            self.zone = Z["TileVoid"]
        else:
            self.zone = Z["OuterVoid"]

    # ---- publication ----------------------------------------------------------------------------------------------
    def publish(self, sec, tick, world_epoch, in_world, emit):
        f32v = self.wire_ref.f32v
        qnow = S.qpc()
        state = (self.T, self.fwd, self.up, self.fov, qnow, self.label, self.teleport_flag)
        if self.mutant == "prev-tick":
            # S1.M2 as the SE plugin implements it (FrameWriter.Capture from IPlugin.Update): the camera of the previous
 # tick, stamped with the QPC of this publication
            prev = self.prev_state or state
            self.prev_state = state
            pub = prev[:4] + (qnow,) + prev[5:]
        else:
            pub = state
        T, fwd, up, fov, qcap, label, tele = pub
        cam_pos = f32v(self.F(T))
        cam_rot = f32v(self.wire_ref.look_rotation(self.mapdir(T, fwd), self.mapdir(T, up)))
        self.update_zone(cam_pos, in_world)
        flags = (1 << L.SE_FRAME_FLAGS["inWorld"]) if in_world else 0
        if tele:
            flags |= 1 << L.SE_FRAME_FLAGS["teleported"]
        rec = L.SE_FRAME.pack(flags=flags, tick=tick, qpcCapture=qcap, simTimeSec=tick / 60.0, simSpeed=1.0,
                              worldEpoch=world_epoch, camPos=cam_pos, camRot=cam_rot, vFovDeg=fov, near=self.near,
                              aspect=self.W / float(self.H), cameraMode=L.CONSTS["CameraMode"]["Spectator"] if
                              self.camera_mode is None else self.camera_mode, charS=1.0)
        S.seqlock_write(sec, L.REGION["C1"].offset, rec)
        self.last_qpc = qcap
        self.published_teleport = bool(tele)
        self.teleport_flag = False
        if not in_world:
            return
        if self.site_logged != world_epoch:
            self.site_logged = world_epoch
            emit("s1.site", "DE.Site OK (stand-in) C=%s Rs=%.8f" % (list(self.C), self.Rs), C=list(self.C), Rs=self.Rs,
                 siteVersion=L.SITE_VERSION, siteHash=L.hex64(L.SITE_HASH_V1), worldEpoch=world_epoch,
                 qpcFrequency=S.QPC_FREQUENCY, standin=True, mutant=self.mutant)
        r = math.sqrt(_v_dot(_v_sub(T, self.C), _v_sub(T, self.C)))
        emit("s1.frame", tick=tick, worldEpoch=world_epoch, qpcCapture=qcap, label=label, camSe=list(T),
             fwdSe=list(fwd), upSe=list(up), camPos=list(cam_pos), camRot=list(cam_rot), vFovDeg=fov, near=self.near,
             aspect=self.W / float(self.H), cameraMode=4, flags=flags,
             gravSe=list(_v_mul(_v_sub(T, self.C), -GRAVITY / r)), depthWm=r - self.Rs)

def motion_command(se, args, rid):
    """de.motion on the stand-in SE camera (S2 kinds bob and line; S3.N2 kind yaw), shared by the S2 and S3a paths."""
    if se.phase != L.SE_PHASE["InWorld"]:
        return {"id": rid, "ok": False, "error": "not in world"}
    kind = str(args.get("kind", ""))
    cam = se.cam
    if kind in ("bob", "yaw"):
        t0, t1 = cam.motion_start(kind, se.tick, **{k: v for k, v in args.items() if k != "kind"})
    elif kind == "line" and "toSn" in args:
        t0, t1 = cam.motion_start("line", se.tick, fromSn=args.get("fromSn") or list(cam.F(cam.T)),
                                  toSn=args["toSn"], speedMps=float(args.get("speedMps", 5.0)),
                                  label=args.get("label", "line"))
    else:
        return {"id": rid, "ok": False, "error": "de.motion: kind bob {x, z, centreY, amplitudeM, hz, seconds}, "
                                                 "kind line {fromSn, toSn, speedMps} or kind yaw {amplitudeDeg, hz, "
                                                 "seconds}"}
    se.emit("s3.motion" if kind == "yaw" else "s2.motion", "de.motion %s ticks %d..%d" % (kind, t0, t1), kind=kind,
            startTick=t0, endTick=t1, qpc=S.qpc(), args=args)
    return {"id": rid, "ok": True, "line": "de.motion %s" % kind, "startTick": t0, "endTick": t1}

class S2Se:
    """The SE plugin's S2 behaviour in the stand-in (module docstring, S2)."""
    VIEW = {"Space": 0, "Track": 1, "Prime": 1, "Underwater": 2, "Surfacing": 3}
    MODE = {"Track": 1, "Prime": 2}
    PERF = {"Space": 0, "Track": 1, "Prime": 2, "Underwater": 2, "Surfacing": 2}
    SCRIPT = (("Space", 3.0), ("Track", 3.0), ("Prime", 3.0), ("Underwater", 4.0), ("Surfacing", 0.1), ("Prime", 2.0),
              ("Track", 2.0))

    def __init__(self, se, a):
        self.se, self.a = se, a
        self.view = "Space"
        self.last_switch = -1e9
        self.prime_t = -1e9
        self.below = False
        self.crossing_tick = 0
        self.margins = False
        self.margins_presents = 0
        self.skip = False
        self.skip_off_presents = 0
        self.skip_request_t = None
        self.forced_see = False          # the SE plugin's ForcedSeeThrough: OR-ed with the machine's request
        self.forced_margins = self.forced_skip = self.forced_view = None
        self.prev_y = None
        self.vy = 0.0
        self.resettle_tick = 0
        self.resettle = True
        self.toggles = 0
        self.mix = 0.0
        self.mix_active = False
        self.zorder_ok = not bool(a.s2_window)
        self.zorder_pending = 0
        self.sn_hwnd_seen = 0
        self.win = None
        self.hwnd = 0
        self.next_sample = 0.0
        self.script_i = 0
        self.script_t = None
        self.sn_ready = False
        self.readback_wanted = False

    # -------------------------------------------------------------------------------------------------- helpers
    def log(self, ev, text, **kw):
        self.se.emit(ev, text, qpc=S.qpc(), tick=self.se.tick, **kw)

    def start(self):
        if self.a.s2_window:
            from fake_sn_host import StandinWindow   # noqa: E402 (same folder)
            self.win = StandinWindow("DE_STANDIN_SE", self.a.s2_window_rect, (30, 90, 40))
            self.hwnd = self.win.start()
        self.se.sec.set_hdr("seHwnd", self.hwnd)

    def close(self):
        if self.win:
            self.win.close()

    def zorder_assert(self, why):
        sn = int(self.se.sec.hdr("snHwnd") or 0)
        if not (self.hwnd and sn):
            return False
        import ctypes
        u = ctypes.windll.user32
        u.SetWindowPos.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                   ctypes.c_int, ctypes.c_uint]
        ok = bool(u.SetWindowPos(sn, self.hwnd, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x4000))
        self.zorder_pending = self.se.tick + 3
        self.log("s2.zorder", "DE.ZOrder assert (%s): SetWindowPos(snHwnd %s, seHwnd %s) %s" % (
            why, hex(sn), hex(self.hwnd), "ok" if ok else "FAILED"), ok=ok, result="assert")
        return ok

    def zorder_verify(self):
        root = os.path.normpath(os.path.join(_HERE, "..", "..", "tools", "display"))
        if root not in sys.path:
            sys.path.insert(0, root)
        import walker   # noqa: E402
        sn = int(self.se.sec.hdr("snHwnd") or 0)
        r = walker.walk(self.hwnd, sn)
        self.zorder_ok = bool(r["ok"])
        self.log("s2.zorder", "DE.ZOrder %s (walker): %s" % ("OK" if r["ok"] else "LOST", r["verdict"]),
                 ok=self.zorder_ok, result="OK" if r["ok"] else "LOST")

    # ----------------------------------------------------------------------------------------------- per tick
    def tick(self):
        se, cam, a = self.se, self.se.cam, self.a
        tick = se.tick
        now = time.perf_counter()
        in_world = se.phase == L.SE_PHASE["InWorld"]
        if self.win:
            sn = int(se.sec.hdr("snHwnd") or 0)
            if sn and sn != self.sn_hwnd_seen:
                self.sn_hwnd_seen = sn
                self.zorder_assert("new snHwnd")
            if self.zorder_pending and tick >= self.zorder_pending:
                self.zorder_pending = 0
                self.zorder_verify()
        Q = cam.F(cam.T)
        y, rho = Q[1], max(abs(Q[0]), abs(Q[2]))
        self.vy = 0.0 if self.prev_y is None else (y - self.prev_y) * a.tick_hz
        self.prev_y = y
        if cam.published_teleport:
            self.resettle, self.resettle_tick = True, tick
        c7 = se.c7.value or {}
        f7 = int(c7.get("flags", 0))
        bit = lambda n: bool(f7 & (1 << L.SN_STATUS_FLAGS[n]))  # noqa: E731
        pst = int(c7.get("presentedSeTick", 0))
        if self.resettle and bit("streamingSettled") and bit("areaBuilt") and pst > self.resettle_tick:
            self.resettle = False
        fresh = tick - 4 <= pst <= tick + 1
        Z = L.CONSTS["Zone"]
        on_site = bool(se.hello_epoch) and in_world and cam.zone in (Z["Core"], Z["TileVoid"], Z["OuterVoid"])
        alive = se.state == "alive"
        self.sn_ready = alive and int(c7.get("snPhase", 0)) == L.SN_PHASE["Active"] and bit("areaBuilt") and \
            not self.resettle and fresh and bit("rectOk") and bit("uiHidden")
        m4 = a.s2_mutant == "M4"
        thr_in, thr_out = (-0.125, -0.125) if m4 else (-0.20, -0.05)
        if not self.below and y <= thr_in:
            self.below, self.crossing_tick = True, tick
        elif self.below and y > thr_out:
            self.below = False
        if self.below and cam.published_teleport:
            self.crossing_tick = tick
        dwell = m4 or (now - self.last_switch) >= 0.25
        prime_cond = y <= 1.0 or (y <= 5.0 and self.vy <= -0.2) or (self.vy < 0 and y <= 2.0 * -self.vy)
        if prime_cond:
            self.prime_t = now
        v, nv, why = self.view, self.view, None
        if a.s2_script == "states":
            nv, why = self.scripted(now, tick)
        elif self.forced_view:
            nv, why = self.forced_view, "forced"
        elif v == "Space":
            if on_site and y < 300.0 and alive:
                nv, why = "Track", "enter Approach"
        elif v in ("Track", "Prime"):
            if not on_site or y > 350.0 or not alive:
                nv, why = "Space", "left site/zone/bridge" if alive else "peer not alive"
            elif v == "Track" and prime_cond:
                nv, why = "Prime", "enter Prime"
            elif v == "Prime" and y <= thr_in and self.sn_ready and self.zorder_ok and dwell and \
                    pst >= self.crossing_tick and self.margins and self.margins_presents >= 1:
                nv, why = "Underwater", "enter Underwater"
            elif v == "Prime" and not prime_cond and y > 1.0 and now - self.prime_t >= (10.0 if y < 5.0 else 3.0):
                nv, why = "Track", "Prime quiet: 10 s below +5 m, 3 s above"
        elif v == "Underwater":
            if not on_site or not alive:
                nv, why = "Surfacing", "surface: site/zone/bridge"
            elif self.resettle and not self.sn_ready:
                nv, why = "Surfacing", "surface: not ready after teleport"
            elif y > thr_out and dwell:
                nv, why = "Surfacing", "surface: waterline"
        elif v == "Surfacing" and not self.skip:
            if on_site and alive and (prime_cond or y <= 1.0):
                nv, why = "Prime", "surfaced to Prime"
            elif on_site and alive and y < 300.0:
                nv, why = "Track", "surfaced to Prime"
            else:
                nv, why = "Space", "surfaced to Space"
        if nv != v:
            if (v == "Underwater") != (nv == "Underwater"):
                self.last_switch = now
            extra = ""
            if nv == "Underwater":
                extra = " crossing->entry %.1f ms (T-D1/S2.N7: presentedSeTick >= crossingTick)" % (
                    (tick - self.crossing_tick) * 1000.0 / a.tick_hz)
            text = ("DE.View %s -> %s tick=%d y=%.4f vy=%.3f rho=%.2f snReady=%s zOrderOk=%s marginsReady=%s "
                    "crossingTick=%d presentedSeTick=%d (%s)%s" % (v, nv, tick, y, self.vy, rho, self.sn_ready,
                                                                  self.zorder_ok, self.margins_presents >= 1,
                                                                  self.crossing_tick, pst, why or "-", extra))
            self.log("s2.state", text, **{"from": v, "to": nv, "y": y, "vy": self.vy, "rho": rho,
                                         "snReady": self.sn_ready, "zOrderOk": self.zorder_ok,
                                         "crossingTick": self.crossing_tick, "presentedSeTick": pst, "reason": why})
            self.view = nv
        self.display(now)
        self.audio(now)
        if now >= self.next_sample:
            self.next_sample = now + 1.0
            se.emit("sample", qpc=S.qpc(), gameplayTick=tick, frameTimeAvgMs=8.3333, frameTimeMaxMs=9.5, fps=120,
                    paused=False, foreground=True, viewState=self.VIEW[self.view], standin=True)

    def scripted(self, now, tick):
        if self.script_t is None:
            self.script_t = now
        name, dur = self.SCRIPT[self.script_i]
        if now - self.script_t >= dur:
            self.script_i = (self.script_i + 1) % len(self.SCRIPT)
            self.script_t = now
            name = self.SCRIPT[self.script_i][0]
            if name == "Underwater":
                self.crossing_tick = tick
        return name, "scripted"

    def display(self, now):
        """Emulated render thread: one request per tick, presented at once (the next tick sees it presented)."""
        nv = self.view
        want_m = self.forced_margins if self.forced_margins is not None else (
            nv in ("Prime", "Underwater", "Surfacing") or self.forced_see)
        want_s = self.forced_skip if self.forced_skip is not None else (self.forced_see or nv == "Underwater")
        m1 = self.a.s2_mutant == "M1"
        if self.margins:
            self.margins_presents += 1
        if not self.skip:
            self.skip_off_presents += 1
        if want_m and not self.margins:
            self.margins, self.margins_presents = True, 0
            self.log("s2.display", "DE.Display margins ON (-1) hr=0x00000000 %slabel=%s presents=%d" % (
                "(MUTANT S2.M1: DwmExtendFrameIntoClientArea NOT called) " if m1 else "S_OK ", nv, self.se.tick),
                action="marginsOn", hr=0, mutantNoCall=m1)
        if want_s and not self.skip and self.margins and self.margins_presents >= 1:
            self.skip = True
            ms = (now - self.skip_request_t) * 1000.0 if self.skip_request_t else 16.7
            self.skip_request_t = None
            self.log("s2.display", "DE.Display see-through first skipped frame presented +%.1f ms after the request "
                     "(%s, margins on for %d presents)" % (ms, nv, self.margins_presents), action="skipPresented",
                     requestToPresentMs=round(ms, 2))
        elif want_s and not self.skip and self.skip_request_t is None:
            self.skip_request_t = now
        if not want_s and self.skip:
            self.skip, self.skip_off_presents = False, 0
        if not want_m and self.margins and not self.skip and self.skip_off_presents >= 1:
            self.margins = False
            self.log("s2.display", "DE.Display margins OFF (0) hr=0x00000000 %slabel=%s presents=%d" % (
                "(MUTANT S2.M1: DwmExtendFrameIntoClientArea NOT called) " if m1 else "S_OK ", nv, self.se.tick),
                action="marginsOff", hr=0, mutantNoCall=m1)
        if self.readback_wanted:
            self.readback_wanted = False
            gt0 = 0.012 if self.skip else 1.0
            lt1 = 1.0 if self.skip else 0.0
            self.log("s2.alpha", "DE.Display alpha mode=F label=%s margins=%s scene=%s alphaFill=%s 2560x1440 format="
                     "R8G8B8A8_UNorm_SRgb pixels=3686400 alphaGt0=%d (%.2f %%) alphaLt1=%d (%.2f %%) alpha0=0 [stand-in]" % (
                         nv, "on" if self.margins else "off", "skipped" if self.skip else "drawn",
                         "done" if not self.skip else "no", int(gt0 * 3686400), 100 * gt0, int(lt1 * 3686400), 100 * lt1),
                     fracAlphaGt0=gt0, fracAlphaLt1=lt1, mode="seeThrough" if self.skip else "scene", emulated=True)

    def audio(self, now):
        active = self.view == "Underwater"
        target = 1.0 if active else 0.0
        step = (1.0 / self.a.tick_hz) * 1000.0 / 300.0
        self.mix = min(target, self.mix + step) if target > self.mix else max(target, self.mix - step)
        if active != self.mix_active:
            self.mix_active = active
            g, mu = self.volumes()
            self.log("s2.audio", "DE.Audio gate=open (inWorld=True focused=True muteWhenUnfocused=True paused=False "
                     "gui=False) mix=%.3f target=%.0f seMixActive=%s game=%.3f music=%.3f hud=0.8 (config game 0.8 "
                     "music 0.6)" % (self.mix, target, active, g, mu), seMixActive=active, game=g, music=mu, hud=0.8,
                     configGame=0.8, configMusic=0.6, mix=self.mix, written=True)

    def volumes(self):
        return 0.8 + (0.56 - 0.8) * self.mix, 0.6 * (1.0 - self.mix)

    # -------------------------------------------------------------------------------------------------- commands
    def command(self, cmd, args, rid):
        args = args if isinstance(args, dict) else {}

        def sw(key):
            v = args.get(key)
            if v is None:
                return "absent"
            t = str(v).lower()
            return True if t in ("on", "true", "1") else (False if t in ("off", "false", "0") else None)
        done = []
        if cmd == "de.display":
            for key, attr in (("margins", "forced_margins"), ("skip", "forced_skip")):
                v = sw(key)
                if v != "absent":
                    setattr(self, attr, v)
                    done.append("%s %s" % (key, "auto" if v is None else ("on" if v else "off")))
            if args.get("toggle") in (True, "true", 1):
                t0 = time.perf_counter()
                self.forced_see = not self.forced_see
                self.toggles += 1
                um = (time.perf_counter() - t0) * 1000.0
                self.log("s2.display", "DE.Display toggle #%d see-through %s via de.display mode=F foregroundIsSe=%s "
                         "updateMs=%.3f" % (self.toggles, "ON" if self.forced_see else "OFF", False, um),
                         action="toggle", index=self.toggles, seeThrough=self.forced_see, updateMs=round(um, 4),
                         isSe=None, standin=True)
                done.append("toggle #%d see-through %s" % (self.toggles, "on" if self.forced_see else "off"))
            v = sw("seeThrough")
            if v is None:
                return {"id": rid, "ok": False, "error": "de.display seeThrough: on|off"}
            if v != "absent":
                self.forced_see = v
                done.append("see-through %s" % ("on" if v else "off"))
            if args.get("readback"):
                self.readback_wanted = True
                done.append("alpha readback requested")
            v = sw("autoFallback")
            if v not in ("absent", None):
                self.log("s2.display", "DE.Display autoFallback %s via de.display (stand-in)" % ("on" if v else "off"),
                         action="autoFallback", on=v, standin=True)
                done.append("autoFallback %s" % ("on" if v else "off"))
            if args.get("probe"):
                # review fix: the plugin's probe line, marked as a stand-in (the N0 probe row is then OTHER)
                self.log("s2.display", "DE.Display probe MEASURED (stand-in: no composition)", action="probe",
                         verdict="UNDECIDED", word="MEASURED", standin=True)
                done.append("probe: MEASURED (stand-in: no composition)")
            return {"id": rid, "ok": True, "line": "de.display: " + ("; ".join(done) or "state"),
                    "state": {"view": self.view, "margins": self.margins, "skip": self.skip, "toggles": self.toggles,
                              "forcedSeeThrough": self.forced_see, "zOrderOk": self.zorder_ok, "standin": True}}
        if cmd == "de.view":
            f = args.get("force")
            if f is not None:
                name = {"space": "Space", "track": "Track", "prime": "Prime", "underwater": "Underwater",
                        "surfacing": "Surfacing"}.get(str(f).lower())
                self.forced_view = name
            return {"id": rid, "ok": True, "line": "de.view", "state": {"view": self.view, "crossingTick":
                    self.crossing_tick, "snReady": self.sn_ready, "vy": round(self.vy, 3)}}
        if cmd == "de.audio":
            g, mu = self.volumes()
            return {"id": rid, "ok": True, "line": "de.audio dump", "audio": {
                "gateOpen": True, "mix": round(self.mix, 4), "target": 1.0 if self.mix_active else 0.0,
                "seMixActive": self.mix_active, "game": round(g, 4), "music": round(mu, 4), "hud": 0.8,
                "configGame": 0.8, "configMusic": 0.6, "standin": True}}
        if cmd == "de.zorder":
            ok = self.zorder_assert("de.zorder") if args.get("reassert") else None
            return {"id": rid, "ok": True, "line": "de.zorder", "asserted": ok, "zOrderOk": self.zorder_ok}
        if cmd == "de.motion":
            return motion_command(self.se, args, rid)
        return None

    def c5_fields(self) -> dict:
        v = self.view
        F = L.SE_STATE_FLAGS
        flags = 1 << F["seForeground"]
        if self.skip:
            flags |= 1 << F["seeThrough"]
        if self.margins:
            flags |= 1 << F["marginsOn"]
        if self.zorder_ok:
            flags |= 1 << F["zOrderOk"]
        if self.mix_active:
            flags |= 1 << F["seMixActive"]
        perf = self.PERF[v]
        return {"flags": flags, "viewState": self.VIEW[v], "approachMode": self.MODE.get(v, 0), "perfMode": perf,
                "targetFps": 0 if perf == 2 else 10, "snBusTarget": 1.0 if v == "Underwater" else 0.0,
                "crossfadeMs": 300.0, "seRect": tuple(self.a.s2_se_rect), "camVelSn": (0.0, self.vy, 0.0),
                "crossingTick": self.crossing_tick, "displayMode": 0}

# ======================================================================================================================
# S3a (ROADMAP S3 "tools/tests": the stand-in SE side of the first block). Everything below is the SE side's job done
# by an independent Python writer: the C3 model stream (a procedural 2.5 m cube), the MaterialDef pointing at SE's real
# armor DDS files (read in place: paths resolved from the install, headers read for the log, nothing copied), the C4
# GridAdd + PartAdd, the C2 static pose (o, q, s, k) from an SE matrix at F^-1(lab point) and the pivot P* nearest to
# the camera (tools/worldmap/wire_ref.py wire_pose, the reference implementation, not WireMath), the GPS-marker pixel
# of S3.N2 and the analytic corner pixels of S3.N3.
# ======================================================================================================================

S3_COMMANDS = ("de.s3",)
SE_CONTENT = os.environ.get("DEEPENG_SE_CONTENT") or \
    r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content"
# name -> (folder under Content, base name): the maps are <base>_cm.dds, <base>_ng.dds, <base>_add.dds (read in place).
# large_edges is the set whose header ROADMAP/probe-ship-render 2.2 verified (2048 x 1024, 12 mips, BC7 DXGI 99/98/99);
# large_square_plate is the armor face (8192 x 4096, 14 mips; its _add is a 4 x 4 uniform map, 196 bytes).
S3A_TEXTURE_SETS = {"large_edges": ("Textures/Models/Cubes/armor", "large_edges"),
                    "large_square_plate": ("Textures/Models/Cubes/armor", "large_square_plate")}
S3A_GRID_ID = 0x0044455333410001          # "DES3A" + 1 (stand-in entity ids never collide with SE's)
S3A_HALF = 1.25                           # large grid cell 2.5 m (CONTRACT 2.1)
S3A_MODEL_PATH = "deepengineers/standin/s3a_cube_2.5m.mwm"         # synthetic: the stand-in has no.mwm
S3A_GLYPH_MODEL_PATH = "deepengineers/standin/s3a_glyph_cube_2.5m.mwm"
S3A_DRAW_TECHNIQUE_MESH = 0               # VRageRender.Import.MyMeshDrawTechnique.MESH
S3A_DEFAULT_HSV = (0.0, -0.8, 0.55)       # ColorMaskHSV offsets of the stand-in's paint (HYPOTHESIS: a grey SE colour)
# N1 glyph: an asymmetric 4-quadrant colour map (u right, v down, DirectX: v = 0 is the first DDS row). A U or V flip, a
# swapped DDS row order or a 90-degree turn moves the colours to other corners of the face.
S3A_GLYPH = {"tl": (200, 40, 40), "tr": (40, 190, 40), "bl": (40, 60, 210), "br": (225, 225, 225)}
S3A_GLYPH_SIZE = 256
S3A_BUMP_TILT = 0.45                      # tangent-space tilt of the N1 bump cells (up/down in y, right/left in x)
S3A_BUMP_GRID = 8                         # the N1 bump map's cells per side (tests/s3/image_checks.py N1_GRID)
CORNER_SIGNS = tuple((sx, sy, sz) for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1))   # corner i: bit0 x, 1 y, 2 z

def s3_norm_path(p: str) -> str:
    """Lower-case normalised path with forward slashes (the key convention of the stand-in)."""
    return os.path.normpath(p).replace("\\", "/").lower()

def s3_model_key(path: str, lod: int = 0) -> int:
    """CONTRACT 5.4 ModelBegin.modelKey: FNV-1a 64 of '<path>|lod<n>' (path trimmed, / -> \\, lower-case), the
 convention of Wire's ModelCodec.ModelKey and the SE plugin (wire_ref.model_key; Subnautica treats it as opaque)."""
    import wire_ref
    return wire_ref.model_key(path, lod)

def s3_material_key(paths, technique: int, flags: int = 0) -> int:
    """CONTRACT 5.4 materialKey: FNV-1a 64 of '<cm>|<ng>|<add>|<alpha>|<technique>|<flags>' (wire_ref.material_key)."""
    import wire_ref
    p = list(paths) + [""] * (4 - len(paths))
    return wire_ref.material_key(technique, flags, p[0], p[1], p[2], p[3])

def s3_cube(h: float = S3A_HALF, u0: float = 0.0, uw: float = 0.5) -> dict:
    """A closed 2.5 m box in SE-local space (right-handed, +Y up, Forward = -Z), 4 vertices and 2 triangles per face, in
 the MyModel.Triangles convention: cross(V1 - V0, V2 - V0) is the outward normal (tools/worldmap/wire_ref.cube_mesh).
 UVs (DirectX, v = 0 at the first texture row): seen from outside with `up` (+Y for the side faces, Forward = -Z for
 the top, +Z for the bottom) at the top of the picture, u grows to the right over [u0, u0 + uw] and v grows downward
 over [0, 1]: uw = 0.5 maps the left square half of SE's 2:1 armor atlases without stretching. Tangent = direction
 of +u, binormal = direction of +v (CONTRACT 2.5: the binormal is sent, Subnautica derives w)."""
    import wire_ref
    verts, tris, _fn = wire_ref.cube_mesh(h)
    out = []
    for f in range(6):
        idx = range(4 * f, 4 * f + 4)
        c = tuple(sum(verts[i][k] for i in idx) / 4.0 for k in range(3))
        n = _v_unit(c)
        if abs(n[1]) > 0.5:
            up = (0.0, 0.0, -1.0) if n[1] > 0 else (0.0, 0.0, 1.0)
        else:
            up = (0.0, 1.0, 0.0)
        right = _v_cross(up, n)
        for i in idx:
            d = _v_sub(verts[i], c)
            u = u0 + uw * (0.5 + _v_dot(d, right) / (2.0 * h))
            v = 0.5 - _v_dot(d, up) / (2.0 * h)
            out.append({"pos": verts[i], "normal": n, "tangent": right, "binormal": _v_mul(up, -1.0), "uv": (u, v),
                        "face": f})
    faces = []
    for f in range(6):
        c = _v_mul(_v_unit(tuple(sum(verts[i][k] for i in range(4 * f, 4 * f + 4)) for k in range(3))), h)
        faces.append({"face": f, "centre": c, "normal": out[4 * f]["normal"], "right": out[4 * f]["tangent"],
                      "up": _v_mul(out[4 * f]["binormal"], -1.0)})
    return {"verts": out, "tris": [tuple(t) for t in tris], "half": h, "u0": u0, "uw": uw, "faces": faces,
            "bounds": (-h, -h, -h, h, h, h)}

def s3_model_payload(cube: dict, material_key: int, technique: int = S3A_DRAW_TECHNIQUE_MESH) -> bytes:
    """CONTRACT 5.4 model payload: vertices of 56 B (pos, normal, tangent, binormal, uv), then u32 indices in
 MyModel.Triangles order, then submeshes of 24 B (materialKey, triStart, triCount, drawTechnique, pad)."""
    import struct
    V, SM = L.RECORDS["ModelVertex"], L.RECORDS["ModelSubmesh"]
    out = bytearray()
    for v in cube["verts"]:
        out += V.pack(pos=v["pos"], normal=v["normal"], tangent=v["tangent"], binormal=v["binormal"], uv=v["uv"])
    for t in cube["tris"]:
        out += struct.pack("<III", *t)
    out += SM.pack(materialKey=material_key, triStart=0, triCount=len(cube["tris"]), drawTechnique=technique)
    return bytes(out)

def s3_model_messages(model_key: int, cube: dict, material_key: int, pattern_scale: float = 1.0,
                      chunk_bytes: int = 1 << 20) -> list:
    """[(C3 type, payload bytes)]: ModelBegin, ModelChunk x n (<= chunk_bytes of model payload each, <= 4 MiB), ModelEnd
 (payloadHash = FNV-1a 64 over the concatenated chunk payloads, D39)."""
    payload = s3_model_payload(cube, material_key)
    chunk_bytes = max(64, min(int(chunk_bytes), L.C3_MAX_CHUNK))
    chunks = [payload[i:i + chunk_bytes] for i in range(0, len(payload), chunk_bytes)] or [b""]
    R = L.RECORDS
    msgs = [(L.C3_TYPES["ModelBegin"], R["ModelBegin"].pack(
        modelKey=model_key, lod=0, vertexCount=len(cube["verts"]), indexCount=3 * len(cube["tris"]), submeshCount=1,
        flags=L.CONSTS["ModelFlags"]["HasTangents"], bounds=cube["bounds"], patternScale=pattern_scale,
        chunkCount=len(chunks), totalBytes=len(payload)))]
    for i, c in enumerate(chunks):
        msgs.append((L.C3_TYPES["ModelChunk"], R["ModelChunkHeader"].pack(modelKey=model_key, chunkIndex=i,
                                                                          bytes=len(c)) + c))
    msgs.append((L.C3_TYPES["ModelEnd"], R["ModelEnd"].pack(modelKey=model_key, payloadHash=L.fnv1a64(payload))))
    return msgs

def s3_material_def(material_key: int, paths, technique: int = S3A_DRAW_TECHNIQUE_MESH, flags: int = 0) -> bytes:
    """MaterialDef: header (materialKey, drawTechnique, flags) + four strings (u16 length + UTF-8) in the order
 colour-metal, normal-gloss, add-maps, alpha-mask (absolute paths; "" when the material has none)."""
    import struct
    out = bytearray(L.RECORDS["MaterialDefHeader"].pack(materialKey=material_key, drawTechnique=technique, flags=flags))
    for p in paths:
        b = (p or "").encode("utf-8")
        out += struct.pack("<H", len(b)) + b
    return bytes(out)

def s3_decode_material(payload: bytes) -> dict:
    import struct
    d = L.RECORDS["MaterialDefHeader"].unpack(payload)
    pos, strings = 16, []
    for _ in range(4):
        n = struct.unpack_from("<H", payload, pos)[0]
        strings.append(payload[pos + 2:pos + 2 + n].decode("utf-8"))
        pos += 2 + n
    if pos != len(payload):
        raise ValueError("MaterialDef: %d trailing bytes" % (len(payload) - pos))
    d["paths"] = strings
    return d

def s3_decode_model(begin: dict, payload: bytes) -> dict:
    """The model payload back into vertices, triangles and submeshes (the Subnautica side's reading, for the tests)."""
    import struct
    V, SM = L.RECORDS["ModelVertex"], L.RECORDS["ModelSubmesh"]
    nv, ni, ns = begin["vertexCount"], begin["indexCount"], begin["submeshCount"]
    need = nv * V.size + ni * 4 + ns * SM.size
    if need != len(payload):
        raise ValueError("model payload %d bytes, header says %d" % (len(payload), need))
    verts = [V.unpack(payload, i * V.size) for i in range(nv)]
    off = nv * V.size
    idx = struct.unpack_from("<%dI" % ni, payload, off)
    off += ni * 4
    subs = [SM.unpack(payload, off + i * SM.size) for i in range(ns)]
    return {"verts": verts, "tris": [tuple(idx[i:i + 3]) for i in range(0, ni, 3)], "submeshes": subs}

def s3_winding_measure(verts, tris, mirror_on: bool = True, swap_on: bool = True) -> float:
    """CONTRACT 2.5 / S3.C3 as Subnautica measures it: the fraction of Unity triangles (I0, I2, I1 of the mirrored mesh)
 whose geometric normal Cross(b - a, c - a) agrees with the mirrored vertex normals. mirror_on/swap_on False are
 the seeded failures S3.M1 (Z mirror dropped) and S3.M2 (triangle swap dropped)."""
    def mir(v):
        return (v[0], v[1], -v[2]) if mirror_on else tuple(v)
    ok = 0
    for t in tris:
        a, b, c = (t[0], t[2], t[1]) if swap_on else t
        pa, pb, pc = mir(verts[a]["pos"]), mir(verts[b]["pos"]), mir(verts[c]["pos"])
        g = _v_cross(_v_sub(pb, pa), _v_sub(pc, pa))
        n = (0.0, 0.0, 0.0)
        for i in t:
            n = _v_add(n, mir(verts[i]["normal"]))
        ok += 1 if _v_dot(g, n) > 0 else 0
    return ok / float(len(tris)) if tris else 0.0

def dds_header_info(path: str) -> dict:
    """The 148-byte DX10 DDS header of a file read in place (never copied): size, mips, DXGI format, the expected file
 size of a BC7 mip chain (16 bytes per 4 x 4 block, blocks rounded up) and whether the file matches it."""
    import struct
    info = {"path": path, "exists": os.path.exists(path)}
    if not info["exists"]:
        return info
    with open(path, "rb") as f:
        h = f.read(148)
    info["bytes"] = os.path.getsize(path)
    if len(h) < 128 or h[:4] != b"DDS ":
        info["error"] = "not a DDS file"
        return info
    _size, _flags, height, width, _pitch, _depth, mips = struct.unpack_from("<7I", h, 4)
    info.update(width=width, height=height, mips=max(1, mips), fourcc=h[84:88].decode("ascii", "replace"))
    if h[84:88] == b"DX10" and len(h) >= 148:
        dxgi, dim, _misc, arr, _misc2 = struct.unpack_from("<5I", h, 128)
        info.update(dxgi=dxgi, dimension=dim, arraySize=arr, srgb=dxgi in (99, 29, 72, 75, 78),
                    format={98: "BC7_UNORM", 99: "BC7_UNORM_SRGB"}.get(dxgi, "DXGI %d" % dxgi))
        if dxgi in (98, 99):
            exp = 148 + sum(((max(1, width >> m) + 3) // 4) * ((max(1, height >> m) + 3) // 4) * 16
                            for m in range(info["mips"]))
            info.update(expectedBytes=exp, sizeMatches=exp == info["bytes"])
    return info

def s3_texture_paths(content: str, texture_set: str) -> list:
    folder, base = S3A_TEXTURE_SETS[texture_set]
    root = os.path.join(content, *folder.split("/"))
    out = []
    for suffix in ("_cm", "_ng", "_add"):
        p = os.path.join(root, base + suffix + ".dds")
        if not os.path.exists(p):
            # the Content folder mixes.dds and.DDS (case-insensitive file system; keep the name as found)
            for n in (os.listdir(root) if os.path.isdir(root) else ()):
                if n.lower() == (base + suffix + ".dds").lower():
                    p = os.path.join(root, n)
                    break
        out.append(os.path.abspath(p))
    return out + [""]

# ---- BC7 (mode 6, solid blocks) and a DX10 DDS writer: the N1 glyph and bump maps, generated at run time ----------

def bc7_solid_block(rgba) -> bytes:
    """One BC7 mode-6 block of a single colour: endpoints E0 = E1 = (7-bit colour, shared p-bit), all indices 0.
 The p-bit is the majority low bit of the four channels, so every channel decodes within 1/255."""
    r, g, b, a = (max(0, min(255, int(round(c)))) for c in rgba)
    p = 1 if sum(c & 1 for c in (r, g, b, a)) >= 2 else 0

    def q7(c):   # nearest (c7 << 1 | p) to c
        return max(0, min(127, (c - p + 1) // 2 if c >= p else 0))
    bits, nbits = 1 << 6, 7                     # mode 6: six 0 bits, then a 1
    for c in (r, g, b, a):
        v = q7(c)
        bits |= v << nbits
        nbits += 7
        bits |= v << nbits
        nbits += 7
    bits |= p << nbits
    nbits += 1
    bits |= p << nbits
    nbits += 1
    return bits.to_bytes(16, "little")         # 65 bits used; the 63 index bits stay 0

def dds_bc7(width: int, height: int, level_blocks, srgb: bool) -> bytes:
    """A DX10 DDS: 'DDS ' + DDS_HEADER (124 B) + DDS_HEADER_DXT10 (20 B) + the mip chain, largest first. level_blocks[m]
 is a bytes object of ((w+3)//4)*((h+3)//4)*16 bytes for mip m (DXGI 99 BC7_UNORM_SRGB or 98 BC7_UNORM)."""
    import struct
    mips = len(level_blocks)
    flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000
    hdr = struct.pack("<7I", 124, flags, height, width, len(level_blocks[0]), 0, mips) + b"\0" * 44
    hdr += struct.pack("<2I4s5I", 32, 0x4, b"DX10", 0, 0, 0, 0, 0)
    hdr += struct.pack("<5I", 0x1000 | 0x400000 | 0x8, 0, 0, 0, 0)
    dx10 = struct.pack("<5I", 99 if srgb else 98, 3, 0, 1, 0)
    data = b"DDS " + hdr + dx10
    assert len(data) == 148
    return data + b"".join(level_blocks)

def glyph_cell_kind(gx: int, gy: int) -> str:
    """The tilt of N1 bump cell (gx, gy) of the 8 x 8 grid (gx right along u, gy down along v): in each colour quadrant
 (4 x 4 cells, local (cx, cy)) the top two rows are an up/down checker ((cx + cy) even: "U") and the bottom two a
 right/left checker ((cx + cy) even: "R"). A checker inside one quadrant keeps the albedo equal and cancels any
 linear brightness gradient (tests/s3/image_checks.py n1_cell_kind is the reader's copy)."""
    cx, cy = gx % 4, gy % 4
    even = (cx + cy) % 2 == 0
    if cy < 2:
        return "U" if even else "D"
    return "R" if even else "L"

def glyph_bump_xy(kind: str):
    """(x, y) of the tangent-space normal SE's _ng stores for a cell kind, in SE's convention: SE negates the map's y
 before using its bitangent (+v, down the face), so y > 0 (G > 0.5) leans the normal towards -v: up on a side face.
 Review finding 3: the up/down cells make S3.N1b judge G' = 1 - Y and the tangent w whatever the sun's
 azimuth (light from above favours "U"); the right/left cells (+u) are the information row S3.N1x."""
    a = S3A_BUMP_TILT
    return {"U": (0.0, a), "D": (0.0, -a), "R": (a, 0.0), "L": (-a, 0.0)}[kind]

def _glyph_texel(kind: str, x: float, y: float):
    """Colour of the N1 maps at texture coordinates (x, y) in [0, 1) (y down)."""
    if kind == "cm":
        key = ("t" if y < 0.5 else "b") + ("l" if x < 0.5 else "r")
        r, g, b = S3A_GLYPH[key]
        return (r, g, b, 0)                     # alpha = metalness 0
    if kind == "ng":                            # tangent-space normal, gloss in alpha (SE _ng layout)
        gx = min(S3A_BUMP_GRID - 1, int(x * S3A_BUMP_GRID))
        gy = min(S3A_BUMP_GRID - 1, int(y * S3A_BUMP_GRID))
        tx, ty = glyph_bump_xy(glyph_cell_kind(gx, gy))
        nz = math.sqrt(max(0.0, 1.0 - tx * tx - ty * ty))
        return (255.0 * (0.5 + 0.5 * tx), 255.0 * (0.5 + 0.5 * ty), 255.0 * (0.5 + 0.5 * nz), 128)
    # add: AO 1 (R), no emissive (G), B unused, paint mask 0 (A)
    return (255, 0, 0, 0)

def glyph_dds(kind: str, size: int = S3A_GLYPH_SIZE) -> bytes:
    """The N1 maps as BC7 DDS: kind cm (sRGB, DXGI 99), ng (linear, 98), add (sRGB, 99), full mip chain."""
    levels = []
    w = size
    while True:
        bw = (w + 3) // 4
        blk = bytearray()
        for by in range(bw):
            for bx in range(bw):
                cx, cy = (bx * 4 + 2.0) / w if w >= 4 else 0.5, (by * 4 + 2.0) / w if w >= 4 else 0.5
                blk += bc7_solid_block(_glyph_texel(kind, min(cx, 0.999), min(cy, 0.999)))
        levels.append(bytes(blk))
        if w == 1:
            break
        w //= 2
    return dds_bc7(size, size, levels, srgb=(kind != "ng"))

def s3_glyph_dir() -> str:
    return os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers", "s3", "glyph")

def write_glyph_files(folder: str = None) -> list:
    """Writes the N1 glyph maps (our own synthetic test data, never a game asset) under %LOCALAPPDATA%\\DeepEngineers\\
 s3\\glyph and returns the four MaterialDef paths (cm, ng, add, alpha mask "")."""
    folder = folder or s3_glyph_dir()
    os.makedirs(folder, exist_ok=True)
    out = []
    for kind in ("cm", "ng", "add"):
        p = os.path.join(folder, "s3_glyph_%s.dds" % kind)
        data = glyph_dds(kind)
        # rewritten whenever the bytes differ
        old = None
        if os.path.exists(p) and os.path.getsize(p) == len(data):
            with open(p, "rb") as f:
                old = f.read()
        if old != data:
            with open(p, "wb") as f:
                f.write(data)
        out.append(os.path.abspath(p))
    return out + [""]

class S3aScene:
    """The SE plugin's S3 duties in the stand-in (module docstring "S3a"): one static large grid at F^-1(lab point) holding
 the procedural cube (and, with --s3a-glyph, a second block at grid cell (1, 0, 0) wearing the N1 glyph maps)."""

    def __init__(self, se, a):
        import wire_ref
        self.se, self.a, self.wire_ref = se, a, wire_ref
        self.cam = se.cam
        self.grid_id = S3A_GRID_ID
        self.placed = None              # the place reply (dict) while a grid exists
        self.queue = collections.deque()    # (ring key, type, payload, meta)
        self.sent_models = set()
        self.pose = None                # (o, q, s, k) last published (doubles)
        self.pose_logged = None
        self.prev_c2 = None
        self.gps_on = False
        self.gps_lines = 0
        self.written = {"C3": 0, "C4": 0}
        self.full = {"C3": 0, "C4": 0}
        self.texture_set = a.s3a_texture_set
        self.paths = s3_texture_paths(a.se_content, self.texture_set)
        self.dds = [dds_header_info(p) for p in self.paths[:3]]
        self.material_key = s3_material_key(self.paths, S3A_DRAW_TECHNIQUE_MESH)
        self.cube = s3_cube(S3A_HALF, 0.0, 0.5)
        self.model_key = s3_model_key(S3A_MODEL_PATH)
        self.glyph = None
        self.auto_place = tuple(a.s3a_place_at) if a.s3a_place_at else None
        self.hsv = tuple(a.s3a_hsv)

    # ---- geometry -------------------------------------------------------------------------------------------------
    def parts(self):
        """[(partKey, modelKey, materialKey, cube, local 12 floats)] of the grid (grid-local SE space)."""
        out = [(self.part_key(0), self.model_key, self.material_key, self.cube,
                (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0))]
        if self.glyph:
            g = self.glyph
            out.append((self.part_key(1), g["modelKey"], g["materialKey"], g["cube"],
                        (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 2.0 * S3A_HALF, 0.0, 0.0)))
        return out

    def part_key(self, i):
        return ((self.grid_id & 0xFFFFFFFFFFFF) << 8) | i

    def local_aabb(self):
        lo, hi = [1e30] * 3, [-1e30] * 3
        for _pk, _mk, _mat, cube, loc in self.parts():
            b = cube["bounds"]
            for k in range(3):
                lo[k] = min(lo[k], b[k] + loc[9 + k])
                hi[k] = max(hi[k], b[3 + k] + loc[9 + k])
        return tuple(lo), tuple(hi)

    def se_matrix(self, sn_point, heading_deg):
        """Rows (Right, Up, Backward, Translation) of a level grid at F^-1(sn_point), Forward along the SN heading
 (0 = SN north, 90 = east), Up = the local vertical; Right = Forward x Up (VRage)."""
        cam = self.cam
        T = cam.Finv(tuple(float(v) for v in sn_point))
        E, U, N = cam.site.frame_at(T)
        h = math.radians(heading_deg)
        fwd = _v_unit(_v_add(_v_mul(N, math.cos(h)), _v_mul(E, math.sin(h))))
        right = _v_cross(fwd, U)
        return (right, U, _v_mul(fwd, -1.0), T)

    def pivot(self):
        lo, hi = self.local_aabb()
        return self.wire_ref.pivot_nearest(self.placed["m"], lo, hi, self.cam.T)

    def compute_pose(self):
        m = self.placed["m"]
        P = self.pivot()
        o, q, s = self.wire_ref.wire_pose(self.cam.site, m, P)
        return P, o, q, s, 1.0

    def corners(self, i_part=0):
        """(SE-local corner, SE-world corner, SN Apply(o, q, s) corner, SN exact F corner) x 8 of one part's AABB."""
        P, o, q, s, _k = self.compute_pose()
        _pk, _mk, _mat, cube, loc = self.parts()[i_part]
        h = cube["half"]
        out = []
        for sg in CORNER_SIGNS:
            l = (sg[0] * h + loc[9], sg[1] * h + loc[10], sg[2] * h + loc[11])
            w = self.wire_ref.se_transform(self.placed["m"], l)
            out.append((l, w, self.wire_ref.sn_apply(o, q, s, l), self.cam.F(w)))
        return out

    # ---- records --------------------------------------------------------------------------------------------------
    def enqueue_scene(self, clear: bool):
        R, we = L.RECORDS, self.se.world_epoch
        if clear:
            # after ClearAll the host has nothing: every model and material again; a re-placement keeps what was sent
            self.queue.append(("C4", L.C4_TYPES["ClearAll"], ST.clear_all(we, self.se.link_epoch), ("clear",)))
            self.sent_models.clear()
        for _pk, mk, mat, cube, _loc in self.parts():
            if mat not in self.sent_models:
                self.sent_models.add(mat)
                paths = self.glyph["paths"] if self.glyph and mat == self.glyph["materialKey"] else self.paths
                self.queue.append(("C3", L.C3_TYPES["MaterialDef"], s3_material_def(mat, paths), ("material", mat)))
            if mk not in self.sent_models:
                self.sent_models.add(mk)
                for t, p in s3_model_messages(mk, cube, mat, chunk_bytes=self.a.s3a_chunk_bytes):
                    self.queue.append(("C3", t, p, ("model", mk) if t == L.C3_TYPES["ModelEnd"] else None))
        self.queue.append(("C4", L.C4_TYPES["GridAdd"], R["GridAdd"].pack(
            entityId=self.grid_id, targetClass=L.CONSTS["GridClass"]["LargeGrid"], flags=0, parentEntityId=0,
            worldEpoch=we), ("grid", self.grid_id)))
        for pk, mk, _mat, _cube, loc in self.parts():
            self.queue.append(("C4", L.C4_TYPES["PartAdd"], R["PartAdd"].pack(
                entityId=self.grid_id, partKey=pk, modelKey=mk, local=loc, colorMaskHSV=self.hsv, uvOffset=(0.0, 0.0),
                skinId=0, flags=0), ("part", pk)))

    def flush(self):
        n = 0
        while self.queue and n < 64:
            key, mtype, payload, meta = self.queue[0]
            ring = self.se.c3p if key == "C3" else self.se.c4p
            st = ring.try_write(mtype, payload)
            if st == S.RW_FULL:
                self.full[key] += 1
                return
            if st != S.RW_OK:
                self.se._producer_corrupt(ring)
                return
            self.queue.popleft()
            self.written[key] += 1
            self.se.written[key] += 1
            n += 1
            if meta:
                if meta[0] == "grid":
                    self.se.written_grids.add(meta[1])
                elif meta[0] == "clear":
                    self.se.written_grids.clear()
                self.se.emit("s3.record", ring=key, type=mtype, bytes=len(payload), meta=[str(x) for x in meta],
                             tick=self.se.tick)
        if not self.queue and n:
            self.se.emit("s3.flushed", "DE.S3 records written (C3 %d, C4 %d messages so far)" % (
                self.written["C3"], self.written["C4"]), tick=self.se.tick, written=self.written, full=self.full)

    def resend(self, reason):
        """A new Subnautica generation or a link-epoch resend: ClearAll, then the scene again (CONTRACT 6)."""
        self.queue.clear()
        self.sent_models.clear()
        if self.placed:
            self.enqueue_scene(clear=True)
        else:
            self.queue.append(("C4", L.C4_TYPES["ClearAll"], ST.clear_all(self.se.world_epoch, self.se.link_epoch),
                               ("clear",)))
        self.se.emit("s3.resend", "DE.S3 resend (%s): %d messages queued" % (reason, len(self.queue)), reason=reason,
                     queued=len(self.queue))

    def write_c2(self):
        """C2 every tick, same tick as C1: one static root grid, or an empty table. S3.M4 publishes the previous
 tick's pose (with C1's previous camera: the whole snapshot one tick late, stamped now)."""
        se = self.se
        ent = b""
        n = 0
        if self.placed and se.phase == L.SE_PHASE["InWorld"]:
            P, o, q, s, k = self.compute_pose()
            self.pose = (o, q, s, k)
            ent = L.RECORDS["SeEntity"].pack(
                entityId=self.grid_id, kind=L.CONSTS["SeEntityKind"]["RootGrid"],
                flags=L.CONSTS["SeEntityFlags"]["Static"], groupId=self.grid_id, o=o, q=q, s=s, k=k, v=(0.0, 0.0, 0.0),
                omega=(0.0, 0.0, 0.0), partsVersion=len(self.parts()), skinVersion=0)
            n = 1
            key = tuple(round(x, 9) for x in tuple(o) + tuple(q) + (s,))
            if key != self.pose_logged:
                self.pose_logged = key
                f32 = self.wire_ref.f32v
                lo, hi = self.local_aabb()
                # the schema of tests/s3/check_placement.py (SE s3.pose): entityId as C2 carries it (decimal), the
 # matrix rows, P*, k, the site, the float32 wire pose, the grid-local AABB
                se.emit("s3.pose", tick=se.tick, entityId=self.grid_id, gridId="0x%016x" % self.grid_id,
                        seMatrix=_rows12(self.placed["m"]), pivot=list(P), o=list(o), q=list(q), s=s, k=k,
                        site={"C": list(self.cam.C), "Rs": self.cam.Rs},
                        wire={"o": list(f32(o)), "q": list(f32(q)), "s": self.wire_ref.f32(s), "k": k},
                        localMin=list(lo), localMax=list(hi), bounds=list(lo) + list(hi), camSe=list(self.cam.T))
        cur = (n, ent)
        if self.a.s3_mutant == "M4":
            pub, self.prev_c2 = (self.prev_c2 or cur), cur
        else:
            pub = cur
        hdr = L.RECORDS["SeEntitiesHeader"].pack(entityCount=pub[0], tick=se.tick, worldEpoch=se.world_epoch,
                                                 subpartCount=0, lightCount=0)
        S.seqlock_write(se.sec, L.REGION["C2"].offset, hdr + pub[1])

    # ---- per tick -------------------------------------------------------------------------------------------------
    def tick(self):
        se = self.se
        if self.auto_place and se.hello_acked and se.phase == L.SE_PHASE["InWorld"] and not self.placed:
            self.place({"sn": list(self.auto_place), "yawDeg": self.a.s3a_heading, "label": "auto"})
            self.auto_place = None
        if se.hello_acked and not se.waiting_link_ack:
            self.flush()

    def log_gps(self):
        """S3.N2: the GPS marker (SE's sprite at the block centre) projected with this tick's camera, stamped with the C1
 qpcCapture of this tick (called after write_c1: the marker SE draws for the snapshot it publishes; with S3.M4
 the snapshot is the previous tick's while the marker stays current)."""
        se = self.se
        if self.gps_on and self.placed and se.phase == L.SE_PHASE["InWorld"]:
            c = self.placed["centreSe"]
            px = self.cam.project_se(c)
            self.gps_lines += 1
            se.emit("s3.gps", tick=se.tick, qpc=self.cam.last_qpc or S.qpc(), px=px["px"] if px else None,
                    depth=px["depth"] if px else None, screen=[self.cam.W, self.cam.H],
                    headingDeg=round(self.cam.heading_of(self.cam.fwd), 6))

    # ---- commands -------------------------------------------------------------------------------------------------
    def place(self, args):
        se, cam = self.se, self.cam
        sn = args.get("sn") or ([args["x"], args["y"], args["z"]] if "x" in args else None)
        if sn is None:
            raise ValueError("de.s3 place needs {\"sn\": [x, y, z]} (the block centre in Subnautica metres)")
        heading = float(args.get("yawDeg", args.get("headingDeg", self.a.s3a_heading)))
        if args.get("glyph") or self.a.s3a_glyph:
            if not self.glyph:
                cube = s3_cube(S3A_HALF, 0.0, 1.0)
                paths = write_glyph_files()
                self.glyph = {"cube": cube, "paths": paths, "modelKey": s3_model_key(S3A_GLYPH_MODEL_PATH),
                              "materialKey": s3_material_key(paths, S3A_DRAW_TECHNIQUE_MESH)}
        else:
            self.glyph = None          # the N1 glyph block only exists in the placement that asked for it
        if self.placed:
            self.queue.append(("C4", L.C4_TYPES["EntityRemove"], ST.entity_remove(self.grid_id), ("remove",)))
        m = self.se_matrix(sn, heading)
        self.placed = {"m": m, "sn": [float(v) for v in sn], "headingDeg": heading, "label": args.get("label", ""),
                       "tick": se.tick, "centreSe": m[3]}
        self.pose_logged = None
        self.enqueue_scene(clear=False)
        P, o, q, s, k = self.compute_pose()
        cs = self.corners(0)
        lo, hi = self.local_aabb()
        verts = sum(len(c["verts"]) for _pk, _mk, _mt, c, _l in self.parts())
        tris = sum(len(c["tris"]) for _pk, _mk, _mt, c, _l in self.parts())
        rep = {"gridId": "0x%016x" % self.grid_id, "entityId": self.grid_id, "labSn": self.placed["sn"],
               "site": {"C": list(cam.C), "Rs": cam.Rs}, "localMin": list(self.local_aabb()[0]),
               "localMax": list(self.local_aabb()[1]),
               "headingDeg": heading, "seMatrix": _rows12(m), "centreSe": list(m[3]), "pivot": list(P), "o": list(o),
               "q": list(q), "s": s, "k": k, "bounds": list(lo) + list(hi), "parts": len(self.parts()),
               "verts": verts, "tris": tris, "modelKey": "0x%016x" % self.model_key,
               "materialKey": "0x%016x" % self.material_key, "texturePaths": self.paths, "textureSet": self.texture_set,
               "dds": self.dds, "cornersLocal": [list(c[0]) for c in cs], "cornersSn": [list(c[2]) for c in cs],
               "cornersSnExact": [list(c[3]) for c in cs], "centreSn": list(cam.F(m[3])),
               "glyph": {"modelKey": "0x%016x" % self.glyph["modelKey"], "paths": self.glyph["paths"],
                         "partLocal": list(self.parts()[1][4])} if self.glyph else None,
               "faces": [{k2: (list(v) if isinstance(v, tuple) else v) for k2, v in f.items()} for f in self.cube["faces"]],
               "hsv": list(self.hsv), "label": self.placed["label"], "tick": se.tick, "mutant": self.a.s3_mutant}
        self.placed["reply"] = rep
        se.emit("s3.place", "DE.S3 place grid=0x%016x labSn=(%.3f, %.3f, %.3f) headingDeg=%g parts=%d verts=%d tris=%d "
                "s=%.9f textureSet=%s" % (self.grid_id, sn[0], sn[1], sn[2], heading, len(self.parts()), verts, tris, s,
                                          self.texture_set), **rep)
        for pk, mk, _mat, cube, _loc in self.parts():
            se.emit("s3.model", "DE.S3 model key=0x%016x verts=%d tris=%d (producer counts; S3.C1)" % (
                mk, len(cube["verts"]), len(cube["tris"])), modelKey="0x%016x" % mk, partKey="0x%016x" % pk,
                verts=len(cube["verts"]), tris=len(cube["tris"]), indexCount=3 * len(cube["tris"]),
                payloadBytes=len(s3_model_payload(cube, 0)))
        return rep

    def pixel(self, args, label):
        cam = self.cam
        if not self.placed:
            raise ValueError("no block placed")
        if "corner" in args:
            i = int(args["corner"])
            c = self.corners(int(args.get("part", 0)))[i]
            pse, psn, what = c[1], c[2], "corner %d" % i
        elif "sn" in args:
            psn = tuple(float(v) for v in args["sn"])
            pse, what = cam.Finv(psn), "sn point"
        else:
            pse, what = self.placed["centreSe"], "centre"
            psn = cam.F(pse)
        px = cam.project_se(pse)
        rep = {"label": label, "tick": self.se.tick, "what": what, "sePx": px["px"] if px else None,
               "depth": px["depth"] if px else None, "pointSe": list(pse), "pointSn": list(psn),
               "screen": [cam.W, cam.H], "vFovDeg": cam.fov, "camSe": list(cam.T), "camSn": list(cam.F(cam.T))}
        self.se.emit("s3.pixel", **rep)
        return rep

    def command(self, args, rid):
        args = args if isinstance(args, dict) else {}
        label = str(args.get("label", ""))
        try:
            if self.se.phase != L.SE_PHASE["InWorld"]:
                raise ValueError("not in world")
            if isinstance(args.get("place"), dict) or (args.get("place") is True and "sn" in args):
                p = args["place"] if isinstance(args.get("place"), dict) else args
                rep = self.place(dict(p, label=p.get("label", label)))
                return dict({"id": rid, "ok": True, "line": "de.s3 place"}, **rep)
            if isinstance(args.get("pixel"), dict):
                return dict({"id": rid, "ok": True, "line": "de.s3 pixel"}, **self.pixel(args["pixel"], label))
            if "gps" in args:
                on = str(args["gps"]).lower() in ("on", "true", "1")
                self.gps_on = on
                self.se.emit("s3.gpsMode", "DE.S3 gps %s" % ("on" if on else "off"), on=on, tick=self.se.tick)
                return {"id": rid, "ok": True, "line": "de.s3 gps %s" % ("on" if on else "off"), "tick": self.se.tick}
            if args.get("clear"):
                if self.placed:
                    self.queue.append(("C4", L.C4_TYPES["EntityRemove"], ST.entity_remove(self.grid_id),
                                       ("remove",)))
                    self.se.written_grids.discard(self.grid_id)
                self.placed = None
                self.se.emit("s3.clear", "DE.S3 clear grid=0x%016x" % self.grid_id, tick=self.se.tick)
                return {"id": rid, "ok": True, "line": "de.s3 clear", "tick": self.se.tick}
            st = {"placed": bool(self.placed), "queue": len(self.queue), "written": self.written, "full": self.full,
                  "gps": self.gps_on, "pose": [list(x) if isinstance(x, tuple) else x for x in self.pose] if self.pose
                  else None, "textureSet": self.texture_set, "dds": self.dds, "mutant": self.a.s3_mutant}
            if self.placed:
                st["place"] = self.placed.get("reply")
            return {"id": rid, "ok": True, "line": "de.s3 state", "state": st, "tick": self.se.tick}
        except (KeyError, TypeError, ValueError) as e:
            self.se.emit("s3.cmd", "DE.S3 de.s3 failed: %s" % e, error=str(e), args=args)
            return {"id": rid, "ok": False, "error": "de.s3: %s" % e}

# ======================================================================================================================
# S5 (ROADMAP S5 "tools/tests": the stream replayer / stand-in at scale). --s5 with --replay <name|file|dir> replays a
# recorded SE C3/C4 stream (and its C2/C1 when recorded) after the hello ack with the S5 budgets; --synth N streams a
# ship of N blocks built from SE's real models read in place (tests/standins/s5_stream.py Ship: instances, colours, three
# subgrids, dynamic subparts, a build-stage variant), poses it every tick in C2 (root entity + subgrid and dynamic subpart
# rows), serves de.s5 (spawn, count, parts, edit, rotor, piston, door, sub, fly, coh, loader, record, teardown, camera,
# status) and logs the SE-side S5 rows (s5.grid, s5.part, s5.pose, s5.edit, s5.sub, s5.model, s5.coh, s5.budget) in the
# schema of tests/s5/analyze_s5.py. Seeded failures at the producer: --s5-mutant M1 (C2 published one tick behind the
# camera: the previous tick's snapshot stamped now, the camera current; the contract's S5.M1 sits in Subnautica's pose
# sampler, this is the stand-in's equivalent) and M3 (PartRemove not written, counted as sent); `de.mutant` switches them.
# ======================================================================================================================

S5_COMMANDS = ("de.s5", "de.mutant")
S5_C3_BYTES_PER_S = 16 << 20                 # CONTRACT 5.4 "Budgets and back-pressure (S5)"
S5_C4_BYTES_PER_TICK = 512 << 10
S5_C4_FREE_MIN = 1 << 20                     # C4 free < 1 MiB: classes (1)-(2) only
S5_C3_FREE_MIN = 8 << 20                     # C3 free < 8 MiB: no new model starts
S5_NEAR_EDIT_M = 50.0
S5_DEFAULT_SPAWN_AHEAD_M = 30.0
S5_COH_POINT_NOTE = "cockpit dashboard point 1.1 m ahead of the seat"

def _s5_stream():
    import s5_stream   # tests/standins (stdlib)
    return s5_stream

class S5Scene:
    """The SE plugin's S5 producer duties in the stand-in (section comment above)."""

    def __init__(self, se, a):
        import wire_ref
        self.se, self.a, self.W = se, a, wire_ref
        self.X = _s5_stream()
        self.cam = se.cam
        self.mutants = set(m for m in (a.s5_mutant or []) if m)
        self.mode = "replay" if a.replay else "synth"
        self.ships = {}                      # size -> Ship (built before the link: no heartbeat stall later)
        self.ship = None
        self.size = None
        self.recording = None                # replay: (header, records)
        self.spawned = False
        self.m_root = None                   # SE world rows of the root grid
        self.sn_pos = None                   # SN point of the root origin, heading (fly)
        self.heading = 0.0
        self.fly = None
        self.cockpit_cam = False
        self.coh_on = False
        self.sub_on = False
        self.c3q = collections.deque()       # model groups: deque of [(type, payload, meta)]
        self.c3_open = None
        self.c4q = {1: collections.deque(), 2: collections.deque(), 3: collections.deque(), 4: collections.deque()}
        self.sent_models, self.sent_materials = set(), set()
        self.written = {"C3": 0, "C4": 0, "C3Bytes": 0, "C4Bytes": 0, "removeSkipped": 0}
        self.full = {"C3": 0, "C4": 0}
        self.backpressure = {"C3": 0, "C4": 0}
        self.sec_acc = {"c3Bytes": 0, "c4Bytes": 0, "c3Ms": 0.0, "c4Ms": 0.0, "c3MsMax": 0.0, "c4MsMax": 0.0}
        self.last_budget = time.monotonic()
        self.prev_c2 = None
        self.c2_rows = 0
        self.c2_dropped = 0
        self.pose_logged = None
        self.first_send_pending = False
        self.replay_i = 0
        self.replay_t0 = None
        self.replay_c2 = None                # last recorded C2 snapshot (bytes, packed)
        self.replay_c1 = None
        self.replay_grids = {}               # entityId -> GridAdd dict (replay counts, synthesised poses)
        self.replay_parts = {}               # entityId -> set(partKey)
        self.recorder = None
        self.recorder_opts = {}
        self.c2_buf = bytearray(L.C2_SUBPARTS_OFFSET + L.C2_MAX_SUBPARTS * 64)
        self.edit_serial = 0
        self.loaded = {}
        self.spawn_args = None
        if self.mode == "replay":
            path = self.X.resolve_recording(a.replay)
            hdr, recs, ended = self.X.read_derec(path)
            self.recording = {"path": path, "header": hdr, "records": recs, "ended": ended,
                              "problems": self.X.recording_problems(path)}
            se.emit("s5.replay", "DE.S5 replay %s: %d records (C1 %s, C2 %s, synthetic %s)%s" % (
                path, len(recs), hdr["hasC1"], hdr["hasC2"], hdr["synthetic"],
                "; problems: " + "; ".join(self.recording["problems"]) if self.recording["problems"] else ""),
                path=path, records=len(recs), header={k: (("0x%016x" % v) if k.endswith("Hash") else v)
                                                      for k, v in hdr.items()}, ended=ended,
                problems=self.recording["problems"])
        else:
            for n in (a.synth or [500]):
                t0 = time.perf_counter()
                ship = self.X.Ship(a.se_content, n, a.synth_seed,
                                   log=lambda ev, **kw: se.emit("s5.model", "DE.S5 model FAILED %s" % kw.get("asset"), **kw))
                self.ships[n] = ship
                se.emit("s5.synth", "DE.S5 synth %d blocks: %d grids, %d parts, %d models in %.1f s (read in place)" % (
                    n, len(ship.grids), len(ship.parts), len(ship.models), time.perf_counter() - t0), blocks=n,
                    grids=len(ship.grids), parts=len(ship.parts), models=len(ship.models), errors=ship.model_errors,
                    dims=list(ship.dims), seconds=round(time.perf_counter() - t0, 2))
            self.size = (a.synth or [500])[0]
            self.ship = self.ships[self.size]
        self.auto_spawn = tuple(a.s5_spawn_at) if a.s5_spawn_at else None

    # ---- helpers --------------------------------------------------------------------------------------------------
    def in_world(self):
        return self.se.phase == L.SE_PHASE["InWorld"]

    def level_matrix(self, sn_point, heading_deg):
        cam = self.cam
        T = cam.Finv(tuple(float(v) for v in sn_point))
        E, U, N = cam.site.frame_at(T)
        h = math.radians(heading_deg)
        fwd = _v_unit(_v_add(_v_mul(N, math.cos(h)), _v_mul(E, math.sin(h))))
        right = _v_cross(fwd, U)
        return (right, U, _v_mul(fwd, -1.0), T)

    def root_bounds(self):
        if self.ship:
            return self.ship.grid_bounds(self.X.SYNTH_ROOT_ID)
        return (-10.0, -10.0, -10.0, 10.0, 10.0, 10.0)

    def pose(self, m=None):
        m = m or self.m_root
        b = self.root_bounds()
        P = self.W.pivot_nearest(m, b[:3], b[3:], self.cam.T)
        o, q, s = self.W.wire_pose(self.cam.site, m, P)
        return P, o, q, s, 1.0

    def site(self):
        return {"C": list(self.cam.C), "Rs": self.cam.Rs}

    def hexk(self, v):
        return "0x%016x" % v

    # ---- queues ---------------------------------------------------------------------------------------------------
    def enqueue(self, items, cls):
        """items [(ring, type, payload, meta)] -> the C3 model groups and the C4 class queue `cls`."""
        group = None
        for ring, mtype, payload, meta in items:
            if ring == "C3":
                if mtype == L.C3_TYPES["ModelBegin"] or group is None:
                    if group:
                        self.c3q.append(group)
                    group = []
                group.append((mtype, payload, meta))
                if mtype in (L.C3_TYPES["ModelEnd"], L.C3_TYPES["MaterialDef"], L.C3_TYPES["SkinnedBind"]):
                    if mtype != L.C3_TYPES["ModelEnd"] and len(group) == 1:
                        self.c3q.append(group)       # a MaterialDef alone is its own group
                        group = None
                    elif mtype == L.C3_TYPES["ModelEnd"]:
                        self.c3q.append(group)
                        group = None
            else:
                self.c4q[cls].append((mtype, payload, meta))
        if group:
            self.c3q.append(group)

    def pending(self) -> bool:
        return bool(self.c3q or self.c3_open or any(self.c4q.values()))

    def clear_queues(self):
        self.c3q.clear()
        self.c3_open = None
        for q in self.c4q.values():
            q.clear()

    def flush(self):
        """Per tick: C4 by class within 1 ms-equivalent bytes (512 KiB) and back-pressure; C3 within 16 MiB/s, a model's
 messages never interleaved with another's; try-once writes (a full ring keeps the message for the next tick)."""
        se = self.se
        t0 = time.perf_counter()
        sent = 0
        c4_free_low = se.c4p.free() < S5_C4_FREE_MIN
        if c4_free_low:
            self.backpressure["C4"] += 1
        for cls in (1, 2, 3, 4):
            q = self.c4q[cls]
            if c4_free_low and cls > 2:
                break
            while q and sent < S5_C4_BYTES_PER_TICK:
                mtype, payload, meta = q[0]
                if mtype == L.C4_TYPES["PartRemove"] and "M3" in self.mutants:
                    q.popleft()                      # S5.M3: not written, counted as sent
                    self.written["removeSkipped"] += 1
                    self.after_write("C4", mtype, payload, meta, written=False)
                    continue
                st = se.c4p.try_write(mtype, payload)
                if st == S.RW_FULL:
                    self.full["C4"] += 1
                    break
                if st != S.RW_OK:
                    se._producer_corrupt(se.c4p)
                    return
                q.popleft()
                sent += len(payload) + 8
                self.after_write("C4", mtype, payload, meta)
            if q and (sent >= S5_C4_BYTES_PER_TICK):
                break
        t1 = time.perf_counter()
        # C3: a byte credit of 16 MiB/s (a chunk may overdraw it; the following ticks pay the debt back)
        budget = S5_C3_BYTES_PER_S / float(self.a.tick_hz)
        self.c3_credit = min(getattr(self, "c3_credit", 0.0) + budget, budget)
        c3 = 0
        c3_low = se.c3p.free() < S5_C3_FREE_MIN
        if c3_low:
            self.backpressure["C3"] += 1
        while self.c3_credit > 0:
            if self.c3_open is None:
                if not self.c3q or c3_low:
                    break
                self.c3_open = collections.deque(self.c3q.popleft())
            while self.c3_open and self.c3_credit > 0:
                mtype, payload, meta = self.c3_open[0]
                st = se.c3p.try_write(mtype, payload)
                if st == S.RW_FULL:
                    self.full["C3"] += 1
                    self.c3_credit = 0.0
                    break
                if st != S.RW_OK:
                    se._producer_corrupt(se.c3p)
                    return
                self.c3_open.popleft()
                c3 += len(payload) + 8
                self.c3_credit -= len(payload) + 8
                self.after_write("C3", mtype, payload, meta)
            if not self.c3_open:
                self.c3_open = None
        t2 = time.perf_counter()
        acc = self.sec_acc
        acc["c4Bytes"] += sent
        acc["c3Bytes"] += c3
        acc["c4Ms"] += (t1 - t0) * 1000.0
        acc["c3Ms"] += (t2 - t1) * 1000.0
        acc["c4MsMax"] = max(acc["c4MsMax"], (t1 - t0) * 1000.0)
        acc["c3MsMax"] = max(acc["c3MsMax"], (t2 - t1) * 1000.0)
        if self.first_send_pending and not self.pending():
            self.first_send_pending = False
            self.log_counts("spawned")

    def after_write(self, ring, mtype, payload, meta, written=True):
        se = self.se
        if written:
            self.written[ring] += 1
            self.written[ring + "Bytes"] += len(payload) + 8
            se.written[ring] += 1
            if self.recorder:
                self.recorder.message(ring, mtype, payload, se.tick, S.qpc())
        if ring == "C4":
            if mtype == L.C4_TYPES["GridAdd"]:
                gid = struct.unpack_from("<Q", payload, 0)[0]
                se.written_grids.add(gid)
            elif mtype == L.C4_TYPES["EntityRemove"]:
                se.written_grids.discard(struct.unpack_from("<Q", payload, 0)[0])
            elif mtype == L.C4_TYPES["ClearAll"]:
                se.written_grids.clear()
        elif mtype == L.C3_TYPES["ModelEnd"] and meta and self.ship:
            mk = meta[1]
            ex = self.ship.models.get(mk)
            if ex and mk not in self.loaded:
                self.loaded[mk] = True
                self.log_model(ex)

    def log_model(self, ex, label="sent"):
        self.se.emit("s5.model", "DE.S5 model key=%s path=%s verts=%d tris=%d loadMs=%.2f" % (
            self.hexk(ex["modelKey"]), ex["asset"], ex["verts"], ex["tris"], ex["loadMs"]),
            modelKey=self.hexk(ex["modelKey"]), path=ex["asset"], verts=ex["verts"], tris=ex["tris"],
            loadMs=ex["loadMs"], glass=ex["glass"], alphaMasked=ex["alphaMasked"], hasTangents=ex["hasTangents"],
            geometryAsset=ex["geometryAsset"], thread="standin", label=label,
            materials=[{"key": self.hexk(k), "technique": i["technique"], "flags": i["flags"]}
                       for k, _p, i in ex["materials"]])

    # ---- the first send, resends, teardown -------------------------------------------------------------------------
    def spawn(self, args):
        if self.mode != "synth":
            raise ValueError("spawn needs --synth (replay mode streams the recording)")
        size = args.get("ship", args.get("blocks"))
        if size is not None:
            size = int(size)
            if size not in self.ships:
                raise ValueError("ship %d not built (this stand-in has %s: --synth %s)" % (
                    size, sorted(self.ships), ",".join(str(x) for x in sorted(self.ships) + [size])))
        if self.spawned:
            self.teardown("respawn")
        if size is not None:
            self.size, self.ship = size, self.ships[size]
        sn = args.get("sn")
        heading = float(args.get("yawDeg", args.get("headingDeg", self.a.s5_heading)))
        if sn is None:     # ahead of the camera, level, at the camera's height
            cam = self.cam
            here = cam.F(cam.T)
            d = cam.mapdir(cam.T, cam.fwd)
            h = math.hypot(d[0], d[2]) or 1.0
            ahead = float(args.get("aheadM", S5_DEFAULT_SPAWN_AHEAD_M))
            sn = (here[0] + d[0] / h * ahead, here[1], here[2] + d[2] / h * ahead)
        self.sn_pos, self.heading = tuple(float(v) for v in sn), heading
        self.m_root = self.level_matrix(self.sn_pos, heading)
        self.spawned = True
        self.spawn_args = {"ship": self.size, "sn": list(self.sn_pos), "yawDeg": heading}
        self.sent_models.clear()
        self.sent_materials.clear()
        self.loaded.clear()
        X = self.X
        items = []
        for ring, mtype, payload, meta in self.ship.stream(self.se.world_epoch):
            if meta and meta[0] == "model":
                self.sent_models.add(meta[1])
            items.append((ring, mtype, payload, meta))
        self.sent_models |= set(self.ship.models)
        self.enqueue(items, 4)
        self.first_send_pending = True
        P, o, q, s, k = self.pose()
        rep = {"ship": self.size, "sn": list(self.sn_pos), "yawDeg": heading, "rootId": self.hexk(X.SYNTH_ROOT_ID),
               "grids": [self.hexk(g) for g in sorted(self.ship.grids)], "parts": len(self.ship.parts),
               "models": len(self.ship.models), "seMatrix": _rows12(self.m_root), "pivot": list(P), "o": list(o),
               "q": list(q), "s": s, "k": k, "site": self.site(), "tick": self.se.tick,
               "cockpitSeat": list(self.ship.cockpit_seat), "cohPoint": list(self.ship.cockpit_point),
               "mutants": sorted(self.mutants)}
        self.se.emit("s5.spawn", "DE.S5 spawn ship=%d sn=(%.2f, %.2f, %.2f) yawDeg=%g grids=%d parts=%d models=%d" % (
            self.size, self.sn_pos[0], self.sn_pos[1], self.sn_pos[2], heading, len(self.ship.grids),
            len(self.ship.parts), len(self.ship.models)), **rep)
        return rep

    def start_replay(self):
        """Replay: ClearAll with the current epochs, then the recorded C3/C4 in order (epochs rewritten)."""
        self.replay_i = 0
        self.replay_t0 = None
        self.replay_grids.clear()
        self.replay_parts.clear()
        self.clear_queues()
        self.enqueue([("C4", L.C4_TYPES["ClearAll"], ST.clear_all(self.se.world_epoch, self.se.link_epoch),
                       ("clear",))], 1)
        self.spawned = True
        self.first_send_pending = True

    def replay_tick(self):
        """Feeds the recorded messages whose tick offset is due (or all of them with --replay-pace budget)."""
        if not self.recording or self.replay_i >= len(self.recording["records"]):
            return
        recs = self.recording["records"]
        if self.replay_t0 is None:
            self.replay_t0 = (self.se.tick, recs[0][1] if recs else 0)
        base_tick, first = self.replay_t0
        due = self.se.tick - base_tick + first
        n = 0
        X = self.X
        while self.replay_i < len(recs) and n < 4096:
            kind, tick, _qpc, blob = recs[self.replay_i]
            if self.a.replay_pace == "recorded" and tick > due:
                break
            if self.a.replay_pace == "budget" and len(self.c4q[4]) + len(self.c3q) > 20000:
                break
            self.replay_i += 1
            n += 1
            if kind in (X.KIND_C3, X.KIND_C4):
                mtype, payload = X.unframe(blob)
                payload = self.rewrite(kind, mtype, payload)
                if kind == X.KIND_C3:
                    meta = ("model", struct.unpack_from("<Q", payload, 0)[0]) if mtype == L.C3_TYPES["ModelEnd"] else None
                    self.enqueue([("C3", mtype, payload, meta)], 4)
                else:
                    self.c4q[4].append((mtype, payload, None))
            elif kind == X.KIND_C2:
                self.replay_c2 = blob
            elif kind == X.KIND_C1:
                self.replay_c1 = blob
            elif kind == X.KIND_MARKER:
                try:
                    obj = json.loads(blob.decode("utf-8"))
                except (ValueError, UnicodeDecodeError):
                    obj = {"raw": len(blob)}
                self.se.emit("s5.marker", "DE.S5 replay marker %s" % json.dumps(obj)[:200], tick=self.se.tick,
                             recordedTick=tick, marker=obj)
        if self.replay_i >= len(recs):
            self.se.emit("s5.replayDone", "DE.S5 replay fed every record (%d)" % len(recs), tick=self.se.tick,
                         records=len(recs))

    def rewrite(self, kind, mtype, payload):
        """Epoch fields to the current ones (the consumer's epoch filter) and the bookkeeping of grids and parts."""
        X = self.X
        if kind != X.KIND_C4:
            return payload
        R = L.RECORDS
        if mtype == L.C4_TYPES["GridAdd"]:
            v = R["GridAdd"].unpack(payload)
            v["worldEpoch"] = self.se.world_epoch
            self.replay_grids[v["entityId"]] = v
            self.replay_parts.setdefault(v["entityId"], set())
            return R["GridAdd"].pack(**v)
        if mtype == L.C4_TYPES["ClearAll"]:
            self.replay_grids.clear()
            self.replay_parts.clear()
            return ST.clear_all(self.se.world_epoch, self.se.link_epoch)
        if mtype == L.C4_TYPES["EntityRemove"]:
            gid = struct.unpack_from("<Q", payload, 0)[0]
            self.replay_grids.pop(gid, None)
            self.replay_parts.pop(gid, None)
        elif mtype == L.C4_TYPES["PartAdd"]:
            gid, pk = struct.unpack_from("<QQ", payload, 0)
            self.replay_parts.setdefault(gid, set()).add(pk)
        elif mtype == L.C4_TYPES["PartRemove"]:
            gid, pk = struct.unpack_from("<QQ", payload, 0)
            self.replay_parts.get(gid, set()).discard(pk)
        return payload

    def resend(self, reason):
        """A new Subnautica generation or a link-epoch resend: ClearAll, then everything again (CONTRACT 6)."""
        self.clear_queues()
        if self.mode == "replay":
            if self.spawned:
                self.start_replay()
        else:
            self.enqueue([("C4", L.C4_TYPES["ClearAll"], ST.clear_all(self.se.world_epoch, self.se.link_epoch),
                           ("clear",))], 1)
            if self.spawned:
                self.spawned = False
                args = dict(self.spawn_args or {})
                self.spawn(args)
        self.se.emit("s5.resend", "DE.S5 resend (%s)" % reason, reason=reason, tick=self.se.tick)

    def teardown(self, why="teardown"):
        X = self.X
        if self.mode == "synth" and self.ship:
            items = [("C4", L.C4_TYPES["EntityRemove"], ST.entity_remove(g), ("remove", g))
                     for g in sorted(self.ship.grids, reverse=True)]
        else:
            items = [("C4", L.C4_TYPES["ClearAll"], ST.clear_all(self.se.world_epoch, self.se.link_epoch), ("clear",))]
            self.replay_i = len(self.recording["records"]) if self.recording else 0
        self.clear_queues()
        self.enqueue(items, 1)
        self.spawned = False
        self.fly = None
        self.cockpit_cam = False
        self.cam.camera_mode = None
        self.se.emit("s5.teardown", "DE.S5 teardown (%s)" % why, tick=self.se.tick, why=why)
        if self.mode == "synth" and self.ship:    # a fresh ship of the same size for the next spawn
            n = self.size
            self.ships[n] = self.X.Ship(self.a.se_content, n, self.a.synth_seed)
            self.ship = self.ships[n]

    # ---- per tick ---------------------------------------------------------------------------------------------------
    def tick(self):
        se = self.se
        if not (se.hello_acked and self.in_world()) or se.waiting_link_ack:
            return
        if self.auto_spawn is not None and not self.spawned:
            if self.mode == "synth":
                self.spawn({"sn": list(self.auto_spawn), "yawDeg": self.a.s5_heading})
            self.auto_spawn = None
        if self.mode == "replay" and not self.spawned and self.recording and self.a.replay_autostart:
            self.start_replay()
        if self.mode == "replay" and self.spawned:
            self.replay_tick()
        if self.ship and self.spawned:
            self.ship.advance(1.0 / self.a.tick_hz)
            self.advance_fly()
        if getattr(self, "coh_until", None) is not None and se.tick >= self.coh_until:
            self.coh_on, self.coh_until = False, None
        if getattr(self, "sub_until", None) is not None and se.tick >= self.sub_until:
            self.sub_on, self.sub_until = False, None
        self.flush()
        now = time.monotonic()
        if now - self.last_budget >= 1.0:
            self.last_budget = now
            acc = self.sec_acc
            se.emit("s5.budget", tick=se.tick, c3Bytes=acc["c3Bytes"], c4Bytes=acc["c4Bytes"],
                    c3Ms=round(acc["c3Ms"], 3), c4Ms=round(acc["c4Ms"], 3), c3MsMax=round(acc["c3MsMax"], 3),
                    c4MsMax=round(acc["c4MsMax"], 3), c3Queue=len(self.c3q) + (1 if self.c3_open else 0),
                    c4Queue={str(k): len(v) for k, v in self.c4q.items()}, backPressure=dict(self.backpressure),
                    full=dict(self.full), c2Rows=self.c2_rows, c2Dropped=self.c2_dropped,
                    entities=len(se.written_grids), models=len(self.loaded), written=dict(self.written),
                    c3Free=se.c3p.free(), c4Free=se.c4p.free(), mutants=sorted(self.mutants))
            for k in acc:
                acc[k] = 0 if isinstance(acc[k], int) else 0.0

    def advance_fly(self):
        f = self.fly
        if f is None and not self.cockpit_cam:
            return
        tick = self.se.tick
        if f is not None:
            k = tick - f["start"]
            if k >= f["n"]:
                self.se.emit("s5.fly", "DE.S5 fly done", tick=tick, phase="done")
                self.fly = None
            else:
                t = k / float(self.a.tick_hz)
                self.heading = f["h0"] + f["amp"] * math.sin(2.0 * math.pi * f["hz"] * t)
                h = math.radians(self.heading)
                dt = 1.0 / float(self.a.tick_hz)
                p = self.sn_pos
                self.sn_pos = (p[0] + f["v"] * dt * math.sin(h), p[1], p[2] + f["v"] * dt * math.cos(h))
                self.m_root = self.level_matrix(self.sn_pos, self.heading)
        if self.cockpit_cam and self.ship:
            m = self.m_root
            cam = self.cam
            cam.path = None
            cam.motion = None
            cam.T = self.X.m_point(m, self.ship.cockpit_seat)
            cam.fwd = _v_mul(m[2], -1.0)
            cam.up = m[1]
            cam.camera_mode = L.CONSTS["CameraMode"]["Cockpit"]

    def write_c2(self):
        """C2 every tick before C1 (same tick): the root grid's entity record and the subpart rows (subgrids, then
 dynamic subparts). Replay: the recorded snapshot re-stamped, or synthesised poses without one. S5.M1 (producer):
 the previous tick's snapshot, stamped now."""
        se = self.se
        buf = self.c2_buf
        ents, subs = [], []
        if self.in_world() and self.spawned:
            if self.mode == "synth" and self.ship and self.m_root:
                P, o, q, s, k = self.pose()
                X = self.X
                ents.append(L.RECORDS["SeEntity"].pack(
                    entityId=X.SYNTH_ROOT_ID, kind=L.CONSTS["SeEntityKind"]["RootGrid"], flags=0,
                    groupId=X.SYNTH_ROOT_ID, o=o, q=q, s=s, k=k, v=(0.0, 0.0, 0.0), omega=(0.0, 0.0, 0.0),
                    partsVersion=len(self.ship.parts) & 0xFFFFFFFF, skinVersion=0))
                for owner, key, local in self.ship.c2_rows():
                    subs.append(L.RECORDS["SeSubpart"].pack(ownerId=owner, partKey=key, local=X.m_rows12(local)))
                self.log_pose(P, o, q, s, k)
            elif self.mode == "replay":
                ents, subs = self.replay_c2_rows()
        if len(subs) > L.C2_MAX_SUBPARTS:
            self.c2_dropped += len(subs) - L.C2_MAX_SUBPARTS
            subs = subs[:L.C2_MAX_SUBPARTS]
        cur = (ents, subs)
        if "M1" in self.mutants:
            pub, self.prev_c2 = (self.prev_c2 or cur), cur
        else:
            pub, self.prev_c2 = cur, cur
        ents, subs = pub
        self.c2_rows = len(subs)
        hdr = L.RECORDS["SeEntitiesHeader"].pack(entityCount=len(ents), tick=se.tick, worldEpoch=se.world_epoch,
                                                 subpartCount=len(subs), lightCount=0)
        end = L.C2_ENTITIES_OFFSET + 96 * len(ents)
        buf[0:64] = hdr
        buf[64:end] = b"".join(ents)
        if subs:
            buf[end:L.C2_SUBPARTS_OFFSET] = bytes(L.C2_SUBPARTS_OFFSET - end)
            s_end = L.C2_SUBPARTS_OFFSET + 64 * len(subs)
            buf[L.C2_SUBPARTS_OFFSET:s_end] = b"".join(subs)
        else:
            s_end = end
        S.seqlock_write(se.sec, L.REGION["C2"].offset, bytes(buf[:s_end]))
        if self.recorder and self.recorder_opts.get("c2"):
            self.recorder.add(self.X.KIND_C2, se.tick, hdr + b"".join(ents) + b"".join(subs), S.qpc())

    def replay_c2_rows(self):
        X = self.X
        if self.replay_c2:
            blob = self.replay_c2
            h = L.RECORDS["SeEntitiesHeader"].unpack(blob)
            ne, ns = h["entityCount"], h["subpartCount"]
            ents = [blob[64 + 96 * i:64 + 96 * (i + 1)] for i in range(ne)]
            off = 64 + 96 * ne
            subs = [blob[off + 64 * i:off + 64 * (i + 1)] for i in range(ns)]
            return ents, subs
        # no recorded C2: the root grids at the spawn point (identity orientation), subgrids at identity
        ents = []
        if self.m_root is None:
            sn = self.auto_spawn or (0.0, -20.0, 30.0)
            self.m_root = self.level_matrix(sn, self.a.s5_heading)
        for gid, g in sorted(self.replay_grids.items()):
            if g["parentEntityId"]:
                continue
            P = self.m_root[3]
            o, q, s = self.W.wire_pose(self.cam.site, self.m_root, P)
            ents.append(L.RECORDS["SeEntity"].pack(entityId=gid, kind=L.CONSTS["SeEntityKind"]["RootGrid"], flags=1,
                                                   groupId=gid, o=o, q=q, s=s, k=1.0))
        subs = [L.RECORDS["SeSubpart"].pack(ownerId=g["parentEntityId"], partKey=gid, local=X.m_rows12(X.IDENTITY))
                for gid, g in sorted(self.replay_grids.items()) if g["parentEntityId"]]
        return ents[:L.C2_MAX_ENTITIES], subs

    def c1_override(self):
        """Replay with recorded C1: the recorded record re-stamped (tick, worldEpoch, qpcCapture); else None."""
        if self.mode != "replay" or not self.replay_c1 or not self.spawned:
            return None
        rec = bytearray(self.replay_c1)
        struct.pack_into("<Q", rec, 0x08, self.se.tick)
        struct.pack_into("<q", rec, 0x10, S.qpc())
        struct.pack_into("<I", rec, 0x24, self.se.world_epoch)
        return bytes(rec)

    def after_c1(self):
        se = self.se
        if self.recorder and self.recorder_opts.get("c1"):
            n = struct.unpack_from("<I", se.sec.read(L.REGION["C1"].offset + 0x80, 4), 0)[0]
            blob = se.sec.read(L.REGION["C1"].offset, 0x100 + 48 * min(n, 160))
            self.recorder.add(self.X.KIND_C1, se.tick, blob, S.qpc())
        if not (self.in_world() and self.spawned and self.ship and self.m_root):
            return
        if self.coh_on:
            X = self.X
            pt = X.m_point(self.m_root, self.ship.cockpit_point)
            px = self.cam.project_se(pt)
            se.emit("s5.coh", tick=se.tick, qpc=self.cam.last_qpc or S.qpc(), px=px["px"] if px else None,
                    depth=px["depth"] if px else None, point=list(pt), pointLocal=list(self.ship.cockpit_point),
                    entityId=self.hexk(X.SYNTH_ROOT_ID), cockpit=self.cockpit_cam)
        if self.sub_on:
            self.log_sub()

    def log_pose(self, P, o, q, s, k):
        key = tuple(round(x, 9) for x in tuple(P) + tuple(o) + tuple(q) + (s,))
        if key == self.pose_logged:
            return
        self.pose_logged = key
        X = self.X
        self.se.emit("s5.pose", tick=self.se.tick, entityId=self.hexk(X.SYNTH_ROOT_ID), seMatrix=_rows12(self.m_root),
                     pivot=list(P), k=k, site=self.site(), o=list(o), q=list(q), s=s)

    def log_sub(self):
        X = self.X
        ship = self.ship
        P, o, q, s, k = self.pose()
        base = {"tick": self.se.tick, "rootId": self.hexk(X.SYNTH_ROOT_ID), "seMatrix": _rows12(self.m_root),
                "pivot": list(P), "k": k, "site": self.site()}
        kinds = {X.SYNTH_ROTOR_ID: "rotor", X.SYNTH_PISTON_ID: "piston", X.SYNTH_DOCK_ID: "subgrid"}
        for owner, key, local in ship.c2_rows():
            if key in ship.grids:
                kind = kinds.get(key, "subgrid")
                bounds = ship.grid_bounds(key)
                to_root = local
            else:
                p = ship.parts.get((owner, key))
                kind = "door" if p and p.role.startswith("door") else ("piston" if p and p.role.startswith("piston")
                                                                       else "subpart")
                bounds = ship.model_bounds(p.model) if p else None
                to_root = local if owner == X.SYNTH_ROOT_ID else X.m_mul(local, ship.rel(owner))
            self.se.emit("s5.sub", ownerId=self.hexk(owner), partKey=self.hexk(key), kind=kind, local=X.m_rows12(local),
                         toRoot=X.m_rows12(to_root), bounds=list(bounds) if bounds else None, **base)

    def log_counts(self, label):
        X = self.X
        out = []
        if self.mode == "synth" and self.ship:
            for gid in sorted(self.ship.grids):
                g = self.ship.grids[gid]
                c = self.ship.counts(gid)
                row = {"entityId": self.hexk(gid), "rootId": self.hexk(X.SYNTH_ROOT_ID),
                       "parentId": self.hexk(g["parent"]), "targetClass": g["cls"], "flags": g["flags"],
                       "parts": c["parts"], "verts": c["verts"], "tris": c["tris"], "byClass": c["byClass"]}
                out.append(row)
        else:
            for gid, g in sorted(self.replay_grids.items()):
                out.append({"entityId": self.hexk(gid), "parentId": self.hexk(g["parentEntityId"]),
                            "targetClass": g["targetClass"], "flags": g["flags"],
                            "parts": len(self.replay_parts.get(gid, ()))})
        for row in out:
            self.se.emit("s5.grid", "DE.S5 grid %s parts=%d" % (row["entityId"], row["parts"]), tick=self.se.tick,
                         label=label, **row)
        return out

    # ---- commands ---------------------------------------------------------------------------------------------------
    def command(self, cmd, args, rid):
        args = args if isinstance(args, dict) else {}
        se = self.se
        try:
            if cmd == "de.mutant":
                return self.mutant_command(args, rid)
            if not self.in_world():
                raise ValueError("not in world")
            if isinstance(args.get("record"), (dict, str)) or args.get("action") == "record":
                return dict({"id": rid, "ok": True}, **self.record_command(args))
            args = self.translate(args)
            act = str(args.get("action", "status"))
            label = str(args.get("label", ""))
            if act == "status":
                return {"id": rid, "ok": True, "state": self.status(), "tick": se.tick}
            if act == "spawn":
                return dict({"id": rid, "ok": True, "line": "de.s5 spawn"}, **self.spawn(args))
            if act == "teardown":
                self.teardown(label or "command")
                return {"id": rid, "ok": True, "line": "de.s5 teardown", "tick": se.tick}
            if act == "count":
                rows = self.log_counts(label or "count")
                return {"id": rid, "ok": True, "line": "de.s5 count", "grids": rows, "tick": se.tick,
                        "pending": self.pending()}
            if act == "parts":
                return dict({"id": rid, "ok": True}, **self.parts_command(args))
            if act == "edit":
                return dict({"id": rid, "ok": True}, **self.edit_command(args))
            if act == "loader":
                return dict({"id": rid, "ok": True}, **self.loader_command(args))
            if act == "noop":
                return {"id": rid, "ok": True, "line": "de.s5 %s (the stand-in has no character; nothing to set)" %
                        args.get("what"), "tick": se.tick}
            self.need_ship()
            if act == "rotor":
                self.ship.rotor_rpm = float(args.get("rpm", self.X.ROTOR_RPM))
            elif act == "piston":
                self.ship.piston_target = max(0.0, min(self.X.PISTON_STROKE_M, float(args.get("extendM", args.get(
                    "target", self.X.PISTON_STROKE_M)))))
            elif act == "door":
                self.ship.door_target = 1.0 if str(args.get("open", True)).lower() in ("1", "true", "on") else 0.0
            elif act == "sub":
                self.sub_on = str(args.get("on", True)).lower() in ("1", "true", "on")
                self.sub_until = se.tick + int(float(args["seconds"]) * self.a.tick_hz) if args.get("seconds") else None
            elif act == "coh":
                self.coh_on = str(args.get("on", True)).lower() in ("1", "true", "on")
                self.coh_until = se.tick + int(float(args["seconds"]) * self.a.tick_hz) if args.get("seconds") else None

            elif act == "fly":
                self.fly = {"start": se.tick, "n": max(1, int(round(float(args.get("seconds", 10.0)) * self.a.tick_hz))),
                            "v": float(args.get("speedMps", 20.0)), "amp": float(args.get("yawAmpDeg", 10.0)),
                            "hz": float(args.get("yawHz", 0.5)), "h0": self.heading}
                self.cockpit_cam = bool(args.get("cockpit", True))
                self.advance_fly()
            elif act == "cockpit":
                self.cockpit_cam = str(args.get("on", True)).lower() in ("1", "true", "on")
                if not self.cockpit_cam:
                    self.cam.camera_mode = None
                self.advance_fly()
            elif act == "camera":
                mode = str(args.get("mode", "third")).lower()
                CM = L.CONSTS["CameraMode"]
                self.cam.camera_mode = CM.get("FirstPerson", 1) if mode.startswith("first") else CM.get("ThirdPerson", 2)
                return {"id": rid, "ok": True, "line": "de.s5 camera %s (the stand-in has no character)" % mode,
                        "tick": se.tick}
            else:
                raise ValueError("unknown de.s5 action %s" % act)
            se.emit("s5.cmd", "DE.S5 %s %s" % (act, json.dumps(args)[:200]), action=act, args=args, tick=se.tick)
            return {"id": rid, "ok": True, "line": "de.s5 %s" % act, "tick": se.tick}
        except (KeyError, TypeError, ValueError, OSError) as e:
            se.emit("s5.cmd", "DE.S5 %s failed: %s" % (cmd, e), error=str(e), args=args)
            return {"id": rid, "ok": False, "error": "%s: %s" % (cmd, e)}

    def translate(self, args):
        """The SE plugin's de.s5 syntax (S5Commands: status, set, export, models, coh, trace, character, record) onto
 the stand-in's actions, so the launcher's real commands drive the stand-in too."""
        if "action" in args:
            return args
        if "export" in args:
            return {"action": "parts", "n": args.get("sample", 20), "seed": args.get("seed", 1), "entity": args["export"]}
        if "models" in args:
            m = args["models"] if isinstance(args["models"], dict) else {}
            return {"action": "loader", "n": m.get("count", args.get("count", 200)), "send": m.get("send", True)}
        if "coh" in args:
            c = args["coh"] if isinstance(args["coh"], dict) else {}
            return {"action": "coh", "on": True, "seconds": c.get("seconds", 10.0), "point": c.get("point")}
        if "trace" in args:
            return {"action": "sub", "on": True, "seconds": args.get("trace") or 10.0}
        if "character" in args or "set" in args:
            return {"action": "noop", "what": "character" if "character" in args else "set"}
        return {"action": "status"}

    def need_ship(self):
        if not (self.mode == "synth" and self.ship and self.spawned):
            raise ValueError("no synthetic ship spawned (de.s5 {action: spawn})")

    def status(self):
        st = {"mode": self.mode, "spawned": self.spawned, "ship": self.size, "queues": {
            "c3": len(self.c3q) + (1 if self.c3_open else 0), "c4": {str(k): len(v) for k, v in self.c4q.items()}},
            "pending": self.pending(), "written": dict(self.written), "full": dict(self.full),
            "mutants": sorted(self.mutants), "recording": bool(self.recorder), "built": sorted(self.ships)}
        if self.ship:
            st.update(grids=len(self.ship.grids), parts=len(self.ship.parts), models=len(self.ship.models),
                      rotorRpm=self.ship.rotor_rpm, pistonExt=self.ship.piston_ext, doorOpen=self.ship.door_open)
        if self.recording:
            st.update(replay={"path": self.recording["path"], "fed": self.replay_i,
                              "records": len(self.recording["records"])})
        return st

    def mutant_command(self, args, rid):
        mid = str(args.get("name", args.get("id", args.get("mutant", "")))).upper().replace("S5.", "")
        on = args.get("on", args.get("state", True))
        on = str(on).lower() in ("1", "true", "on")
        if mid not in ("M1", "M3"):
            return {"id": rid, "ok": False, "error": "de.mutant: the stand-in owns S5.M1 and S5.M3 only (got %s)" % mid}
        (self.mutants.add if on else self.mutants.discard)(mid)
        self.se.emit("s5.mutant", "DE.S5 MUTANT S5.%s %s" % (mid, "on" if on else "off"), mutant="S5." + mid, on=on,
                     tick=self.se.tick)
        return {"id": rid, "ok": True, "line": "de.mutant S5.%s %s" % (mid, "on" if on else "off"),
                "mutants": sorted(self.mutants)}

    def parts_command(self, args):
        """S5.N1: n random parts logged as s5.part with their rows, the root's matrix and P* (the reply lists them)."""
        self.need_ship()
        X = self.X
        n, seed = int(args.get("n", 20)), int(args.get("seed", 0))
        P, o, q, s, k = self.pose()
        out = []
        for gid, key in self.ship.random_parts(n, seed):
            p = self.ship.parts[(gid, key)]
            local = self.ship.subpart_local(p) if p.flags & X.PF_DYNAMIC else p.local
            rel = self.ship.rel(gid)
            row = {"entityId": self.hexk(gid), "rootId": self.hexk(X.SYNTH_ROOT_ID), "partKey": self.hexk(key),
                   "modelKey": self.hexk(p.model), "cls": p.cls, "flags": p.flags, "local": X.m_rows12(local),
                   "rel": X.m_rows12(rel), "toRoot": X.m_rows12(self.ship.to_root(p)), "seMatrix": _rows12(self.m_root),
                   "pivot": list(P), "k": k, "site": self.site(), "bounds": list(self.ship.model_bounds(p.model)),
                   "wire": {"o": list(o), "q": list(q), "s": s}}
            self.se.emit("s5.part", tick=self.se.tick, **row)
            out.append({"entityId": row["entityId"], "partKey": row["partKey"]})
        return {"line": "de.s5 parts", "parts": out, "tick": self.se.tick}

    def edit_command(self, args):
        """S5.N2: one SE event (weld, stage, damage, paint, grind) -> its records first in C4 (class 2 within 50 m)."""
        self.need_ship()
        X = self.X
        kind = str(args.get("kind", "paint"))
        if kind not in ("weld", "stage", "damage", "paint", "grind"):
            raise ValueError("edit kind weld|stage|damage|paint|grind")
        res = self.ship.edit(kind, self.sent_models)
        near = math.dist(self.cam.T, self.m_root[3]) <= S5_NEAR_EDIT_M + 1e-9
        self.enqueue(res["records"], 2 if near else 3)
        self.edit_serial += 1
        c = self.ship.counts(X.SYNTH_ROOT_ID)
        recs = {}
        for _r, mtype, _p, _m in res["records"]:
            name = next((n for n, v in list(L.C4_TYPES.items()) + list(L.C3_TYPES.items()) if v == mtype), str(mtype))
            recs[name] = recs.get(name, 0) + 1
        row = {"edit": kind, "editId": self.edit_serial, "entityId": self.hexk(X.SYNTH_ROOT_ID),
               "partKeys": [self.hexk(k) for k in res["keys"]], "records": recs, "partsAfter": c["parts"],
               "trisAfter": c["tris"], "near": near, "mutants": sorted(self.mutants)}
        self.se.emit("s5.edit", "DE.S5 edit %s #%d keys=%d" % (kind, self.edit_serial, len(res["keys"])),
                     tick=self.se.tick, **row)
        return dict(row, line="de.s5 edit %s" % kind, tick=self.se.tick)

    def loader_command(self, args):
        """S5.N5 in the stand-in: n distinct models read in place (the SE plugin loads them on DE.ModelLoader)."""
        X = self.X
        n = int(args.get("n", 200))
        assets = list(X.armor_catalogue(self.a.se_content)) + sorted(X.FAT_MODELS.values()) + list(X.SMALL_ARMOR)
        done, errors = 0, 0
        send = str(args.get("send", False)).lower() in ("1", "true", "on")
        group = []
        for asset in assets[:n]:
            try:
                ex = X._EXPORT_CACHE.get((self.a.se_content, asset, 1 << 20)) or X.export_model(self.a.se_content, asset)
                self.log_model(ex, label="loader")
                done += 1
                if send and ex["modelKey"] not in self.sent_models:
                    self.sent_models.add(ex["modelKey"])
                    for key, payload, _i in ex["materials"]:
                        group.append(("C3", L.C3_TYPES["MaterialDef"], payload, ("material", key)))
                    for t, payload in ex["messages"]:
                        group.append(("C3", t, payload, ("model", ex["modelKey"]) if t == L.C3_TYPES["ModelEnd"] else None))
            except (OSError, ValueError, struct.error, IndexError) as e:
                errors += 1
                self.se.emit("s5.model", "DE.S5 model %s exception %s" % (asset, e), path=asset,
                             exception="%s: %s" % (type(e).__name__, e), label="loader", thread="standin")
        if group:
            self.enqueue(group, 4)
        return {"line": "de.s5 loader", "loaded": done, "exceptions": errors, "sent": bool(group), "tick": self.se.tick}

    def record_command(self, args):
        """de.s5 {record: {name, c1, c2}} / {record: "stop"} (the SE plugin's dev-build recorder; the stand-in records
 what it writes, in the same format)."""
        X = self.X
        r = args.get("record")
        if r == "stop" or (isinstance(r, dict) and r.get("stop")) or args.get("stop"):
            if not self.recorder:
                raise ValueError("not recording")
            side = self.recorder.close(self.recorder_opts.get("name"))
            self.recorder = None
            self.se.emit("s5.record", "DE.S5 record stop %s" % side.get("name"), phase="stop", sidecar=side,
                         tick=self.se.tick)
            return {"line": "de.s5 record stop", "sidecar": side}
        opts = r if isinstance(r, dict) else {k: args.get(k) for k in ("name", "c1", "c2")}
        name = str(opts.get("name") or "standin-%s" % time.strftime("%Y%m%d-%H%M%S"))
        if self.recorder:
            raise ValueError("already recording")
        c1, c2 = bool(opts.get("c1", True)), bool(opts.get("c2", True))
        self.recorder = X.DerecWriter(X.recording_path(name), synthetic=self.mode == "synth", se_version=0,
                                      world_epoch=self.se.world_epoch, has_c1=c1, has_c2=c2)
        self.recorder_opts = {"name": name, "c1": c1, "c2": c2}
        self.recorder.marker({"ev": "record", "name": name, "standin": True, "mode": self.mode, "ship": self.size},
                             self.se.tick)
        # the scene so far, so the recording replays on its own (the SE plugin starts a recording with a resend)
        self.resend("record start")
        self.se.emit("s5.record", "DE.S5 record start %s" % name, phase="start", name=name, c1=c1, c2=c2,
                     path=self.recorder.path, tick=self.se.tick)
        return {"line": "de.s5 record %s" % name, "path": self.recorder.path}

    def summary(self):
        if self.recorder:
            self.recorder.close(self.recorder_opts.get("name"))
            self.recorder = None
        self.se.emit("s5.summary", "DE.S5 stand-in summary: mode=%s spawned=%s written C3 %d / C4 %d, full C3 %d / C4 "
                     "%d, removeSkipped %d" % (self.mode, self.spawned, self.written["C3"], self.written["C4"],
                                               self.full["C3"], self.full["C4"], self.written["removeSkipped"]),
                     written=self.written, full=self.full, backPressure=self.backpressure)

def _rows12(m):
    return [c for row in m for c in row]

# ----------------------------------------------------------------------------------------------------------------
# S6 (ROADMAP S6 "tools/tests": the B-T1 scene without SE). See the module docstring S6.
# ----------------------------------------------------------------------------------------------------------------

S6_COMMANDS = ("de.s6",)
S6_C4_BYTES_PER_TICK = 512 << 10
S6_C3_BYTES_PER_TICK = (16 << 20) // 60

def _s6_stream():
    import s6_stream   # tests/standins (stdlib)
    return s6_stream

def _s6_id(v) -> int:
    if isinstance(v, int):
        return v & 0xFFFFFFFFFFFFFFFF
    s = str(v).strip()
    return (int(s, 16) if s.lower().startswith("0x") else int(s)) & 0xFFFFFFFFFFFFFFFF

class S6Scene:
    """The SE plugin's S6 producer duties in the stand-in: a synthetic hull (tests/standins/s6_stream.py Hull) as a
 mirrored grid (C3 box models + MaterialDef, C4 GridAdd/PartAdd, C2 root pose and subgrid rows every tick) with its
 collision skin as C4 type 6 SkinSet (version 1 after the GridAdd, +1 per resend, back to 1 after a ClearAll), a loop
 motion (a circle of radiusM at speedMps, Forward along the path), and the C8 drain as s6.c8 rows. Commands (de.s6):
 {action: status | spawn {skin, sn, yawDeg, speedMps, radiusM, startDeg} | teardown | motion {speedMps, radiusM} |
 pose}, and SE's own syntax {status: true} | {skin: "<id>"|"all", rows} | {resend: "<id>"|"all"}; {damage} and
 {selftest} answer that the stand-in has no damage consumer (S6se runs the real SE)."""

    def __init__(self, se, a):
        import wire_ref
        self.se, self.a, self.W = se, a, wire_ref
        self.X = _s6_stream()
        self.cam = se.cam
        self.kind = (a.skin or "synth:bt1").replace("synth:", "")
        self.hull = None
        self.spawned = False
        self.spawn_args = None
        self.sn_centre = None
        self.heading = 0.0
        self.speed = 0.0
        self.radius = 20.0
        self.theta0 = 0.0
        self.t0_tick = 0
        self.m_root = None
        self.versions = {}                   # entityId -> last SkinSet version sent in this stream
        self.c3q = collections.deque()
        self.c4q = collections.deque()
        self.sent_models = set()
        self.written = {"C3": 0, "C4": 0, "C3Bytes": 0, "C4Bytes": 0, "skins": 0}
        self.full = {"C3": 0, "C4": 0}
        self.c8 = {"records": 0, "byType": {}, "byKind": {}, "duplicates": 0}
        self.dedup = self.X.Dedup()
        self.pose_logged = None
        self.last_stats = time.monotonic()
        try:
            self.textures = s3_texture_paths(a.se_content, "large_edges")
        except OSError:
            self.textures = ["", "", "", ""]
        self.material_key = s3_material_key(self.textures, S3A_DRAW_TECHNIQUE_MESH)
        self.auto_spawn = tuple(a.skin_spawn_at) if a.skin_spawn_at else None

    # ---- helpers --------------------------------------------------------------------------------------------------
    def in_world(self):
        return self.se.phase == L.SE_PHASE["InWorld"]

    def hexk(self, v):
        return "0x%016x" % v

    def level_matrix(self, sn_point, heading_deg):
        cam = self.cam
        T = cam.Finv(tuple(float(v) for v in sn_point))
        E, U, N = cam.site.frame_at(T)
        h = math.radians(heading_deg)
        fwd = _v_unit(_v_add(_v_mul(N, math.cos(h)), _v_mul(E, math.sin(h))))
        right = _v_cross(fwd, U)
        return (right, U, _v_mul(fwd, -1.0), T)

    def path_point(self, tick=None):
        """(SN origin of the root grid, heading in SN degrees) at `tick`: the static pose, or the loop (centre sn,
 radius, speed; position c + r (sin th, 0, cos th), Forward along the circle: heading atan2(cos th, -sin th))."""
        tick = self.se.tick if tick is None else tick
        if self.speed <= 0.0 or self.radius <= 0.0:
            return self.sn_centre, self.heading
        t = (tick - self.t0_tick) / float(self.a.tick_hz)
        th = self.theta0 + self.speed / self.radius * t
        c = self.sn_centre
        p = (c[0] + self.radius * math.sin(th), c[1], c[2] + self.radius * math.cos(th))
        return p, math.degrees(math.atan2(math.cos(th), -math.sin(th)))

    def pose(self):
        lo, hi = self.hull.bounds()
        P = self.W.pivot_nearest(self.m_root, lo, hi, self.cam.T)
        o, q, s = self.W.wire_pose(self.cam.site, self.m_root, P)
        return P, o, q, s, 1.0

    # ---- the stream -------------------------------------------------------------------------------------------------
    def models_for(self, half):
        """(modelKey, [(C3 type, payload)]) of the procedural box model of half extents `half` (MaterialDef first)."""
        X = self.X
        mk = s3_model_key(X.box_model_path(half))
        out = []
        if ("mat", self.material_key) not in self.sent_models:
            self.sent_models.add(("mat", self.material_key))
            out.append((L.C3_TYPES["MaterialDef"], s3_material_def(self.material_key, self.textures)))
        if mk not in self.sent_models:
            self.sent_models.add(mk)
            out.extend(s3_model_messages(mk, X.box_model(half), self.material_key))
        return mk, out

    def skin_message(self, gid, why):
        X = self.X
        g = self.hull.grids[gid]
        v = self.versions.get(gid, 0) + 1
        self.versions[gid] = v
        payload = X.encode_skin(gid, v, g["sections"])
        near = bool(self.m_root) and math.dist(self.cam.T, self.m_root[3]) <= 50.0
        row = dict(self.hull.census(gid), whyKey=why, version=v, segmentBoxes=0, obbBoxes=0, cells=0, coverage="ok",
                   lane="NearEdit" if near else "Edit", buildMs=0.0, standin=True)
        return payload, row

    def enqueue_hull(self, why):
        """GridAdds (root first), the visual parts with their models on first use, then every grid's SkinSet."""
        h = self.hull
        order = sorted(h.grids, key=lambda g: (h.grids[g]["parent"] != 0, g))
        for gid in order:
            g = h.grids[gid]
            self.c4q.append((L.C4_TYPES["GridAdd"], L.RECORDS["GridAdd"].pack(
                entityId=gid, targetClass=g["cls"], flags=g["flags"], parentEntityId=g["parent"],
                worldEpoch=self.se.world_epoch), ("grid", gid)))
        for gid, key, half, rows in h.visual():
            mk, c3 = self.models_for(half)
            self.c3q.extend(c3)
            self.c4q.append((L.C4_TYPES["PartAdd"], L.RECORDS["PartAdd"].pack(
                entityId=gid, partKey=key, modelKey=mk, local=list(rows), colorMaskHSV=(0.58, 0.4, 0.1),
                uvOffset=(0.0, 0.0), skinId=0, flags=self.X.PART_V1 | self.X.PART_FAT), None))
        if h.skinned:
            for gid in order:
                payload, row = self.skin_message(gid, why)
                self.c4q.append((L.C4_TYPES["SkinSet"], payload, ("skin", gid, row)))

    def pending(self) -> bool:
        return bool(self.c3q or self.c4q)

    def flush(self):
        """Per tick: the C3 models first (a box model is far below one chunk), then C4 in order once C3 is empty (every
 PartAdd follows its model), try-once writes within the S5 budgets (a full ring keeps the message)."""
        se = self.se
        n = 0
        while self.c3q and n < S6_C3_BYTES_PER_TICK:
            t, p = self.c3q[0]
            st = se.c3p.try_write(t, p)
            if st == S.RW_FULL:
                self.full["C3"] += 1
                return
            if st != S.RW_OK:
                se._producer_corrupt(se.c3p)
                return
            self.c3q.popleft()
            n += len(p) + 8
            self.written["C3"] += 1
            self.written["C3Bytes"] += len(p) + 8
            se.written["C3"] += 1
        if self.c3q:
            return
        n = 0
        while self.c4q and n < S6_C4_BYTES_PER_TICK:
            t, p, meta = self.c4q[0]
            st = se.c4p.try_write(t, p)
            if st == S.RW_FULL:
                self.full["C4"] += 1
                return
            if st != S.RW_OK:
                se._producer_corrupt(se.c4p)
                return
            self.c4q.popleft()
            n += len(p) + 8
            self.written["C4"] += 1
            self.written["C4Bytes"] += len(p) + 8
            se.written["C4"] += 1
            if not meta:
                continue
            if meta[0] == "grid":
                se.written_grids.add(meta[1])
            elif meta[0] == "remove":
                se.written_grids.discard(meta[1])
                self.versions.pop(meta[1], None)
            elif meta[0] == "clear":
                se.written_grids.clear()
            elif meta[0] == "skin":
                self.written["skins"] += 1
                row = dict(meta[2])
                why = row.pop("whyKey", "first")
                se.emit("s6.skin", "DE.S6 skin %s v%d %d sections %d boxes %d B (%s)" % (
                    row["entity"], row["version"], row["sections"], row["boxes"], row["bytes"], why),
                    tick=se.tick, why=why, **row)

    # ---- spawn, teardown, resend ------------------------------------------------------------------------------------
    def spawn(self, args):
        kind = str(args.get("skin", self.kind or "bt1")).replace("synth:", "")
        if kind not in self.X.HULL_KINDS:
            raise ValueError("skin %s (known: %s)" % (kind, ", ".join(self.X.HULL_KINDS)))
        if self.spawned:
            self.teardown("respawn")
        sn = args.get("sn")
        if sn is None:
            cam = self.cam
            here = cam.F(cam.T)
            d = cam.mapdir(cam.T, cam.fwd)
            hh = math.hypot(d[0], d[2]) or 1.0
            ahead = float(args.get("aheadM", 30.0))
            sn = (here[0] + d[0] / hh * ahead, here[1], here[2] + d[2] / hh * ahead)
        self.kind = kind
        self.hull = self.X.Hull(kind)
        self.sn_centre = tuple(float(v) for v in sn)
        self.heading = float(args.get("yawDeg", args.get("headingDeg", self.a.skin_heading)))
        default_speed = self.a.skin_speed if self.a.skin_speed is not None else self.X.DEFAULT_SPEED[kind]
        self.speed = float(args.get("speedMps", default_speed))
        self.radius = float(args.get("radiusM", self.a.skin_radius))
        self.theta0 = math.radians(float(args.get("startDeg", 0.0)))
        self.t0_tick = self.se.tick
        p, hd = self.path_point()
        self.m_root = self.level_matrix(p, hd)
        self.spawned = True
        self.spawn_args = {"skin": kind, "sn": list(self.sn_centre), "yawDeg": self.heading, "speedMps": self.speed,
                           "radiusM": self.radius}
        self.enqueue_hull("first")
        P, o, q, s, k = self.pose()
        rep = {"skin": kind, "rootId": self.hexk(self.hull.root),
               "grids": [self.hull.census(g) for g in sorted(self.hull.grids)], "sn": list(self.sn_centre),
               "yawDeg": self.heading, "speedMps": self.speed, "radiusM": self.radius, "seMatrix": _rows12(self.m_root),
               "pivot": list(P), "o": list(o), "q": list(q), "s": s, "k": k, "tick": self.se.tick,
               "parts": len(self.hull.visual())}
        self.se.emit("s6.spawn", "DE.S6 stand-in hull %s at sn=(%.2f, %.2f, %.2f) speed %.1f m/s radius %.1f m" % (
            kind, self.sn_centre[0], self.sn_centre[1], self.sn_centre[2], self.speed, self.radius), **rep)
        return rep

    def teardown(self, why="teardown"):
        if self.hull:
            for gid in sorted(self.hull.grids, reverse=True):
                self.c4q.append((L.C4_TYPES["EntityRemove"], ST.entity_remove(gid), ("remove", gid)))
        self.spawned = False
        self.se.emit("s6.teardown", "DE.S6 stand-in teardown (%s)" % why, tick=self.se.tick, why=why, skin=self.kind)

    def resend(self, reason):
        """A new Subnautica generation or a link-epoch resend: ClearAll, then the hull again (skins from version 1)."""
        self.c3q.clear()
        self.c4q.clear()
        self.sent_models.clear()
        self.versions.clear()
        self.c4q.append((L.C4_TYPES["ClearAll"], ST.clear_all(self.se.world_epoch, self.se.link_epoch), ("clear",)))
        if self.spawned and self.hull:
            self.enqueue_hull("restart")
        self.se.emit("s6.resend", "DE.S6 resend (%s)" % reason, reason=reason, tick=self.se.tick)

    # ---- per tick ---------------------------------------------------------------------------------------------------
    def tick(self):
        se = self.se
        if not (se.hello_acked and self.in_world()) or se.waiting_link_ack:
            return
        if self.auto_spawn is not None and not self.spawned:
            self.spawn({"sn": list(self.auto_spawn), "skin": self.kind})
            self.auto_spawn = None
        if self.spawned and self.hull:
            self.hull.advance(1.0 / self.a.tick_hz)
            p, hd = self.path_point()
            self.m_root = self.level_matrix(p, hd)
        self.flush()
        now = time.monotonic()
        if now - self.last_stats >= 1.0:
            self.last_stats = now
            se.emit("s6.stats", tick=se.tick, written=dict(self.written), full=dict(self.full), c8=dict(self.c8),
                    dedup=dict(self.dedup.counts), spawned=self.spawned, skin=self.kind, c3Queue=len(self.c3q),
                    c4Queue=len(self.c4q))

    def write_c2(self):
        """C2 every tick before C1 (same tick): the root's entity record and the subgrid rows (rel)."""
        se = self.se
        ents, subs = [], []
        if self.in_world() and self.spawned and self.hull and self.m_root:
            P, o, q, s, k = self.pose()
            h = self.hull
            ents.append(L.RECORDS["SeEntity"].pack(
                entityId=h.root, kind=L.CONSTS["SeEntityKind"]["RootGrid"], flags=0, groupId=h.root, o=o, q=q, s=s,
                k=k, v=(0.0, 0.0, 0.0), omega=(0.0, 0.0, 0.0), partsVersion=len(h.visual()) & 0xFFFFFFFF,
                skinVersion=self.versions.get(h.root, 0)))
            for gid in h.subgrids():
                subs.append(L.RECORDS["SeSubpart"].pack(ownerId=h.root, partKey=gid, local=_rows12(h.rel(gid))))
            if se.tick % 30 == 0:
                key = tuple(round(x, 6) for x in tuple(P) + tuple(o) + tuple(q) + (s,))
                if key != self.pose_logged:
                    self.pose_logged = key
                    se.emit("s6.pose", tick=se.tick, entityId=self.hexk(h.root), seMatrix=_rows12(self.m_root),
                            pivot=list(P), o=list(o), q=list(q), s=s, k=k,
                            rel={self.hexk(g): _rows12(h.rel(g)) for g in h.subgrids()})
        hdr = L.RECORDS["SeEntitiesHeader"].pack(entityCount=len(ents), tick=se.tick, worldEpoch=se.world_epoch,
                                                 subpartCount=len(subs), lightCount=0)
        end = L.C2_ENTITIES_OFFSET + 96 * len(ents)
        buf = bytearray(L.C2_SUBPARTS_OFFSET + 64 * len(subs)) if subs else bytearray(end)
        buf[0:64] = hdr
        buf[64:end] = b"".join(ents)
        if subs:
            buf[L.C2_SUBPARTS_OFFSET:] = b"".join(subs)
        S.seqlock_write(se.sec, L.REGION["C2"].offset, bytes(buf))

    def on_c8(self, rec: bytes):
        """One C8 record from Subnautica: an s6.c8 row (the forwarded hit as SE would read it) and the counters."""
        v = self.X.unpack_record(rec)
        row = self.X.record_row(v)
        verdict = self.dedup.admit(v["snGeneration"], v["dedupSeq"])
        c = self.c8
        c["records"] += 1
        c["byType"][str(v["snDamageType"])] = c["byType"].get(str(v["snDamageType"]), 0) + 1
        c["byKind"][str(row["kind"])] = c["byKind"].get(str(row["kind"]), 0) + 1
        if verdict == "Duplicate":
            c["duplicates"] += 1
        self.se.emit("s6.c8", tick=self.se.tick, dedup=verdict, **row)

    # ---- commands ---------------------------------------------------------------------------------------------------
    def status(self):
        st = {"standin": True, "spawned": self.spawned, "skin": self.kind, "written": dict(self.written),
              "full": dict(self.full), "pending": self.pending(), "c8": dict(self.c8),
              "versions": {self.hexk(k): v for k, v in self.versions.items()}}
        if self.spawned and self.hull:
            p, hd = self.path_point()
            st.update(rootId=self.hexk(self.hull.root), snOrigin=list(p), headingDeg=hd, speedMps=self.speed,
                      radiusM=self.radius, centre=list(self.sn_centre),
                      grids=[self.hexk(g) for g in sorted(self.hull.grids)])
        return st

    def command(self, cmd, args, rid):
        args = args if isinstance(args, dict) else {}
        se = self.se
        try:
            if not self.in_world():
                raise ValueError("not in world")
            if "damage" in args or args.get("selftest") or args.get("action") in ("damage", "selftest"):
                return {"id": rid, "ok": False, "line": "de.s6 standin",
                        "error": "de.s6: the stand-in producer has no damage consumer (S6se runs the real SE)"}
            act = args.get("action")
            if act is None:
                act = "skin" if "skin" in args else ("resend" if "resend" in args else "status")
            label = str(args.get("label", ""))
            if act == "status":
                return {"id": rid, "ok": True, "line": "de.s6 status", "state": self.status(), "tick": se.tick}
            if act == "spawn":
                return dict({"id": rid, "ok": True, "line": "de.s6 spawn"}, **self.spawn(args))
            if act == "teardown":
                self.teardown(label or "command")
                return {"id": rid, "ok": True, "line": "de.s6 teardown", "tick": se.tick}
            if not (self.spawned and self.hull):
                raise ValueError("nothing spawned (de.s6 {action: spawn, skin, sn})")
            if act == "pose":
                P, o, q, s, k = self.pose()
                p, hd = self.path_point()
                return {"id": rid, "ok": True, "line": "de.s6 pose", "snOrigin": list(p), "headingDeg": hd,
                        "o": list(o), "q": list(q), "s": s, "tick": se.tick}
            if act == "motion":
                p, hd = self.path_point()
                speed = float(args.get("speedMps", self.speed))
                radius = float(args.get("radiusM", self.radius))
                if speed > 0.0 and radius > 0.0:
                    # the new loop starts where the hull is, heading unchanged: th with atan2(cos th, -sin th) = hd
                    h = math.radians(hd)
                    theta = math.atan2(-math.cos(h), math.sin(h))
                    self.sn_centre = (p[0] - radius * math.sin(theta), p[1], p[2] - radius * math.cos(theta))
                    self.theta0 = theta
                else:
                    self.sn_centre, self.heading = tuple(p), hd
                self.speed, self.radius, self.t0_tick = speed, radius, se.tick
                se.emit("s6.motion", "DE.S6 stand-in motion speed %.2f m/s radius %.1f m" % (speed, radius),
                        tick=se.tick, speedMps=speed, radiusM=radius, snOrigin=list(p))
                return {"id": rid, "ok": True, "line": "de.s6 motion", "speedMps": speed, "radiusM": radius,
                        "tick": se.tick}
            if act == "skin":
                want = args.get("skin")
                rows = bool(args.get("rows", False))
                gids = sorted(self.hull.grids) if want in (None, "", "all", True) else [_s6_id(want)]
                out = []
                for gid in gids:
                    if gid not in self.hull.grids:
                        raise ValueError("unknown entity %s (stand-in grids: %s)" % (
                            want, [self.hexk(g) for g in sorted(self.hull.grids)]))
                    out.append(dict(self.hull.census(gid, rows), version=self.versions.get(gid, 0)))
                return {"id": rid, "ok": True, "line": "de.s6 skin", "skins": out, "tick": se.tick}
            if act == "resend":
                want = args.get("resend")
                gids = sorted(self.hull.grids) if want in (None, "", "all", True) else [_s6_id(want)]
                for gid in gids:
                    if gid not in self.hull.grids:
                        raise ValueError("unknown entity %s" % want)
                    payload, row = self.skin_message(gid, "resend")
                    self.c4q.append((L.C4_TYPES["SkinSet"], payload, ("skin", gid, row)))
                return {"id": rid, "ok": True, "line": "de.s6 resend", "entities": [self.hexk(g) for g in gids],
                        "tick": se.tick}
            raise ValueError("unknown de.s6 action %s" % act)
        except (KeyError, TypeError, ValueError, OSError) as e:
            se.emit("s6.cmd", "DE.S6 %s failed: %s" % (cmd, e), error=str(e), args=args)
            return {"id": rid, "ok": False, "line": "de.s6 standin", "error": "%s: %s" % (cmd, e)}

    def summary(self):
        self.se.emit("s6.summary", "DE.S6 stand-in summary: skin=%s spawned=%s written C3 %d / C4 %d (skins %d), C8 %d "
                     "records" % (self.kind, self.spawned, self.written["C3"], self.written["C4"],
                                  self.written["skins"], self.c8["records"]), written=self.written, c8=self.c8,
                     dedup=self.dedup.counts)

class Se:
    def __init__(self, a):
        self.a = a
        self.emit = ST.Emitter("se", quiet=a.quiet, human=a.human)
        self.name = a.name
        _sn, self.mutex_se, _l = L.mutex_names(a.name)
        self.pid = S.current_pid()
        self.t0 = time.monotonic()
        self.sec = None
        self.mutex = None
        self.generation = 0
        self.phase = 0
        self.tick = 0
        self.world_epoch = 0
        self.hello_epoch = 0
        self.link_epoch = 0
        self.overlays = 0
        self.site_hash = ST.site_hash()
        # peer
        self.state = "none"         # none | alive | stalled | exited | rejected
        self.peer_key = None
        self.peer_pid = 0
        self.peer_gen = 0
        self.watch = None
        self.last_exit_poll = 0.0
        self.stall_hb = 0
        self.last_hb_value = None
        self.last_hb_change = 0
        self.reinit_seen = False
        self.hello_acked = False
        self.resend_pending = None
        self.resend = None
        self.waiting_link_ack = None
        self.c7 = None
        # scene and streaming
        self.serial = 0
        self.grids = collections.deque()
        self.written_grids = set()
        self.c3q = collections.deque()
        self.c4q = collections.deque()
        self.send_serial = 0
        self.next_churn = 0.0
        self.next_recolor = 0.0
        self.next_model = 0.0
        self.recolor_k = 0
        self.model_k = 0
        self.full = {"C3": 0, "C4": 0}
        self.written = {"C3": 0, "C4": 0}
        self.producer_corrupt_logged = False
        self.damage = ST.DamageChecker()
        self.c8_corrupt = 0
        self.c12_messages = 0
        self.c11_handled = 0
        self.last_stats = 0.0
        self.last_wait_log = -1e9
        self.reject_logged = {}
        for _ in range(a.grids):
            self.add_grid(enqueue=False)
        # S1
        self.cam = S1Camera(self, a) if a.s1 else None
        if self.cam and a.s3_mutant == "M4":
            self.cam.mutant = "prev-tick"      # S3.M4: the whole snapshot one tick late (C1 here, C2 in S3aScene)
        self.s2 = S2Se(self, a) if a.s2 else None
        self.s3 = S3aScene(self, a) if a.s3a else None
        self.s5 = S5Scene(self, a) if a.s5 else None
        self.s6 = S6Scene(self, a) if a.s6 else None
        # S7 (statics census): the camera visits every batch centre (tests/standins/s7_census_path.py; a hook)
        if getattr(a, "s7_census", False):
            import s7_census_path   # noqa: E402
            self.s7census = s7_census_path.S7CensusPath(self, a)
        else:
            self.s7census = None
        self.in_world_t = None
        self.script = None
        self.script_next = 0.0
        self.script_started = False
        self.exit_at = None
        self.capture_pending = None
        self.capture_req_id = 0
        self.yaw_index = 0

    # ------------------------------------------------------------------------------------------------ connecting
    def deadline_passed(self) -> bool:
        a = self.a
        el = time.monotonic() - self.t0
        return bool((a.run_seconds and el >= a.run_seconds) or (a.give_up_after and el >= a.give_up_after))

    def wait_log(self, why: str):
        now = time.monotonic()
        if now - self.last_wait_log >= L.LINK_WAIT_LOG_S:
            self.last_wait_log = now
            self.emit("wait", "DE.Link WAIT %s" % why, reason=why)

    def connect(self) -> int:
        a = self.a
        while True:
            if self.deadline_passed():
                self.emit("give_up", "never linked", seconds=round(time.monotonic() - self.t0, 3))
                return EXIT_NEVER_LINKED
            el = time.monotonic() - self.t0
            retry = L.SE_OPEN_RETRY_S if el < 60 else L.SE_OPEN_RETRY_SLOW_S
            try:
                sec = S.Section.open(self.name)
            except OSError as e:
                self.wait_log("open error %s" % e)
                time.sleep(retry)
                continue
            if sec is None:
                self.wait_log("section absent")
                time.sleep(retry)
                continue
            r = S.validate(sec, selftest_pass=not a.selftest_fail)
            if r["outcome"] == "not_ready":
                sec.close()
                self.wait_log("magic 0 (creator initialising)")
                time.sleep(min(retry, 0.25))
                continue
            if r["outcome"] == "reject":
                sec.close()
                key = (r["field"], r["found"])
                if time.monotonic() - self.reject_logged.get(key, -1e9) >= L.LINK_WAIT_LOG_S:
                    self.reject_logged[key] = time.monotonic()
                    self.emit("reject", "DE.Link REJECT %s" % S.describe_validation(r), step=r["step"],
                              field=r["field"], expected=r["expected"], found=r["found"])
                if a.exit_on_reject:
                    return EXIT_REFUSED
                time.sleep(L.SE_OPEN_RETRY_SLOW_S if not a.fast_retry else retry)
                continue
            st, m, err = S.NamedMutex.try_acquire(self.mutex_se)
            if st in ("held", "error"):
                sec.close()
                self.emit("refuse", "DE.Link REFUSE second instance" if st == "held" else
                          "DE.Link REFUSE mutex error %d" % err, mutex=self.mutex_se)
                return EXIT_REFUSED
            self.mutex = m
            self.sec = sec
            self.attach(r)
            return EXIT_OK

    def attach(self, r):
        s = self.sec
        self.c7 = S.SeqlockReader(s, L.REGION["C7"].offset, L.SN_STATUS)
        self.link_epoch = L.SE_STATE.unpack(s.read(L.REGION["C5"].offset, 256))["linkEpoch"]
        self.write_c5()   # our C5 before our PID/generation (a host never reads a previous SE's hello as ours)
        self.generation = (s.hdr("seGeneration") + 1) & S.MASK32 or 1
        s.set_hdr("seHeartbeatMs", S.tick64())
        s.set_identity("se", self.pid, self.generation)   # heartbeat first, then PID + generation as one u64
        s.set_hdr("seFlags", 1 << L.SE_FLAGS["gateOpen"])
        s.set_hdr("seHwnd", 0)
        s.set_hdr("seLoadStep", 0)
        self.set_phase(L.SE_PHASE["Menu"], emit=False)
        self.c3p = S.ByteRing(s, "C3")
        self.c4p = S.ByteRing(s, "C4")
        self.c11b = S.ByteRing(s, "C11b")
        self.c8 = S.EntryRing(s, "C8")
        self.c12 = S.ByteRing(s, "C12")
        self.c11a = S.ByteRing(s, "C11a")
        if self.s2:
            self.s2.start()
        self.emit("link_up", "DE.Link UP generation=%d (%s)" % (self.generation, S.describe_validation(r)),
                  seGeneration=self.generation, notes=r["notes"], heads=s.ring_heads(), mutant=self.a.mutant)

    def set_phase(self, p, emit=True):
        self.phase = p
        self.sec.set_hdr("sePhase", p)
        if emit:
            self.emit("phase", "sePhase=%s" % L.SE_PHASES[p], phase=p, name=L.SE_PHASES[p])

    # -------------------------------------------------------------------------------------------------- the loop
    def run(self) -> int:
        a = self.a
        self.emit("hello", "DE.Hello fake_se_producer wire v%d layout %d.%d hash %s site v%d siteHash %s" % (
            L.WIRE_VERSION, L.LAYOUT_MAJOR, L.LAYOUT_MINOR, L.hex64(L.layout_hash()), L.SITE_VERSION,
            L.hex64(self.site_hash)), name=self.name, mutant=a.mutant)
        rc = self.connect()
        if rc != EXIT_OK:
            return rc
        period = 1.0 / a.tick_hz
        next_t = time.perf_counter()
        attached_at = time.monotonic()
        stalled_once = False
        while True:
            el = time.monotonic() - self.t0
            if a.run_seconds and el >= a.run_seconds:
                return self.exit("run time over")
            if self.exit_at is not None and time.monotonic() >= self.exit_at:
                return self.exit("S1 script done")
            if a.exit_after and el >= a.exit_after:
                if a.exit_mode == "abrupt":
                    self.emit("abrupt_exit", "abrupt exit (no cleanup)")
                    os._exit(9)
                return self.exit("exit-after")
            if a.stall_after and el >= a.stall_after and not stalled_once:
                stalled_once = True
                self.emit("stall_begin", "SE stalling %.2f s (no writes)" % a.stall_for, seconds=a.stall_for)
                time.sleep(a.stall_for)
                self.emit("stall_end", "SE stall over")
                next_t = time.perf_counter()
            since = time.monotonic() - attached_at
            if self.phase == L.SE_PHASE["Menu"] and since >= a.load_after:
                self.set_phase(L.SE_PHASE["LoadingWorld"])
            if self.phase == L.SE_PHASE["LoadingWorld"] and since >= a.load_after + a.load_seconds:
                self.world_epoch += 1
                self.hello_epoch = self.world_epoch
                self.set_phase(L.SE_PHASE["InWorld"])
                self.emit("hello_sent", "hello epoch %d (Rs=%.8f siteHash=%s)" % (
                    self.hello_epoch, L.RS_EXPECTED, L.hex64(self.site_hash)), helloEpoch=self.hello_epoch)
            self.do_tick()
            next_t += period
            d = next_t - time.perf_counter()
            if d > 0:
                time.sleep(d)
            else:
                next_t = time.perf_counter()

    def do_tick(self):
        s = self.sec
        self.tick += 1
        now = S.tick64()
        s.set_hdr("seHeartbeatMs", now)
        if self.state != "rejected":
            self.detect(now)
        if self.c7:
            self.c7.poll()
        if self.state in ("alive", "stalled") and self.c7.value:
            self.after_peer_status()
        if self.state in ("alive", "stalled") and self.hello_acked and not self.waiting_link_ack and \
                (not self.cam or self.a.s1_stream):
            self.produce()
        self.drain()
        if self.s3 and self.state in ("alive", "stalled"):
            self.s3.tick()
        if self.s5 and self.state in ("alive", "stalled"):
            self.s5.tick()
        if self.s6 and self.state in ("alive", "stalled"):
            self.s6.tick()
        if self.cam:
            self.s1_tick()
        if self.s3:
            self.s3.write_c2()              # C2 before C1, same tick (CONTRACT 5.1: C2's tick must equal C1's)
        if self.s5:
            self.s5.write_c2()
        if self.s6:
            self.s6.write_c2()
        self.write_c1()
        if self.s3:
            self.s3.log_gps()
        if self.s5:
            self.s5.after_c1()
        if self.s2:
            self.s2.tick()
        self.write_c5()
        self.write_c6()
        el = time.monotonic()
        if el - self.last_stats >= 1.0:
            self.last_stats = el
            self.emit("stats", state=self.state, peer=[self.peer_pid, self.peer_gen], seGeneration=self.generation,
                      linkEpoch=self.link_epoch, helloAcked=self.hello_acked, grids=len(self.grids),
                      writtenGrids=len(self.written_grids), c3Queue=len(self.c3q), c4Queue=len(self.c4q),
                      written=self.written, full=self.full, producerCorrupt=self.c3p.corrupt + self.c4p.corrupt,
                      c8Records=self.damage.records, c8Misparse=self.damage.misparse, c8Stale=self.damage.stale,
                      c8Gaps=self.damage.gaps, c8Corrupt=self.c8_corrupt, heads=s.ring_heads(), tails=s.ring_tails(),
                      details=self.damage.details[:5])

    # ------------------------------------------------------------------------------------------------- detector
    def detect(self, now):
        s = self.sec
        m = self.a.mutant
        magic = s.hdr("magic")
        if magic != L.MAGIC:
            if magic == 0:
                if not self.reinit_seen:
                    self.reinit_seen = True
                    self.emit("peer_reinit", "magic 0: the creator is re-initialising the section")
                return
            self.state = "rejected"
            self.emit("reject", "DE.Link REJECT magic changed to %s while linked" % hex(magic), field="magic",
                      expected=hex(L.MAGIC), found=hex(magic))
            return
        self.reinit_seen = False
        pid, gen = s.identity("sn")      # one u64 read
        hb, ph = s.hdr("snHeartbeatMs"), s.hdr("snPhase")
        key = pid if m == "heartbeat-liveness" else (pid, gen)
        if key != self.peer_key:
            if self.peer_key is not None and self.state != "exited":
                self.peer_exited("pid changed" if pid != self.peer_pid else "generation changed", now, hb)
            r = S.validate(s, selftest_pass=not self.a.selftest_fail)
            if r["outcome"] != "ok":
                if r["outcome"] == "reject":
                    self.state = "rejected"
                    self.emit("reject", "DE.Link REJECT %s" % S.describe_validation(r), step=r["step"],
                              field=r["field"], expected=r["expected"], found=r["found"])
                return
            self.new_peer(key, pid, gen, now, hb)
            return
        if self.state == "exited":
            return
        poll_due = (time.monotonic() - self.last_exit_poll) * 1000 >= self.a.exit_poll_ms
        if m not in ("no-exit-poll", "heartbeat-liveness") and self.watch and poll_due:
            self.last_exit_poll = time.monotonic()
            if self.watch.exited():
                self.peer_exited("process exit", now, hb)
                return
        if ph in (L.SN_PHASE["Quitting"], L.SN_PHASE["None"]):
            self.peer_exited("snPhase %s" % L.SN_PHASES[ph], now, hb)
            return
        if m == "heartbeat-liveness":
            if hb != self.last_hb_value:
                self.last_hb_value = hb
                self.last_hb_change = now
            age = now - self.last_hb_change
            limit = L.STALL_PLAY_S * 1000
        else:
            age = now - hb if now >= hb else 0
            loading = ph in L.SN_LOADING_PHASES or self.phase in L.SE_LOADING_PHASES
            limit = (L.STALL_LOADING_S if loading else (30.0 if m == "timeout30" else L.STALL_PLAY_S)) * 1000
        if self.state == "alive" and age >= limit:
            self.state = "stalled"
            self.stall_hb = hb if m != "heartbeat-liveness" else self.last_hb_change
            self.overlays |= BIT("PeerStalled", L.OVERLAYS)
            self.emit("peer_stalled", "PeerStalled: heartbeat %d ms old (limit %d ms)" % (age, limit), ageMs=age,
                      limitMs=limit, lastHeartbeatMs=hb)
        elif self.state == "stalled" and age < limit:
            self.stall_ended(now)

    def new_peer(self, key, pid, gen, now, hb):
        prev = (self.peer_pid, self.peer_gen) if self.peer_key is not None else None
        self.peer_key, self.peer_pid, self.peer_gen = key, pid, gen
        if self.watch:
            self.watch.close()
        self.watch = S.ProcessWatch(pid)
        self.state = "alive"
        self.last_hb_value, self.last_hb_change = hb, now
        self.overlays &= ~(BIT("PeerStalled", L.OVERLAYS) | BIT("PeerExited", L.OVERLAYS))
        tails = {}
        for k, ring in (("C8", self.c8), ("C12", self.c12), ("C11a", self.c11a)):
            tails[k] = ring.resync_tail()
        self.damage.reset(gen)
        self.hello_acked = False
        self.waiting_link_ack = None
        self.c7.reset()
        self.resend_pending = "initial" if prev is None else "new generation"
        self.emit("new_peer", "new Subnautica peer pid=%d generation=%d (previous %s)" % (pid, gen, prev),
                  snPid=pid, snGeneration=gen, previous=prev, watchError=self.watch.err)
        self.emit("tails_resynced", "consumer tails := heads %s" % tails, tails=tails,
                  heads={k: self.sec.ring_head(k) for k in tails})

    def peer_exited(self, reason, now, hb):
        self.state = "exited"
        self.overlays |= BIT("PeerExited", L.OVERLAYS)
        self.resend = None
        self.emit("peer_exited", "PeerExited (%s)" % reason, reason=reason, heartbeatAgeMs=now - hb if now >= hb else 0)

    def stall_ended(self, now):
        stall_ms = now - self.stall_hb if now >= self.stall_hb else 0
        self.state = "alive"
        self.overlays &= ~BIT("PeerStalled", L.OVERLAYS)
        self.link_epoch = (self.link_epoch + 1) & S.MASK32
        tails = {k: r.resync_tail() for k, r in (("C8", self.c8), ("C12", self.c12), ("C11a", self.c11a))}
        self.damage.reset(self.peer_gen)
        self.waiting_link_ack = {"epoch": self.link_epoch, "stallMs": stall_ms, "t": time.monotonic()}
        self.write_c5()
        self.emit("link_epoch", "stall over after %d ms: linkEpoch=%d, tails re-synced" % (stall_ms, self.link_epoch),
                  linkEpoch=self.link_epoch, stallMs=stall_ms, tails=tails)

    def after_peer_status(self):
        v = self.c7.value
        if self.hello_epoch and not self.hello_acked and v["helloAckEpoch"] == self.hello_epoch:
            self.hello_acked = True
            self.emit("hello_acked", "hello %d acknowledged (hostKind=%d capabilities=0x%x)" % (
                self.hello_epoch, v["hostKind"], v["capabilities"]), hostKind=v["hostKind"],
                capabilities=v["capabilities"])
        if self.waiting_link_ack and v["linkAckEpoch"] == self.waiting_link_ack["epoch"]:
            w, self.waiting_link_ack = self.waiting_link_ack, None
            host = v["mirroredEntityCount"]
            sent = len(self.written_grids)
            if w["stallMs"] >= L.RESEND_STALL_S * 1000:
                why = "stall %d ms >= 10 s" % w["stallMs"]
            elif host != sent:
                why = "counters disagree (host %d grids, sent %d)" % (host, sent)
            else:
                why = None
            self.emit("link_ack", "linkAckEpoch=%d: %s" % (w["epoch"], "resend" if why else "continue"),
                      linkEpoch=w["epoch"], hostGrids=host, sentGrids=sent, pendingParts=v["pendingParts"],
                      decision="resend" if why else "continue")
            if why:
                self.start_resend("link epoch %d: %s" % (w["epoch"], why))
        if self.hello_acked and self.resend_pending:
            why, self.resend_pending = self.resend_pending, None
            self.start_resend(why)
        if self.resend:
            # complete only once the host consumed past the end of the resend (its tails) and then published a C7
 # with matching counters: an older C7 could show equal counters from before the ClearAll
            r = self.resend
            if r["flushed"] is None and not self.c3q and not self.c4q and not (self.s3 and self.s3.queue) and \
                    not (self.s5 and self.s5.pending()) and not (self.s6 and self.s6.pending()):
                r["flushed"] = (self.c3p.producer_head, self.c4p.producer_head)
            if r["flushed"] is not None and r["c7Seq"] is None and \
                    self.sec.ring_tail("C3") >= r["flushed"][0] and self.sec.ring_tail("C4") >= r["flushed"][1]:
                r["c7Seq"] = self.c7.last_seq
        if self.resend and self.resend["c7Seq"] is not None and self.c7.last_seq != self.resend["c7Seq"] and \
                v["mirroredEntityCount"] == len(self.written_grids) and v["pendingParts"] == 0 and \
                v["linkAckEpoch"] == self.link_epoch:
            r, self.resend = self.resend, None
            self.emit("resend_complete", "resend complete (%s) in %.0f ms: %d grids, %d parts, models complete" % (
                r["reason"], (time.monotonic() - r["t"]) * 1000, len(self.written_grids),
                len(self.written_grids) * ST.PARTS_PER_GRID), reason=r["reason"],
                ms=round((time.monotonic() - r["t"]) * 1000, 1), grids=len(self.written_grids))

    # ------------------------------------------------------------------------------------------------ streaming
    def add_grid(self, enqueue=True):
        self.serial += 1
        gid = ST.grid_id(self.serial)
        self.grids.append(gid)
        if enqueue:
            self.enqueue_grid(gid)
        if len(self.grids) > self.a.max_grids:
            old = self.grids.popleft()
            if enqueue:
                self.c4q.append((L.C4_TYPES["EntityRemove"], ST.entity_remove(old), ("remove", old)))

    def enqueue_grid(self, gid):
        self.c4q.append((L.C4_TYPES["GridAdd"], ST.grid_add(gid, self.world_epoch), ("grid", gid)))
        for i in range(ST.PARTS_PER_GRID):
            self.c4q.append((L.C4_TYPES["PartAdd"], ST.part_add(gid, i), None))

    def enqueue_model(self, key):
        self.c3q.append(("begin", key))
        for i in range(ST.model_chunk_count(key)):
            self.c3q.append(("chunk", key, i))
        self.c3q.append(("end", key))

    def start_resend(self, reason):
        self.c3q.clear()
        self.c4q.clear()
        if self.s6:
            # S6: the synthetic hull and its skin are the stand-in's whole C3/C4 traffic
            self.s6.resend(reason)
            self.resend = {"reason": reason, "t": time.monotonic(), "flushed": None, "c7Seq": None}
            return
        if self.s5:
            # S5: the replayed or synthesised stream is the stand-in's whole C3/C4 traffic
            self.s5.resend(reason)
            self.resend = {"reason": reason, "t": time.monotonic(), "flushed": None, "c7Seq": None}
            return
        if self.s3:
            # S3a: the scene is the stand-in's whole C3/C4 traffic (no synthetic S0 catalogue)
            self.s3.resend(reason)
            self.resend = {"reason": reason, "t": time.monotonic(), "flushed": None, "c7Seq": None}
            return
        self.c4q.append((L.C4_TYPES["ClearAll"], ST.clear_all(self.world_epoch, self.link_epoch), ("clear",)))
        for gid in self.grids:
            self.enqueue_grid(gid)
        for key in ST.SCENE_MODELS:
            self.enqueue_model(key)
        self.resend = {"reason": reason, "t": time.monotonic(), "flushed": None, "c7Seq": None}
        self.emit("resend_request", "resend everything (%s): ClearAll(%d, %d), %d grids, %d models" % (
            reason, self.world_epoch, self.link_epoch, len(self.grids), len(ST.SCENE_MODELS)), reason=reason,
            grids=len(self.grids), linkEpoch=self.link_epoch)

    def produce(self):
        a = self.a
        now = time.monotonic()
        if a.churn_ms > 0 and now >= self.next_churn:
            self.next_churn = now + a.churn_ms / 1000.0
            self.add_grid()
        if a.recolor_hz > 0 and now >= self.next_recolor and self.grids:
            self.next_recolor = now + 1.0 / a.recolor_hz
            self.recolor_k += 1
            gid = self.grids[self.recolor_k % len(self.grids)]
            self.c4q.append((L.C4_TYPES["PartRecolor"], ST.part_recolor(gid, self.recolor_k % ST.PARTS_PER_GRID,
                                                                        self.recolor_k), None))
        if a.model_stream_ms > 0 and now >= self.next_model and len(self.c3q) < 64:
            self.next_model = now + a.model_stream_ms / 1000.0
            self.model_k += 1
            self.enqueue_model(ST.SCENE_MODELS[self.model_k % len(ST.SCENE_MODELS)])
        self.flush_c4()
        self.flush_c3()

    def _producer_corrupt(self, ring):
        if not self.producer_corrupt_logged:
            self.producer_corrupt_logged = True
            self.emit("producer_corrupt", "%s: impossible counters (head %d, tail %d): not writing" % (
                ring.key, ring.producer_head, self.sec.ring_tail(ring.key)), ring=ring.key,
                producerHead=ring.producer_head, headWord=self.sec.ring_head(ring.key),
                tail=self.sec.ring_tail(ring.key))

    def flush_c4(self):
        n = 0
        while self.c4q and n < 500:
            mtype, payload, meta = self.c4q[0]
            st = self.c4p.try_write(mtype, payload)
            if st == S.RW_FULL:
                self.full["C4"] += 1
                return
            if st != S.RW_OK:
                self._producer_corrupt(self.c4p)
                return
            self.c4q.popleft()
            self.written["C4"] += 1
            n += 1
            if meta:
                if meta[0] == "grid":
                    self.written_grids.add(meta[1])
                elif meta[0] == "remove":
                    self.written_grids.discard(meta[1])
                elif meta[0] == "clear":
                    self.written_grids.clear()

    def flush_c3(self):
        n = 0
        while self.c3q and n < 96:
            item = self.c3q[0]
            if item[0] == "begin":
                mtype, payload = L.C3_TYPES["ModelBegin"], ST.model_begin(item[1])
            elif item[0] == "chunk":
                self.send_serial += 1
                mtype, payload = L.C3_TYPES["ModelChunk"], ST.model_chunk(item[1], item[2], self.send_serial)
            else:
                mtype, payload = L.C3_TYPES["ModelEnd"], ST.model_end(item[1])
            st = self.c3p.try_write(mtype, payload)
            if st == S.RW_FULL:
                self.full["C3"] += 1
                return
            if st != S.RW_OK:
                self._producer_corrupt(self.c3p)
                return
            self.c3q.popleft()
            self.written["C3"] += 1
            n += 1

    def drain(self):
        for _ in range(4096):
            st, rec = self.c8.try_read()
            if st == S.RR_EMPTY:
                break
            if st == S.RR_CORRUPT:
                self.c8_corrupt += 1
                self.emit("c8_corrupt", "C8 counters impossible: re-sync", head=self.sec.ring_head("C8"),
                          tail=self.c8.consumer_tail)
                self.c8.resync_tail()
                break
            if self.s6:
                self.s6.on_c8(rec)     # S6: the forwarded hits as s6.c8 rows (no S0 content check)
            else:
                self.damage.feed(rec)
        for _ in range(256):
            st, _t, _p = self.c12.try_read()
            if st != S.RR_OK:
                if st == S.RR_CORRUPT:
                    self.c12.resync_tail()
                break
            self.c12_messages += 1
        if self.sec.hdr("devControl") & 1:
            for _ in range(16):
                st, mtype, payload = self.c11a.try_read()
                if st != S.RR_OK:
                    if st == S.RR_CORRUPT:
                        self.c11a.resync_tail()
                    break
                self.c11_handled += 1
                try:
                    req = json.loads(payload.decode("utf-8"))
                    rid, cmd = req.get("id"), req.get("cmd")
                    args = req.get("args") or {}
                except (ValueError, AttributeError):
                    rid, cmd, args = None, None, {}
                if cmd == "de.ping":
                    rep = {"id": rid, "ok": True, "pong": True, "seGeneration": self.generation}
                elif cmd == "de.state":
                    rep = {"id": rid, "ok": True, "state": self.state, "linkEpoch": self.link_epoch,
                           "peer": [self.peer_pid, self.peer_gen], "sePhase": L.SE_PHASES.get(self.phase),
                           "tick": self.tick, "s1": bool(self.cam)}
                    if self.cam:
                        rep.update(label=self.cam.label, camSe=list(self.cam.T), pathActive=bool(self.cam.path),
                                   camPos=list(self.cam.F(self.cam.T)), motion=bool(self.cam.motion))
                    if self.s3:
                        rep.update(s3={"placed": bool(self.s3.placed), "queue": len(self.s3.queue),
                                       "written": self.s3.written, "gps": self.s3.gps_on})
                    if self.s5:
                        rep.update(s5=self.s5.status())
                    if self.s6:
                        rep.update(s6=self.s6.status())
                elif self.s3 and cmd in S3_COMMANDS:
                    rep = self.s3.command(args, rid)
                elif self.s5 and cmd in S5_COMMANDS:
                    rep = self.s5.command(cmd, args, rid)
                elif self.s6 and cmd in S6_COMMANDS:
                    rep = self.s6.command(cmd, args, rid)
                elif self.s2 and cmd in S2_COMMANDS:
                    rep = self.s2.command(cmd, args, rid)
                elif (self.s3 or self.s5 or self.s6) and cmd == "de.motion":
                    rep = motion_command(self, args if isinstance(args, dict) else {}, rid)
                elif self.cam and cmd in S1_COMMANDS:
                    rep = self.s1_command(cmd, args if isinstance(args, dict) else {}, rid)
                else:
                    rep = {"id": rid, "ok": False, "error": "unknown command %s" % cmd}
                if rep is not None:
                    self.reply(rep)

    def reply(self, rep):
        self.c11b.try_write_or_drop(L.C11_TYPES["Json"], json.dumps(rep).encode("utf-8"))

    # ------------------------------------------------------------------------------------------------------- S1
    def in_world(self) -> bool:
        return self.phase == L.SE_PHASE["InWorld"]

    def s1_command(self, cmd, args, rid):
        """One S1 test command (C11 or the --s1-script scheduler). Returns the reply (None: answered later)."""
        cam = self.cam
        label = args.get("label", "")
        self.emit("s1.cmd", "S1 %s %s" % (cmd, label), cmd=cmd, id=rid, label=label, phase="start", tick=self.tick,
                  args=args)
        try:
            if not self.in_world():
                raise ValueError("not in world (sePhase %s)" % L.SE_PHASES.get(self.phase))
            if cmd == "de.teleport":
                fov = cam.fov if "vFovDeg" not in args else (self.a.fov if args["vFovDeg"] is None else args["vFovDeg"])
                sn = args.get("sn") or [args["x"], args["y"], args["z"]]
                heading = float(args.get("headingDeg", args.get("yawDeg", 0.0)))
                cam.teleport(sn, heading, float(args.get("pitchDeg", 0.0)), fov, label)
                return {"id": rid, "ok": True, "tick": self.tick, "camSe": list(cam.T), "camPos": list(cam.F(cam.T)),
                        "vFovDeg": cam.fov, "label": label}
            if cmd == "de.look":
                d = str(args.get("dir", "N"))
                if d[:3] in ("yaw", "pit") and len(d) > 4:      # yaw+30, yaw-30, pitch+45: relative turns
                    return self.s1_turn(d, args, label, rid)
                cam.look(d, label)
                return {"id": rid, "ok": True, "tick": self.tick, "fwdSe": list(cam.fwd), "label": label}
            if cmd == "de.path":
                if "fromSn" not in args:
                    args = dict(args, fromSn=[0.0, float(args.get("altitude", -100.0)), 0.0])
                t0, t1 = cam.path_start(args, self.tick)
                return {"id": rid, "ok": True, "tick": t0, "startTick": t0, "endTick": t1, "label": label}
            if cmd == "de.yawStep":
                return self.s1_turn("yaw%+g" % float(args.get("deg", 30.0)), args, label, rid)
            if cmd == "de.pixel":
                px = cam.pixel(float(args.get("aheadM", args.get("ahead", 10.0))), label, self.tick)
                self.emit("s1.pixel", **px)
                rep = {"id": rid, "ok": True}
                rep.update(px)
                return rep
            if cmd == "de.capture":
                return self.capture_request(rid, args)
            raise ValueError("unknown S1 command %s" % cmd)
        except (KeyError, TypeError, ValueError) as e:
            self.emit("s1.cmd", "S1 %s failed: %s" % (cmd, e), cmd=cmd, id=rid, label=label, phase="error",
                      tick=self.tick, error=str(e))
            return {"id": rid, "ok": False, "error": str(e)}

    def s1_turn(self, d, args, label, rid):
        """de.look yaw+N / yaw-N (S1.N2: logs s1.yawStep with qpc(T)) and pitch+N / pitch-N (G1-E5 elevations)."""
        cam = self.cam
        deg = float(d[3:] if d.startswith("yaw") else d[5:])
        if d.startswith("yaw"):
            qpc = S.qpc()
            y0, y1 = cam.yaw_step(deg)
            idx = args.get("index", self.yaw_index)
            self.yaw_index += 1
            self.emit("s1.yawStep", index=idx, tick=self.tick, qpc=qpc, yawFromDeg=y0, yawToDeg=y1, label=label)
            return {"id": rid, "ok": True, "tick": self.tick, "qpc": qpc, "yawFromDeg": y0, "yawToDeg": y1}
        cam.pitch_step(deg, label)
        return {"id": rid, "ok": True, "tick": self.tick, "fwdSe": list(cam.fwd), "label": label}

    def capture_request(self, rid, args):
        """C10: SE writes the request (relayed from the harness), Subnautica the result; one request in flight."""
        off = L.REGION["C10"].offset
        if not self.capture_req_id:
            found = L.CAPTURE_REQUEST.unpack(self.sec.read(off + L.C10_REQUEST_OFFSET, L.CAPTURE_REQUEST.size))
            self.capture_req_id = found["reqId"]
        self.capture_req_id = (self.capture_req_id + 1) & S.MASK32 or 1
        path = str(args.get("path") or "s1/capture-%d.png" % self.capture_req_id)
        pb = path.encode("utf-8")[:230]
        kind = int(args.get("kind", L.CONSTS["CaptureKind"]["NextFramePng"]))
        rec = L.CAPTURE_REQUEST.pack(reqId=self.capture_req_id, kind=kind, atSeTick=int(args.get("atSeTick", 0)),
                                     pathLength=len(pb), path=pb)
        S.seqlock_write(self.sec, off + L.C10_REQUEST_OFFSET, rec)
        if self.capture_pending and self.capture_pending["rid"] is not None:
            self.reply({"id": self.capture_pending["rid"], "ok": False, "error": "replaced by a newer request"})
        self.capture_pending = {"rid": rid, "reqId": self.capture_req_id, "t0": time.monotonic(), "path": path,
                                "label": args.get("label", "")}
        return None

    def capture_poll(self):
        cp = self.capture_pending
        if not cp:
            return
        st, _seq, data = S.seqlock_read(self.sec, L.REGION["C10"].offset + L.C10_RESULT_OFFSET,
                                        L.CAPTURE_RESULT.size)
        res = L.CAPTURE_RESULT.unpack(data) if data else None
        done = res is not None and res["reqId"] == cp["reqId"] and res["status"] != L.CONSTS["CaptureStatus"]["Pending"]
        if not done and time.monotonic() - cp["t0"] < 5.0:
            return
        self.capture_pending = None
        ok = bool(done and res["status"] == L.CONSTS["CaptureStatus"]["Ok"])
        rep = {"id": cp["rid"], "ok": ok, "reqId": cp["reqId"], "path": cp["path"], "label": cp["label"],
               "status": res["status"] if done else "timeout", "snFrame": res["snFrame"] if done else None,
               "seTickUsed": res["seTickUsed"] if done else None}
        self.emit("s1.cmd", "S1 de.capture %s" % ("ok" if ok else "failed"), cmd="de.capture", id=cp["rid"],
                  label=cp["label"], phase="done" if ok else "error", tick=self.tick, result=rep)
        if cp["rid"] is not None:
            self.reply(rep)

    def s1_tick(self):
        """Per tick in S1 mode: the WM-3 path, the capture result, the --s1-script scheduler."""
        if not self.in_world():
            return
        if self.in_world_t is None:
            self.in_world_t = time.monotonic()
        if self.cam.path:
            lab = self.cam.path["label"]
            if self.cam.advance(self.tick) == "done":
                self.emit("s1.cmd", "S1 de.path done", cmd="de.path", label=lab, phase="done", tick=self.tick)
        if self.cam.motion and self.cam.motion_advance(self.tick) == "done":
            self.emit("s1.cmd", "S2 motion done", cmd="de.motion", label=self.cam.label, phase="done",
                      tick=self.tick)
        if self.s7census is not None and self.hello_acked:
            self.s7census.tick()   # S7 census camera path (--s7-census)
        self.capture_poll()
        a = self.a
        if a.s1_script and not self.script_started:
            waited = time.monotonic() - self.in_world_t
            if self.hello_acked or waited >= a.s1_script_wait:
                self.script_started = True
                import check_wm   # noqa: E402 (tests/s1: the S1 plan, single source of the steps)
                p = check_wm.plan(a.s1_quick)
                parts = None if a.s1_script == "all" else [x.strip() for x in a.s1_script.split(",") if x.strip()]
                self.script = collections.deque(check_wm.steps(p, parts, capture_prefix=a.s1_capture_prefix))
                self.script_next = time.monotonic() + 0.5
                self.emit("s1.script", "S1 script start (%d steps, helloAcked=%s)" % (len(self.script), self.hello_acked),
                          steps=len(self.script), parts=parts or "all", quick=a.s1_quick)
        if self.script is not None and time.monotonic() >= self.script_next:
            if not self.script:
                self.script = None
                self.emit("s1.script", "S1 script done")
                if a.s1_exit_after_script:
                    self.exit_at = time.monotonic() + 1.0
                return
            st = self.script.popleft()
            now = time.monotonic()
            if st["kind"] == "sn":            # Subnautica-side commands need the harness: not sent by the script
                self.script_next = now
                return
            if st["kind"] == "se":
                self.s1_command(st["cmd"], dict(st["args"]), None)
                self.script_next = now + st["wait"]
            elif st["kind"] == "hold":
                self.emit("s1.hold", label=st["label"], phase="begin", qpc=S.qpc())
                self.script.appendleft({"kind": "holdEnd", "label": st["label"]})
                self.script_next = now + st["seconds"]
            elif st["kind"] == "holdEnd":
                self.emit("s1.hold", label=st["label"], phase="end", qpc=S.qpc())
                self.script_next = now
            elif st["kind"] == "pixel":
                self.s1_command("de.pixel", {"aheadM": st["aheadM"], "label": st["label"]}, None)
                self.script_next = now + 0.1
            elif st["kind"] == "capture":
                self.capture_request(None, {"path": st["path"], "label": st["label"]})
                self.script_next = now + 0.5

    # ------------------------------------------------------------------------------------------------- records
    def write_c1(self):
        in_world = self.phase == L.SE_PHASE["InWorld"]
        ov = self.s5.c1_override() if self.s5 else None
        if ov is not None:              # S5 replay with a recorded C1: the recorded record, re-stamped
            S.seqlock_write(self.sec, L.REGION["C1"].offset, ov)
            return
        if self.cam:
            self.cam.publish(self.sec, self.tick, self.world_epoch, in_world, self.emit)
            return
        rec = L.SE_FRAME.pack(flags=(1 << L.SE_FRAME_FLAGS["inWorld"]) if in_world else 0, tick=self.tick,
                              qpcCapture=S.qpc(), simTimeSec=self.tick / 60.0, simSpeed=1.0,
                              worldEpoch=self.world_epoch, camPos=(0.0, 0.0, 0.0), camRot=(0.0, 0.0, 0.0, 1.0),
                              vFovDeg=60.0, near=0.05, aspect=16.0 / 9.0, cameraMode=1, charS=1.0)
        S.seqlock_write(self.sec, L.REGION["C1"].offset, rec)

    def write_c5(self):
        flags = 1 << L.SE_STATE_FLAGS["seForeground"]
        if self.phase == L.SE_PHASE["Exiting"]:
            flags |= 1 << L.SE_STATE_FLAGS["seExiting"]
        Z, PM = L.CONSTS["Zone"], L.CONSTS["PerfMode"]
        # the SE plugin: the camera's zone while the hello is published (S1), off-site otherwise; S1Perf: Full with
 # targetFps 0 in a dive zone, Idle with 10 otherwise
        site = bool(self.cam) and bool(self.hello_epoch) and self.phase == L.SE_PHASE["InWorld"]
        zone = self.cam.zone if site else Z["OffSite"]
        dive = zone in (Z["Core"], Z["TileVoid"], Z["OuterVoid"])
        s2 = {}
        if self.s2:
            s2 = self.s2.c5_fields()
            flags = (flags & (1 << L.SE_STATE_FLAGS["seExiting"])) | s2.pop("flags")
        rec = L.SE_STATE.pack(flags=flags, tick=self.tick, viewState=s2.pop("viewState", 0),
                              approachMode=s2.pop("approachMode", 0), displayMode=s2.pop("displayMode", 0),
                              overlays=self.overlays, zone=zone, worldEpoch=self.world_epoch,
                              perfMode=s2.pop("perfMode", PM["Full"] if dive else PM["Idle"]),
                              targetFps=s2.pop("targetFps", 0 if dive else 10),
                              snBusTarget=s2.pop("snBusTarget", 0.0), crossfadeMs=s2.pop("crossfadeMs", 300.0),
                              helloEpoch=self.hello_epoch, linkEpoch=self.link_epoch, Rs=L.RS_EXPECTED,
                              siteHash=self.site_hash, siteVersion=L.SITE_VERSION,
                              camSnY=self.cam.cam_sn_y if site else 0.0, **s2)
        S.seqlock_write(self.sec, L.REGION["C5"].offset, rec)

    def write_c6(self):
        rec = L.SE_TIME.pack(jumpSeq=0, tick=self.tick, hourAngleRad=0.0, dayLengthSec=1200.0, simSpeed=1.0,
                             sunriseHourAngleRad=1.5707963)
        S.seqlock_write(self.sec, L.REGION["C6"].offset, rec)

    def exit(self, why) -> int:
        if self.sec:
            self.set_phase(L.SE_PHASE["Exiting"])
            self.write_c5()
        if self.s5:
            self.s5.summary()
        if self.s6:
            self.s6.summary()
        if self.s3:
            self.emit("s3.summary", "DE.S3 stand-in summary: placed=%s written C3 %d / C4 %d, full C3 %d / C4 %d, gps "
                      "lines %d" % (bool(self.s3.placed), self.s3.written["C3"], self.s3.written["C4"],
                                    self.s3.full["C3"], self.s3.full["C4"], self.s3.gps_lines),
                      written=self.s3.written, full=self.s3.full, gpsLines=self.s3.gps_lines)
        self.emit("exit", "SE exiting (%s)" % why, c8Records=self.damage.records, c8Misparse=self.damage.misparse,
                  c8Stale=self.damage.stale, written=self.written, producerCorrupt=(self.c3p.corrupt + self.c4p.corrupt)
                  if self.sec else 0)
        if self.s2:
            self.s2.close()
        if self.sec:
            self.sec.close()
            self.sec = None
        if self.mutex:
            self.mutex.release()
            self.mutex = None
        return EXIT_OK

class _SelftestSe:
    """The few Se members S3aScene uses, for the self-test (no section, no peer)."""

    def __init__(self, a):
        self.a = a
        self.tick, self.world_epoch, self.link_epoch = 100, 1, 0
        self.phase = L.SE_PHASE["InWorld"]
        self.hello_acked, self.waiting_link_ack = True, None
        self.written, self.written_grids = {"C3": 0, "C4": 0}, set()
        self.events = []
        self.cam = S1Camera(self, a)

    def emit(self, ev, msg=None, **kw):
        self.events.append(dict(kw, ev=ev, msg=msg))

def s3a_selftest(a) -> int:
    """S3a self-test (stdlib; PIL only to cross-check the BC7 encoder when it is installed): the cube, the C3/C4 codecs
 through a private test section's byte rings, the winding measure with S3.M1/M2, the pose at the lab spot (Apply vs
 the exact map F within the tool's bound; the mirror-dropped error), the N2 rest geometry (SE GPS pixel vs the SN
 pinhole of the mirrored centre), the BC7/DDS writer and the real armor DDS headers (read in place)."""
    import struct
    import wire_ref
    results = []

    def check(ok, what, detail=""):
        results.append((bool(ok), what, detail))
        print("  %s %s%s" % ("PASS" if ok else "FAIL", what, (": " + detail) if detail else ""))

    print("S3a self-test (tests/standins/fake_se_producer.py --s3a-selftest)")
    # 1. the cube
    cube = s3_cube()
    vs, ts = cube["verts"], cube["tris"]
    check(len(vs) == 24 and len(ts) == 12, "cube: 24 vertices, 12 triangles", "%d / %d" % (len(vs), len(ts)))
    agree = sum(1 for t in ts if _v_dot(_v_cross(_v_sub(vs[t[1]]["pos"], vs[t[0]]["pos"]),
                                                  _v_sub(vs[t[2]]["pos"], vs[t[0]]["pos"])), vs[t[0]]["normal"]) > 0)
    check(agree == 12, "cube: cross(V1 - V0, V2 - V0) is the outward normal on every triangle (MyModel.Triangles)",
          "%d/12" % agree)
    worst_tb = 0.0
    for f in range(6):
        idx = [i for i, v in enumerate(vs) if v["face"] == f]
        v0, v1, v2 = vs[idx[0]], vs[idx[1]], vs[idx[2]]
        e1, e2 = _v_sub(v1["pos"], v0["pos"]), _v_sub(v2["pos"], v0["pos"])
        du1, dv1 = v1["uv"][0] - v0["uv"][0], v1["uv"][1] - v0["uv"][1]
        du2, dv2 = v2["uv"][0] - v0["uv"][0], v2["uv"][1] - v0["uv"][1]
        det = du1 * dv2 - du2 * dv1
        dpdu = _v_unit(_v_mul(_v_sub(_v_mul(e1, dv2), _v_mul(e2, dv1)), 1.0 / det))
        dpdv = _v_unit(_v_mul(_v_sub(_v_mul(e2, du1), _v_mul(e1, du2)), 1.0 / det))
        worst_tb = max(worst_tb, math.dist(dpdu, v0["tangent"]), math.dist(dpdv, v0["binormal"]),
                       abs(_v_dot(v0["tangent"], v0["normal"])), abs(_v_dot(v0["binormal"], v0["normal"])))
    check(worst_tb < 1e-9, "cube: tangent = dP/du, binormal = dP/dv, both orthogonal to the normal", "%.1e" % worst_tb)
    uvs = [v["uv"] for v in vs]
    check(min(u for u, _v in uvs) == 0.0 and max(u for u, _v in uvs) == 0.5 and min(v for _u, v in uvs) == 0.0 and
          max(v for _u, v in uvs) == 1.0, "cube: u over [0, 0.5] (the square half of the 2:1 atlas), v over [0, 1]")
    top = next(f for f in cube["faces"] if f["normal"][1] > 0.5)
    check(top["up"] == (0.0, 0.0, -1.0) and top["right"] == (1.0, 0.0, 0.0),
          "cube: top face reads upright looking down with SE Forward (-Z) at the top of the picture")
    # 2. winding measure (S3.C3) and its seeded failures
    base, m1, m2 = s3_winding_measure(vs, ts), s3_winding_measure(vs, ts, mirror_on=False), \
        s3_winding_measure(vs, ts, swap_on=False)
    check(base == 1.0, "C3 measure on the mirrored mesh with (I0, I2, I1)", "%.3f" % base)
    check(m1 == 0.0 and m2 == 0.0, "C3 measure red for S3.M1 (mirror dropped) and S3.M2 (swap dropped)",
          "M1 %.3f, M2 %.3f (an automatic flip would hide them: C3 is judged on the raw measure)" % (m1, m2))
    # 3. codecs
    mat_paths = ["C:/x/large_edges_cm.dds", "C:/x/large_edges_ng.dds", "C:/x/large_edges_add.dds", ""]
    mk, matk = s3_model_key(S3A_MODEL_PATH), s3_material_key(mat_paths, 0)
    msgs = [(L.C3_TYPES["MaterialDef"], s3_material_def(matk, mat_paths))] + s3_model_messages(mk, cube, matk,
                                                                                               chunk_bytes=500)
    payload = s3_model_payload(cube, matk)
    check(len(payload) == 24 * 56 + 36 * 4 + 24, "model payload 24 x 56 + 36 x 4 + 24 bytes", "%d" % len(payload))
    check(s3_model_key(S3A_MODEL_PATH.upper().replace("/", "\\")) == mk, "modelKey ignores case and slash style")
    # 4. through a private test section (the real Python byte ring)
    name = "Local\\DeepEngineers_s3a_selftest_%d" % S.current_pid()
    sec = None
    try:
        sec, _existed = S.Section.create(name, L.SECTION_SIZE, S.section_sddl_for_current_user())
    except Exception as e:      # noqa: BLE001 - reported, the codec part still runs in memory
        print("  note: no private section (%s); ring part skipped" % e)
    got = []
    if sec is not None:
        try:
            prod, cons = S.ByteRing(sec, "C3"), S.ByteRing(sec, "C3")
            for t, p in msgs:
                st = prod.try_write(t, p)
                if st != S.RW_OK:
                    raise RuntimeError("write %s" % st)
            while True:
                st, t, p = cons.try_read()
                if st != S.RR_OK:
                    break
                got.append((t, p))
        finally:
            sec.close()
        check(got == msgs, "C3 messages through the private section's byte ring, byte for byte",
              "%d messages (%d chunks)" % (len(got), len(msgs) - 3))
    rx = got or msgs
    mat = s3_decode_material(rx[0][1])
    check(mat["materialKey"] == matk and mat["paths"] == mat_paths and mat["drawTechnique"] == 0,
          "MaterialDef decodes: key, technique MESH, four strings in order (cm, ng, add, alpha mask)")
    begin = L.RECORDS["ModelBegin"].unpack(rx[1][1])
    body = b"".join(p[16:] for t, p in rx if t == L.C3_TYPES["ModelChunk"])
    end = L.RECORDS["ModelEnd"].unpack(rx[-1][1])
    check(begin["modelKey"] == mk and begin["vertexCount"] == 24 and begin["indexCount"] == 36 and
          begin["submeshCount"] == 1 and begin["flags"] == 2 and begin["totalBytes"] == len(body) and
          begin["chunkCount"] == len(rx) - 3, "ModelBegin fields (counts, HasTangents, chunkCount, totalBytes)")
    check(end["payloadHash"] == L.fnv1a64(body), "ModelEnd payloadHash = FNV-1a 64 of the concatenated chunks")
    dec = s3_decode_model(begin, body)
    same = all(math.dist(d["pos"], v["pos"]) < 1e-6 and math.dist(d["uv"], v["uv"]) < 1e-7 for d, v in zip(dec["verts"], vs))
    check(same and dec["tris"] == ts and dec["submeshes"][0]["materialKey"] == matk and
          dec["submeshes"][0]["triCount"] == 12, "model payload decodes back (positions, UVs, triangles, submesh)")
    # 5. the pose at the lab spot (deep spot of the sessions), camera at P1
    se = _SelftestSe(a)
    scene = S3aScene(se, a)
    lab = (-1029.0, -150.0, -1116.0)
    se.cam.teleport((lab[0], lab[1] + 0.6, lab[2] - 6.0), 0.0, 0.0, None, "P1")
    rep = scene.place({"sn": list(lab), "yawDeg": 30.0, "label": "selftest"})
    check(rep["verts"] == 24 and rep["tris"] == 12 and rep["parts"] == 1, "place: one part, producer counts 24 / 12")
    check(math.dist(rep["centreSn"], lab) < 1e-6, "place: F(block centre) is the lab point", "%.2e m" %
          math.dist(rep["centreSn"], lab))
    P = tuple(rep["pivot"])
    r = math.dist(P, se.cam.C)
    theta = math.hypot(lab[0], lab[2]) / se.cam.Rs
    aniso = theta / math.sin(theta) - 1.0
    worst, worst_ratio = 0.0, 0.0
    for c in scene.corners(0):
        d = math.dist(c[1], P)
        bound = d * d / (math.sqrt(3) * (r - d)) + aniso * rep["s"] * d
        err = math.dist(c[2], c[3])
        worst = max(worst, err)
        worst_ratio = max(worst_ratio, err / bound if bound > 0 else 0.0)
    check(worst < 1e-3 and worst_ratio <= 1.02, "C2 geometry: Apply(o, q, s) corners vs the exact F within 1 mm and the "
          "bound d^2/(sqrt3 r) + (theta/sin theta - 1) s d", "worst %.3e m, worst/bound %.3f" % (worst, worst_ratio))
    o, q, s = rep["o"], rep["q"], rep["s"]
    nomir = max(math.dist(wire_ref.add(o, wire_ref.scale_h(wire_ref.qrot(q, c[0]), s)), c[2]) for c in scene.corners(0))
    check(nomir > 2.0, "S3.M1 seen by C2 on index-matched corners (Apply without the mirror)", "%.3f m off" % nomir)
    o32, q32 = wire_ref.f32v(o), wire_ref.f32v(q)
    f32err = max(math.dist(wire_ref.sn_apply(o32, q32, wire_ref.f32(s), c[0]), c[2]) for c in scene.corners(0))
    check(f32err < 2e-4, "float32 wire pose vs the double pose at the corners", "%.2e m" % f32err)
    # 6. N2 at rest: the SE GPS marker (SE pinhole of the SE centre) vs Subnautica's pinhole of the mirrored centre
    cam = se.cam
    gps = cam.project_se(rep["centreSe"])["px"]
    cpos = cam.F(cam.T)
    crot = wire_ref.look_rotation(cam.mapdir(cam.T, cam.fwd), cam.mapdir(cam.T, cam.up))
    ctr = wire_ref.sn_apply(o, q, s, (0.0, 0.0, 0.0))
    c = wire_ref.qrot(wire_ref.qconj(crot), wire_ref.sub(ctr, cpos))
    k = (cam.H / 2.0) / math.tan(math.radians(cam.fov) / 2.0)
    snpx = (cam.W / 2.0 + k * c[0] / c[2], cam.H / 2.0 - k * c[1] / c[2])
    # the known limitation (ARCHITECTURE 5.4): SE's unscaled camera vs the world drawn with s horizontally puts a point
 # seen at elevation alpha about (s - 1)/2 sin 2 alpha rad off; the ROADMAP's rest threshold is 2 px
    dv = _v_sub(rep["centreSe"], cam.T)
    alpha = math.asin(max(-1.0, min(1.0, _v_dot(_v_unit(dv), cam.site.frame_at(cam.T)[1]))))
    pred = (s - 1.0) / 2.0 * math.sin(2.0 * abs(alpha)) * k
    off = math.dist(gps, snpx)
    check(off <= 2.0 and abs(off - pred) <= 0.25 * pred + 0.05, "N2 rest geometry: SE GPS pixel vs Subnautica's pixel "
          "of the mirrored centre within 2 px, as (s - 1)/2 sin 2 alpha predicts", "%.3f px, predicted %.3f px (alpha "
          "%.2f deg, s %.6f)" % (off, pred, math.degrees(alpha), s))
    px0 = scene.pixel({"corner": 0}, "c0")
    check(px0["sePx"] is not None and 0 <= px0["sePx"][0] <= cam.W and 0 <= px0["sePx"][1] <= cam.H,
          "N3: corner 0 in view at P1", "%s" % [round(x, 1) for x in px0["sePx"]])
    # 6b. the C2 publication (header + one SeEntity, same tick as C1) and S3.M4 (the pose of the previous tick)
    name2 = "Local\\DeepEngineers_s3a_selftest_c2_%d" % S.current_pid()
    try:
        sec2, _e2 = S.Section.create(name2, L.SECTION_SIZE, S.section_sddl_for_current_user())
    except Exception as e:      # noqa: BLE001
        sec2 = None
        print("  note: no private section for the C2 check (%s)" % e)
    if sec2 is not None:
        try:
            se.sec = sec2
            HDR, ENT = L.RECORDS["SeEntitiesHeader"], L.RECORDS["SeEntity"]

            def read_c2():
                st, _seq, data = S.seqlock_read(sec2, L.REGION["C2"].offset, HDR.size + ENT.size)
                return HDR.unpack(data), ENT.unpack(data, HDR.size), data[HDR.size:]
            results_c2 = {}
            for mut in (None, "M4"):
                a.s3_mutant = mut
                scene.prev_c2 = None
                se.cam.teleport((lab[0], lab[1] + 0.6, lab[2] - 6.0), 0.0, 0.0, None, "P1")
                se.tick = 200
                scene.write_c2()
                raw_a = read_c2()[2]
                se.cam.teleport((lab[0], lab[1] + 10.0, lab[2]), 0.0, -90.0, None, "P3")   # P* moves to the top face
                se.tick = 201
                scene.write_c2()
                h2, e2, raw_b = read_c2()
                results_c2[mut] = (h2, e2, raw_a, raw_b)
            a.s3_mutant = None
            h0, e0, ra0, rb0 = results_c2[None]
            h4, e4, ra4, rb4 = results_c2["M4"]
            P2, o2, q2, s2, _k2 = scene.compute_pose()
            check(h0["entityCount"] == 1 and h0["tick"] == 201 and e0["entityId"] == S3A_GRID_ID and e0["kind"] == 1 and
                  e0["flags"] == 1 and math.dist(e0["o"], o2) < 1e-3 and abs(e0["s"] - s2) < 1e-6 and e0["k"] == 1.0 and
                  rb0 != ra0, "C2: header tick = C1 tick, one static root grid, (o, q, s, k) of the current P* (the "
                  "record changes when P* moves)", "s %.9f -> %.9f" % (L.RECORDS["SeEntity"].unpack(ra0)["s"], e0["s"]))
            check(h4["tick"] == 201 and rb4 == ra0 and rb4 != rb0, "S3.M4 at the producer: C2 at tick 201 carries the "
                  "entity record of tick 200, byte for byte (stamped with the current tick)")
        finally:
            se.sec = None
            sec2.close()
    # 7. BC7 / DDS writer of the N1 maps
    blk = bc7_solid_block((200, 40, 40, 0))
    check(len(blk) == 16 and blk[0] & 0x7F == 0x40, "BC7 mode-6 block: 16 bytes, mode bits 0b1000000")
    dds = glyph_dds("cm", 64)
    exp = 148 + sum(((max(1, 64 >> m) + 3) // 4) ** 2 * 16 for m in range(7))
    check(len(dds) == exp, "glyph DDS: DX10 header + 7 BC7 mips of 64 px", "%d bytes" % len(dds))
    try:
        import io
        from PIL import Image  # noqa: E402 (cross-check only; the oracle itself is stdlib)
        im = Image.open(io.BytesIO(glyph_dds("cm", 64))).convert("RGBA")
        want = {(16, 16): S3A_GLYPH["tl"], (48, 16): S3A_GLYPH["tr"], (16, 48): S3A_GLYPH["bl"], (48, 48): S3A_GLYPH["br"]}
        bad = [(xy, im.getpixel(xy)[:3], rgb) for xy, rgb in want.items()
               if max(abs(a2 - b2) for a2, b2 in zip(im.getpixel(xy)[:3], rgb)) > 1]
        check(not bad, "glyph DDS decoded by Pillow %s: quadrant colours where the DirectX UVs put them (row 0 = v 0)"
              % getattr(__import__("PIL"), "__version__", "?"), "%s" % (bad or "4/4"))
        ng = Image.open(io.BytesIO(glyph_dds("ng", 64))).convert("RGBA")
        # 64 px: 8 px cells; cell (0, 0) U, (1, 0) D, (0, 2) R, (1, 2) L in every colour quadrant
        got = {k: ng.getpixel(((gx * 8 + 4), (gy * 8 + 4)))[:2] for k, (gx, gy) in
               {"U": (0, 0), "D": (1, 0), "R": (0, 2), "L": (1, 2), "U2": (5, 5), "D2": (4, 5)}.items()}
        good = (got["U"][1] > 160 and got["D"][1] < 96 and got["R"][0] > 160 and got["L"][0] < 96 and
                got["U2"][1] > 160 and got["D2"][1] < 96 and 112 < got["U"][0] < 144 and 112 < got["R"][1] < 144)
        check(good, "bump DDS: up/down cells in G (U > 0.5), right/left cells in R, checkers per colour quadrant",
              "%s" % got)
    except ImportError:
        print("  note: Pillow not installed: the BC7 decode cross-check is skipped (the oracle does not need it)")
    # 8. the real armor maps, read in place (headers only)
    for p, info in zip(scene.paths[:3], scene.dds):
        if not info.get("exists"):
            print("  note: %s not found (SE install?)" % p)
            continue
        check(info.get("dxgi") in (98, 99) and info.get("sizeMatches"), "SE map %s: %s %dx%d, %d mips, file size = "
              "header + BC7 chain" % (os.path.basename(p), info.get("format"), info.get("width", 0),
                                       info.get("height", 0), info.get("mips", 0)))
    want_srgb = [True, False, True]
    found = [i.get("srgb") for i in scene.dds if i.get("exists")]
    if len(found) == 3:
        check(found == want_srgb, "colour spaces as CONTRACT 2.5 expects: _cm sRGB, _ng linear, _add sRGB (as stored)",
              "%s" % found)
    n_fail = sum(1 for ok, _w, _d in results if not ok)
    print("S3A SELFTEST %s (%d checks, %d failed)" % ("PASS" if not n_fail else "FAIL", len(results), n_fail))
    return 0 if not n_fail else 1

class _S5Consumer:
    """The Subnautica side's bookkeeping of the S5 stream for the self-test: models (Begin, chunks, End with the payload
 hash), materials, grids, parts per grid, recolours; every inconsistency is an error."""

    def __init__(self, sec):
        self.c3, self.c4 = S.ByteRing(sec, "C3"), S.ByteRing(sec, "C4")
        self.models, self.open, self.materials = {}, {}, set()
        self.grids, self.parts, self.recolors, self.errors = {}, {}, 0, []
        self.part_models = {}
        self.removes = 0

    def drain(self):
        R = L.RECORDS
        while True:
            st, t, p = self.c3.try_read()
            if st != S.RR_OK:
                break
            if t == L.C3_TYPES["ModelBegin"]:
                v = R["ModelBegin"].unpack(p)
                self.open[v["modelKey"]] = (v, bytearray())
            elif t == L.C3_TYPES["ModelChunk"]:
                h = R["ModelChunkHeader"].unpack(p)
                if h["modelKey"] not in self.open:
                    self.errors.append("chunk without begin")
                else:
                    self.open[h["modelKey"]][1].extend(p[16:16 + h["bytes"]])
            elif t == L.C3_TYPES["ModelEnd"]:
                e = R["ModelEnd"].unpack(p)
                v, body = self.open.pop(e["modelKey"], (None, b""))
                if v is None or L.fnv1a64(bytes(body)) != e["payloadHash"] or len(body) != v["totalBytes"]:
                    self.errors.append("model %x incomplete or hash" % e["modelKey"])
                else:
                    self.models[e["modelKey"]] = v
            elif t == L.C3_TYPES["MaterialDef"]:
                self.materials.add(struct.unpack_from("<Q", p, 0)[0])
        while True:
            st, t, p = self.c4.try_read()
            if st != S.RR_OK:
                break
            if t == L.C4_TYPES["ClearAll"]:
                self.grids.clear()
                self.parts.clear()
            elif t == L.C4_TYPES["GridAdd"]:
                v = R["GridAdd"].unpack(p)
                self.grids[v["entityId"]] = v
                self.parts.setdefault(v["entityId"], set())
            elif t == L.C4_TYPES["EntityRemove"]:
                gid = struct.unpack_from("<Q", p, 0)[0]
                self.grids.pop(gid, None)
                self.parts.pop(gid, None)
            elif t == L.C4_TYPES["PartAdd"]:
                v = R["PartAdd"].unpack(p)
                if v["entityId"] not in self.grids:
                    self.errors.append("part %x before its grid" % v["partKey"])
                self.parts.setdefault(v["entityId"], set()).add(v["partKey"])
                self.part_models[(v["entityId"], v["partKey"])] = v["modelKey"]
                if (v["flags"] >> 28) != 1:
                    self.errors.append("part %x without Version1" % v["partKey"])
            elif t == L.C4_TYPES["PartRemove"]:
                gid, pk = struct.unpack_from("<QQ", p, 0)
                self.parts.get(gid, set()).discard(pk)
                self.removes += 1
            elif t == L.C4_TYPES["PartRecolor"]:
                self.recolors += 1

    def count(self, gid):
        return len(self.parts.get(gid, ()))

def s5_selftest(a) -> int:
    """S5 stand-in self-test (module docstring S5): the synthetic ship through a private section's byte rings under the
 S5 budgets, the C2 rows, the de.s5 commands and their SE rows, the edits, S5.M1/M3, and a recording replayed."""
    import tempfile
    X = _s5_stream()
    results = []

    def check(ok, what, detail=""):
        results.append(bool(ok))
        print("  %s %s%s" % ("PASS" if ok else "FAIL", what, (": " + detail) if detail else ""))

    print("S5 stand-in self-test (tests/standins/fake_se_producer.py --s5-selftest)")
    rec_dir = tempfile.mkdtemp(prefix="de-s5-rec-")
    X.RECORDINGS = rec_dir
    secs = []

    def section(tag):
        name = "Local\\DeepEngineers_s5_selftest_%s_%d" % (tag, S.current_pid())
        sec, _e = S.Section.create(name, L.SECTION_SIZE, S.section_sddl_for_current_user())
        secs.append(sec)
        return sec

    def make(a2, sec):
        se = _SelftestSe(a2)
        se.sec = sec
        se.c3p, se.c4p = S.ByteRing(sec, "C3"), S.ByteRing(sec, "C4")
        se.written = {"C3": 0, "C4": 0}

        def corrupt(ring):
            raise RuntimeError("producer ring %s corrupt" % ring.key)
        se._producer_corrupt = corrupt
        return se, S5Scene(se, a2)

    def run(se, scene, cons, ticks, until_idle=True, per_tick=None):
        heads = []
        for _ in range(ticks):
            se.tick += 1
            h0 = (se.c3p.producer_head, se.c4p.producer_head)
            scene.tick()
            scene.write_c2()
            scene.after_c1()
            heads.append((se.c3p.producer_head - h0[0], se.c4p.producer_head - h0[1]))
            cons.drain()
            if per_tick:
                per_tick(se.tick)
            if until_idle and not scene.pending():
                break
        return heads

    def c2(sec):
        blob = sec.read(L.REGION["C2"].offset, L.C2_SUBPARTS_OFFSET + 64 * 64)
        h = L.RECORDS["SeEntitiesHeader"].unpack(blob)
        ents = [L.RECORDS["SeEntity"].unpack(blob, 64 + 96 * i) for i in range(h["entityCount"])]
        subs = [L.RECORDS["SeSubpart"].unpack(blob, L.C2_SUBPARTS_OFFSET + 64 * i) for i in range(h["subpartCount"])]
        return h, ents, subs

    try:
        a.synth = a.synth or [500]
        sec = section("a")
        se, scene = make(a, sec)
        ship = scene.ship
        cons = _S5Consumer(sec)
        check(not ship.model_errors and len(ship.models) >= 200, "synth ship %d blocks built before the link: %d grids, "
              "%d parts, %d models" % (scene.size, len(ship.grids), len(ship.parts), len(ship.models)))
        se.cam.teleport((-1029.0, -150.0, -1116.0), 30.0, 0.0, None, "selftest")
        rep = scene.command("de.s5", {"action": "spawn", "sn": [-1029.0, -150.0, -1100.0], "yawDeg": 30.0}, 1)
        check(rep.get("ok"), "de.s5 spawn", json.dumps(rep)[:160] if not rep.get("ok") else "")
        heads = run(se, scene, cons, 2000)
        c3max = max(h[0] for h in heads)
        c4max = max(h[1] for h in heads)
        check(not scene.pending() and not cons.errors, "first send drained in %d ticks, consumer consistent" % len(heads),
              "; ".join(cons.errors[:3]))
        c3_1s = max(sum(h[0] for h in heads[i:i + 60]) for i in range(max(1, len(heads) - 59)))
        check(c4max <= S5_C4_BYTES_PER_TICK + 128 and c3_1s <= S5_C3_BYTES_PER_S + (1 << 20) + 64,
              "budgets: C4 max %d B per tick (<= 512 KiB), C3 max %d B in any 60 ticks (<= 16 MiB/s + one chunk)" % (
                  c4max, c3_1s))
        check(set(cons.grids) == set(ship.grids) and all(cons.count(g) == ship.counts(g)["parts"] for g in ship.grids),
              "consumer grids and parts per grid = the ship's (%s)" % ", ".join(
                  "%x:%d" % (g & 0xF, cons.count(g)) for g in sorted(ship.grids)))
        check(set(cons.models) >= {p.model for p in ship.parts.values()}, "every part's model ended (%d models)" %
              len(cons.models))
        sub = [g for g in cons.grids.values() if g["parentEntityId"]]
        check(len(sub) == 3 and all(g["flags"] & X.GRID_SUBGRID and (g["flags"] >> 28) == 1 for g in sub),
              "GridAdd: 3 subgrids flattened to the root, Subgrid flag and the version nibble")
        spawned = [e for e in se.events if e["ev"] == "s5.grid" and e.get("label") == "spawned"]
        check(len(spawned) == len(ship.grids), "s5.grid rows after the first send (%d)" % len(spawned))
        mrows = [e for e in se.events if e["ev"] == "s5.model"]
        check(len(mrows) >= len({p.model for p in ship.parts.values()}), "s5.model rows (%d)" % len(mrows))
        # C2: the root entity, the subgrid and dynamic subpart rows, the pose = wire_pose at the logged P*
        h, ents, subs = c2(sec)
        P, o, q, s_, k = scene.pose()
        check(h["tick"] == se.tick and h["entityCount"] == 1 and h["subpartCount"] == 3 + len(ship.dynamic_parts()),
              "C2 header: tick, 1 root entity, %d subpart rows" % h["subpartCount"])
        check(math.dist(ents[0]["o"], o) < 1e-3 and abs(ents[0]["s"] - s_) < 1e-6, "C2 root pose = wire_pose(M, P*)")
        rot = next(r for r in subs if r["partKey"] == X.SYNTH_ROTOR_ID)
        check(rot["ownerId"] == X.SYNTH_ROOT_ID, "subgrid row: ownerId = root, partKey = the subgrid id")
        # rotor at 10 rpm: one second turns the row 60 degrees; s5.sub rows carry the matrices
        scene.command("de.s5", {"action": "rotor", "rpm": 10}, 2)
        scene.command("de.s5", {"action": "sub", "on": True}, 3)
        _h, _e, s0 = c2(sec)
        r0 = next(r for r in s0 if r["partKey"] == X.SYNTH_ROTOR_ID)["local"]
        run(se, scene, cons, 60, until_idle=False)
        _h, _e, s1 = c2(sec)
        r1 = next(r for r in s1 if r["partKey"] == X.SYNTH_ROTOR_ID)["local"]
        ang = math.degrees(math.atan2(r1[2], r1[0]) - math.atan2(r0[2], r0[0])) % 360.0
        check(abs(ang - 60.0) < 0.01 or abs(ang - 300.0) < 0.01, "rotor row turns 60 deg in 60 ticks (%.4f)" % ang)
        srows = [e for e in se.events if e["ev"] == "s5.sub" and e.get("kind") == "rotor"]
        check(len(srows) >= 60 and all(len(r["toRoot"]) == 12 and r.get("seMatrix") for r in srows[-5:]),
              "s5.sub rotor rows every tick with toRoot and the root matrix (%d)" % len(srows))
        scene.command("de.s5", {"action": "rotor", "rpm": 0}, 4)
        scene.command("de.s5", {"action": "sub", "on": False}, 5)
        # 20 random parts (N1)
        rep = scene.command("de.s5", {"action": "parts", "n": 20, "seed": 3}, 6)
        prow = [e for e in se.events if e["ev"] == "s5.part"]
        check(rep.get("ok") and len(rep.get("parts", [])) == 20 and len(prow) == 20 and
              all(len(r["local"]) == 12 and len(r["seMatrix"]) == 12 and len(r["pivot"]) == 3 for r in prow),
              "de.s5 parts: 20 s5.part rows with rows, the root matrix and P*")
        # edits (N2): records drained within 2 ticks (class 2: within 50 m)
        se.cam.teleport((-1029.0, -150.0, -1110.0), 30.0, 0.0, None, "near")
        for kind in ("weld", "stage", "damage", "paint", "grind"):
            rc0 = cons.recolors
            rep = scene.command("de.s5", {"action": "edit", "kind": kind}, 7)
            n = len(run(se, scene, cons, 50))
            want_rec = kind == "paint"
            check(rep.get("ok") and n <= 2 and cons.count(X.SYNTH_ROOT_ID) == ship.counts(X.SYNTH_ROOT_ID)["parts"]
                  and (cons.recolors > rc0) == want_rec, "edit %s: records out in %d tick(s), part count %d = SE's" % (
                      kind, n, cons.count(X.SYNTH_ROOT_ID)))
        # S5.M3: PartRemove counted, not written -> the consumer keeps the block
        scene.command("de.mutant", {"id": "S5.M3", "on": True}, 8)
        r0 = cons.removes
        scene.command("de.s5", {"action": "edit", "kind": "grind"}, 9)
        run(se, scene, cons, 50)
        check(cons.removes == r0 and scene.written["removeSkipped"] >= 1 and
              cons.count(X.SYNTH_ROOT_ID) > ship.counts(X.SYNTH_ROOT_ID)["parts"],
              "S5.M3: PartRemove not written (%d skipped), Subnautica's count %d > SE's %d" % (
                  scene.written["removeSkipped"], cons.count(X.SYNTH_ROOT_ID), ship.counts(X.SYNTH_ROOT_ID)["parts"]))
        scene.command("de.mutant", {"id": "S5.M3", "on": False}, 10)
        # fly in the cockpit with the coherence point (N4): SE's own pixel of the dashboard point stays put
        scene.command("de.s5", {"action": "coh", "on": True}, 11)
        scene.command("de.s5", {"action": "fly", "speedMps": 20, "yawAmpDeg": 10, "yawHz": 0.5, "seconds": 2,
                                "cockpit": True}, 12)
        run(se, scene, cons, 60, until_idle=False)
        coh = [e["px"] for e in se.events if e["ev"] == "s5.coh" and e.get("px")]
        sx = (sum((p[0] - sum(c[0] for c in coh) / len(coh)) ** 2 for p in coh) / len(coh)) ** 0.5 if coh else None
        check(len(coh) >= 60 and sx is not None and sx < 1e-6 and se.cam.camera_mode == 3,
              "fly: camera in the cockpit (C1 cameraMode 3), SE's dashboard pixel fixed (std-dev %.2e px)" % (sx or -1))
        # S5.M1 at the producer: C2 publishes the previous tick's root pose
        scene.command("de.mutant", {"id": "S5.M1", "on": True}, 13)
        scene.command("de.s5", {"action": "fly", "speedMps": 20, "yawAmpDeg": 10, "yawHz": 0.5, "seconds": 1}, 14)
        o_hist = []
        run(se, scene, cons, 5, until_idle=False, per_tick=lambda t: o_hist.append((c2(sec)[1][0]["o"], scene.pose()[1])))
        lag_ok = all(math.dist(o_hist[i][0], o_hist[i - 1][1]) < 1e-3 and math.dist(o_hist[i][0], o_hist[i][1]) > 0.1
                     for i in range(2, len(o_hist)))
        check(lag_ok, "S5.M1 (producer): C2 carries the previous tick's root pose (20 m/s: %.3f m behind)" % (
            math.dist(o_hist[-1][0], o_hist[-1][1]) if o_hist else -1))
        scene.command("de.mutant", {"id": "S5.M1", "on": False}, 15)
        scene.command("de.s5", {"action": "coh", "on": False}, 16)
        run(se, scene, cons, 200, until_idle=False)
        # record, then replay the recording on a second private section
        rep = scene.command("de.s5", {"record": {"name": "selftest", "c1": False, "c2": True}}, 17)
        check(rep.get("ok"), "de.s5 record start (a resend so the recording stands alone)", str(rep.get("error", "")))
        run(se, scene, cons, 3000)
        run(se, scene, cons, 10, until_idle=False)
        rep = scene.command("de.s5", {"record": "stop"}, 18)
        side = rep.get("sidecar") or {}
        path = X.recording_path("selftest")
        probs = X.recording_problems(path) if os.path.exists(path) else ["missing"]
        check(rep.get("ok") and not probs and side.get("grids") == len(ship.grids) and side.get("parts", 0) >=
              len(ship.parts), "recording %s: %s" % (os.path.basename(path), json.dumps(side.get("counts"))),
              "; ".join(probs))
        want = {g: cons.count(g) for g in cons.grids}
        a2 = parse(["--replay", path, "--replay-pace", "recorded", "--name", "x"])
        sec2 = section("b")
        se2, scene2 = make(a2, sec2)
        cons2 = _S5Consumer(sec2)
        run(se2, scene2, cons2, 3000, until_idle=False,
            per_tick=None)
        h2, e2, s2 = c2(sec2)
        check(not cons2.errors and {g: cons2.count(g) for g in cons2.grids} == want,
              "replay: the consumer holds the recorded scene (%d grids, %d parts)" % (
                  len(cons2.grids), sum(cons2.count(g) for g in cons2.grids)), "; ".join(cons2.errors[:3]))
        check(h2["tick"] == se2.tick and h2["entityCount"] == 1 and h2["subpartCount"] == 3 + len(ship.dynamic_parts()),
              "replay: the recorded C2 re-stamped with the replayer's tick")
        check(scene2.recording["header"]["synthetic"] and scene2.recording["header"]["hasC2"],
              "replay header: synthetic, has C2")
    except Exception as e:     # noqa: BLE001 - a self-test reports, never raises
        import traceback
        traceback.print_exc()
        check(False, "self-test raised %s: %s" % (type(e).__name__, e))
    finally:
        for sec in secs:
            sec.close()
    ok = all(results)
    print("S5 SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def s6_selftest(a) -> int:
    """S6 stand-in self-test (module docstring S6): every synthetic hull through a private section's byte rings (C3
 models before their parts, GridAdd before the SkinSet, the skin decoded with the Python twin), the C2 rows (the loop
 at 5 m/s, the rotor's subgrid row turning), the resend versions (+1, then 1 after a ClearAll), the de.s6 commands
 and the C8 drain into s6.c8 rows (duplicates seen by the dedup twin)."""
    X = _s6_stream()
    results = []

    def check(ok, what, detail=""):
        results.append(bool(ok))
        print("  %s %s%s" % ("PASS" if ok else "FAIL", what, (": " + detail) if detail and not ok else ""))

    print("S6 stand-in self-test (tests/standins/fake_se_producer.py --s6-selftest)")
    name = "Local\\DeepEngineers_s6_selftest_%d" % S.current_pid()
    sec, _e = S.Section.create(name, L.SECTION_SIZE, S.section_sddl_for_current_user())
    try:
        se = _SelftestSe(a)
        se.sec = sec
        se.c3p, se.c4p = S.ByteRing(sec, "C3"), S.ByteRing(sec, "C4")
        se.written = {"C3": 0, "C4": 0}

        def corrupt(ring):
            raise RuntimeError("producer ring %s corrupt" % ring.key)
        se._producer_corrupt = corrupt
        scene = S6Scene(se, a)
        c3, c4 = S.ByteRing(sec, "C3"), S.ByteRing(sec, "C4")
        state = {"models": set(), "grids": {}, "parts": {}, "skins": {}, "errors": [], "clears": 0}

        def drain():
            R = L.RECORDS
            while True:
                st, t, p = c3.try_read()
                if st != S.RR_OK:
                    break
                if t == L.C3_TYPES["ModelEnd"]:
                    state["models"].add(R["ModelEnd"].unpack(p)["modelKey"])
            while True:
                st, t, p = c4.try_read()
                if st != S.RR_OK:
                    break
                if t == L.C4_TYPES["GridAdd"]:
                    v = R["GridAdd"].unpack(p)
                    state["grids"][v["entityId"]] = v
                elif t == L.C4_TYPES["PartAdd"]:
                    v = R["PartAdd"].unpack(p)
                    if v["modelKey"] not in state["models"]:
                        state["errors"].append("part before its model")
                    if v["entityId"] not in state["grids"]:
                        state["errors"].append("part before its grid")
                    state["parts"].setdefault(v["entityId"], set()).add(v["partKey"])
                elif t == L.C4_TYPES["SkinSet"]:
                    h, secs, err = X.decode_skin(p)
                    if err:
                        state["errors"].append("skin: " + err)
                    elif h["entityId"] not in state["grids"]:
                        state["errors"].append("SkinSet before its GridAdd")
                    else:
                        state["skins"][h["entityId"]] = (h["skinVersion"], secs)
                elif t == L.C4_TYPES["EntityRemove"]:
                    gid = struct.unpack_from("<Q", p, 0)[0]
                    state["grids"].pop(gid, None)
                    state["parts"].pop(gid, None)
                    state["skins"].pop(gid, None)
                elif t == L.C4_TYPES["ClearAll"]:
                    state["clears"] += 1
                    state["grids"].clear()
                    state["parts"].clear()
                    state["skins"].clear()

        def run(ticks):
            for _ in range(ticks):
                se.tick += 1
                scene.tick()
                scene.write_c2()
                drain()

        def c2():
            blob = sec.read(L.REGION["C2"].offset, L.C2_SUBPARTS_OFFSET + 64 * 8)
            h = L.RECORDS["SeEntitiesHeader"].unpack(blob)
            ents = [L.RECORDS["SeEntity"].unpack(blob, 64 + 96 * i) for i in range(h["entityCount"])]
            subs = [L.RECORDS["SeSubpart"].unpack(blob, L.C2_SUBPARTS_OFFSET + 64 * i) for i in range(h["subpartCount"])]
            return h, ents, subs

        se.cam.teleport((0.0, -6.0, -25.0), 0.0, 0.0, None, "selftest")
        for kind in X.HULL_KINDS:
            rep = scene.command("de.s6", {"action": "spawn", "skin": kind, "sn": [0.0, -8.0, 20.0]}, 1)
            if not rep.get("ok"):
                check(False, "de.s6 spawn %s" % kind, json.dumps(rep)[:200])
                continue
            run(30)
            hull = scene.hull
            want = {gid: len(g["boxes"]) for gid, g in hull.grids.items()} if hull.skinned else {}
            got = {gid: sum(len(s["boxes"]) for s in secs) for gid, (_v, secs) in state["skins"].items()}
            parts = sum(len(v) for g, v in state["parts"].items() if g in hull.grids)
            check(not state["errors"] and got == want and parts == len(hull.visual()) and not scene.pending(),
                  "%s: grids %d, parts %d, skins %s, versions %s" % (kind, len(hull.grids), parts, got, sorted(
                      v for v, _s in state["skins"].values())), "; ".join(state["errors"][:3]))
            if kind == "bt1":
                secs = state["skins"][hull.root][1]
                check(len(secs) == 2 and [len(s["boxes"]) for s in secs] == [30, 20] and
                      [s["hash"] for s in secs] == [s["hash"] for s in hull.sections(hull.root)],
                      "bt1 skin: 2 sections of 30 and 20 boxes, content hashes as built")
                _h, e0, _s = c2()
                o0 = e0[0]["o"]
                run(60)
                _h, e1, _s = c2()
                d = math.dist(o0, e1[0]["o"])
                check(abs(d - 5.0) < 0.15, "bt1 loop: the root moved %.3f m in 1 s (5 m/s on a 20 m circle)" % d)
                scene.command("de.s6", {"resend": "all"}, 2)
                run(3)
                check(state["skins"][hull.root][0] == 2, "de.s6 resend: skin version 2")
                scene.resend("selftest")
                run(30)
                check(state["clears"] >= 1 and state["skins"].get(hull.root, (0,))[0] == 1,
                      "resend after a ClearAll: skin version back to 1")
                rep = scene.command("de.s6", {"skin": "0x%016x" % hull.root, "rows": True}, 3)
                check(rep.get("ok") and rep["skins"][0]["sections"] == 2 and "boxes" in rep["skins"][0]["detail"][0],
                      "de.s6 {skin, rows}: the census with boxes")
            if kind == "rotor":
                _h, _e, s0 = c2()
                run(45)                      # 0.75 s at 10 rpm = 45 degrees
                _h, _e, s1 = c2()
                head = X.HULL_IDS["rotorHead"]
                ang = math.degrees(math.acos(max(-1.0, min(1.0, sum(s0[0]["local"][i] * s1[0]["local"][i]
                                                                     for i in range(3))))))
                check(s0 and s0[0]["partKey"] == head and abs(ang - 45.0) < 1.0,
                      "rotor: C2 subgrid row of the head turns %.2f degrees in 0.75 s" % ang)
                check(state["grids"][head]["parentEntityId"] == hull.root and state["grids"][head]["targetClass"] == 2,
                      "rotor head GridAdd: parent = root, targetClass 2 (small)")
        # C8 drain
        prod, cons = S.EntryRing(sec, "C8"), S.EntryRing(sec, "C8")
        seq = [0]

        def nxt():
            seq[0] += 1
            return seq[0]
        hull = scene.hull
        targets = [X.Target(gid, "grid", g["cls"], g["parent"], g["sections"]) for gid, g in sorted(hull.grids.items())
                   if hull.skinned]
        if not targets:
            hull = X.Hull("rotor")
            targets = [X.Target(gid, "grid", g["cls"], g["parent"], g["sections"]) for gid, g in sorted(hull.grids.items())]
        recs, plan = X.plan_n1(targets, 3, 1, nxt, seed=2, unique=20, dups=4)
        for rec, _m in recs:
            prod.try_write(rec)
        se.events.clear()
        while True:
            st, rec = cons.try_read()
            if st != S.RR_OK:
                break
            scene.on_c8(rec)
        rows = [e for e in se.events if e["ev"] == "s6.c8"]
        check(len(rows) == len(recs) and sum(1 for r in rows if r["dedup"] == "Duplicate") == 4,
              "C8 drain: %d s6.c8 rows, 4 seen as duplicates by the twin" % len(rows))
    except Exception as e:     # noqa: BLE001 - a self-test reports, never raises
        import traceback
        traceback.print_exc()
        check(False, "self-test raised %s: %s" % (type(e).__name__, e))
    finally:
        sec.close()
    ok = all(results) and bool(results)
    print("S6 SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def parse(argv):
    ap = argparse.ArgumentParser(description="Fake SE producer (S0 stand-in)")
    ap.add_argument("--name", default=L.SECTION_NAME)
    ap.add_argument("--tick-hz", type=float, default=60.0)
    ap.add_argument("--exit-poll-ms", type=float, default=0.0,
                    help="process-exit poll interval (0: every tick, as while margins are on; 1000: the Space cadence)")
    ap.add_argument("--mutant", choices=("no-exit-poll", "timeout30", "heartbeat-liveness") + S1_MUTANTS)
    ap.add_argument("--grids", type=int, default=8)
    ap.add_argument("--max-grids", type=int, default=200)
    ap.add_argument("--churn-ms", type=float, default=250.0, help="a new grid (+6 parts) every N ms (0: off)")
    ap.add_argument("--recolor-hz", type=float, default=10.0)
    ap.add_argument("--model-stream-ms", type=float, default=100.0, help="re-stream one model every N ms (0: off)")
    ap.add_argument("--load-after", type=float, default=0.2, help="Menu -> LoadingWorld after link-up")
    ap.add_argument("--load-seconds", type=float, default=0.3)
    ap.add_argument("--run-seconds", type=float, default=0.0)
    ap.add_argument("--give-up-after", type=float, default=0.0)
    ap.add_argument("--exit-after", type=float, default=0.0)
    ap.add_argument("--exit-mode", choices=("abrupt", "quit"), default="quit")
    ap.add_argument("--stall-after", type=float, default=0.0)
    ap.add_argument("--stall-for", type=float, default=5.0)
    ap.add_argument("--selftest-fail", action="store_true", help="validation step 6 fails (WM-1 self-check FAIL)")
    ap.add_argument("--exit-on-reject", action="store_true")
    ap.add_argument("--fast-retry", action="store_true", help="retry a rejected section every second")
    ap.add_argument("--quiet", action="store_true")
    ap.add_argument("--human", action="store_true")
    g = ap.add_argument_group("S1 (camera director)")
    g.add_argument("--s1", action="store_true", help="S1 camera director, S1 events and C11 test commands")
    g.add_argument("--s1-script", default="", help="run the scripted S1 steps by itself: all or a comma list of "
                                                  "wm2,hold,wm3,wm4,yaw,g1e5 (implies --s1)")
    g.add_argument("--s1-quick", action="store_true", help="the quick plan (short holds, faster WM-3 run)")
    g.add_argument("--s1-script-wait", type=float, default=30.0,
                   help="start the script after the hello ack, or after this many seconds in world")
    g.add_argument("--s1-exit-after-script", action="store_true")
    g.add_argument("--s1-capture-prefix", default="s1/standin", help="C10 capture path prefix (relative to captures)")
    g.add_argument("--s1-stream", action="store_true", help="keep the S0 C3/C4 test streaming in S1 mode")
    g.add_argument("--planet-centre", type=lambda s: tuple(float(x) for x in s.split(",")), default=None,
                   help="X,Y,Z of the planet centre C (default: site_v1.TEST_C)")
    g.add_argument("--screen", type=lambda s: tuple(int(x) for x in s.lower().split("x")), default=(2560, 1440))
    g.add_argument("--fov", type=float, default=70.0, help="configured vertical FOV in degrees")
    g = ap.add_argument_group("S2 (view state machine, display and audio emulation; see the module docstring)")
    g.add_argument("--s2", action="store_true", help="S2 stand-in (implies --s1)")
    g.add_argument("--s2-script", choices=("", "states"), default="", help="states: a fixed state timeline (no camera)")
    g.add_argument("--s2-mutant", choices=("M1", "M4"), default=None)
    g.add_argument("--s2-se-rect", type=lambda s: tuple(int(x) for x in s.split(",")), default=(0, 0, 2560, 1440),
                   help="C5 seRect x,y,w,h")
    g.add_argument("--s2-window", action="store_true", help="a real stand-in SE window (C0 seHwnd), off-screen")
    g.add_argument("--s2-window-rect", type=lambda s: tuple(int(x) for x in s.split(",")),
                   default=(-32000, -32000, 320, 180))
    g = ap.add_argument_group("S3a (the first block, stand-in SE side; see the module docstring S3a)")
    g.add_argument("--s3a", action="store_true", help="S3a scene: C3/C4 cube + MaterialDef, C2 static pose, de.s3 "
                                                     "(implies --s1)")
    g.add_argument("--s3a-selftest", action="store_true", help="the S3a geometry, record and texture self-test "
                                                               "(no peer needed; a private test section)")
    g.add_argument("--se-content", default=SE_CONTENT, help="SE Content folder (textures are read in place)")
    g.add_argument("--s3a-texture-set", choices=sorted(S3A_TEXTURE_SETS), default="large_edges")
    g.add_argument("--s3a-place-at", type=lambda s: tuple(float(x) for x in s.split(",")), default=None,
                   help="x,y,z (SN): place the block there once the hello is acknowledged (no de.s3 needed)")
    g.add_argument("--s3a-heading", type=float, default=30.0, help="grid Forward heading in SN degrees (0 = north)")
    g.add_argument("--s3a-glyph", action="store_true", help="a second block at grid cell (1, 0, 0) with the N1 glyph "
                                                            "and bump maps (written under %%LOCALAPPDATA%%)")
    g.add_argument("--s3a-hsv", type=lambda s: tuple(float(x) for x in s.split(",")), default=S3A_DEFAULT_HSV,
                   help="PartAdd colorMaskHSV h,s,v")
    g.add_argument("--s3a-chunk-bytes", type=int, default=1 << 20, help="model payload bytes per ModelChunk")
    g.add_argument("--s3-mutant", choices=("M4",), default=None,
                   help="S3.M4: the snapshot (C1 camera and C2 pose) published one tick late, stamped now")
    g = ap.add_argument_group("S5 (stream replayer / stand-in at scale; see the module docstring S5)")
    g.add_argument("--s5", action="store_true", help="S5 scene (implied by --replay / --synth; implies --s1)")
    g.add_argument("--replay", default=None, metavar="NAME|FILE|DIR",
                   help="replay a .derec recording (a name under %%LOCALAPPDATA%%/DeepEngineers/recordings, a file, or "
                        "the newest .derec of a folder) after the hello ack")
    g.add_argument("--replay-pace", choices=("recorded", "budget"), default="recorded",
                   help="recorded: messages at their recorded tick offsets; budget: as fast as the S5 budgets allow")
    g.add_argument("--no-replay-autostart", dest="replay_autostart", action="store_false",
                   help="wait for de.s5 {action: spawn} ... (replay starts at the hello ack by default)")
    g.add_argument("--synth", "--synth-ship", dest="synth", default=None,
                   type=lambda v: [int(x) for x in str(v).split(",") if x.strip()],
                   help="comma list of ship sizes in blocks (500,2000,5000): built from SE's models read in place before "
                        "the link; the first is spawned by --s5-spawn-at or de.s5 spawn")
    g.add_argument("--synth-seed", type=int, default=1)
    g.add_argument("--s5-spawn-at", type=lambda v: tuple(float(x) for x in v.split(",")), default=None,
                   help="x,y,z (SN) of the root grid origin: spawn at the hello ack without a command")
    g.add_argument("--s5-heading", type=float, default=30.0, help="ship Forward heading in SN degrees")
    g.add_argument("--s5-mutant", action="append", default=[], choices=("M1", "M3"),
                   help="M1: C2 one tick behind the camera (stand-in form of S5.M1); M3: PartRemove not written, counted "
                        "as sent (S5.M3); repeatable")
    g.add_argument("--s5-selftest", action="store_true", help="the S5 scene against a private test section: synth ship "
                                                              "stream through the byte rings, C2 rows, edits, M1/M3, "
                                                              "record and replay (no peer)")
    g = ap.add_argument_group("S6 (the B-T1 scene: a synthetic hull with its collision skin; module docstring S6)")
    g.add_argument("--s6", action="store_true", help="S6 scene (implied by --skin; implies --s1)")
    g.add_argument("--skin", default=None, metavar="synth:KIND",
                   help="synth:bt1 (20x6x6 m, 50 boxes, 2 sections, loop at 5 m/s) | synth:bt1k (the same envelope in "
                        "1,000 boxes) | synth:rotated (rotated boxes) | synth:hull100 (100 m, static) | synth:rotor "
                        "(a large root grid with a small-grid head at 10 rpm) | synth:none (the bt1 hull, no skin)")
    g.add_argument("--skin-spawn-at", type=lambda v: tuple(float(x) for x in v.split(",")), default=None,
                   help="x,y,z (SN): spawn at the hello ack without a command (the loop centre when moving)")
    g.add_argument("--skin-heading", type=float, default=0.0, help="Forward heading in SN degrees (static hulls)")
    g.add_argument("--skin-speed", type=float, default=None, help="loop speed m/s (default 5 for bt1/bt1k/none, else 0)")
    g.add_argument("--skin-radius", type=float, default=20.0, help="loop radius m")
    g.add_argument("--s6-selftest", action="store_true", help="the S6 scene against a private test section: every "
                                                              "hull through the rings, SkinSet decode, C2 rows, the "
                                                              "loop speed, resend versions, the C8 drain (no peer)")
    g = ap.add_argument_group("S7 statics census (tests/standins/s7_census_path.py)")
    g.add_argument("--s7-census", action="store_true", help="the census camera path: every batch centre for "
                                                            "--s7-census-dwell s (implies --s1)")
    g.add_argument("--s7-census-batches", default="all", help="all (every batch with a cache file in the Subnautica "
                                                              "install) or a file of bx,by,bz lines")
    g.add_argument("--s7-census-dwell", type=float, default=3.0, help="seconds per batch")
    g.add_argument("--s7-census-start", type=int, default=0, help="resume at this batch index")
    a = ap.parse_args(argv)
    if a.s7_census:
        a.s1 = True
    if a.skin or a.s6_selftest:
        a.s6 = True
    if a.s6:
        a.skin = (a.skin or "synth:bt1")
        kind = a.skin.replace("synth:", "")
        import s6_stream as _x
        if kind not in _x.HULL_KINDS:
            ap.error("--skin synth:%s unknown (known: %s)" % (kind, ", ".join(_x.HULL_KINDS)))
        if a.s5 or a.replay or a.synth or a.s3a or a.s5_selftest:
            ap.error("--s6 is its own scene: not with --s3a / --s5 / --synth / --replay (both would write C2)")
        a.s1 = True
    if a.s3a:
        a.s1 = True
    if a.replay or a.synth or a.s5_selftest:
        a.s5 = True
    if a.s5:
        a.s1 = True
        if not a.replay and not a.synth:
            a.synth = [500]
    if a.s2 or a.s2_script:
        a.s2 = True
        a.s1 = True
    if a.s1_script:
        a.s1 = True
    if a.mutant in S1_MUTANTS and not a.s1:
        ap.error("--mutant %s needs --s1" % a.mutant)
    return a

def main(argv=None) -> int:
    a = parse(sys.argv[1:] if argv is None else argv)
    if a.s3a_selftest:
        return s3a_selftest(a)
    if a.s5_selftest:
        return s5_selftest(a)
    if a.s6_selftest:
        return s6_selftest(a)
    S.fine_timer(True)
    se = Se(a)
    try:
        return se.run()
    except KeyboardInterrupt:
        return se.exit("interrupted") if se.sec else EXIT_OK
    finally:
        S.fine_timer(False)

if __name__ == "__main__":
    sys.exit(main())
