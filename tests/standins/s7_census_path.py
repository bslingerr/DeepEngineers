"""S7 census camera path for the fake SE producer (ROADMAP S7 "tools/tests": "the fake producer's census camera path";
`launcher.py s7 --census`): with --s7-census the stand-in's S1 camera visits every 160 m batch centre for --s7-census-dwell
seconds (default 3 s), so the real Subnautica plugin (census mode on through `de.statics {action: census, on: true}`,
sent by the launcher on C11c) streams each batch around the camera and writes the census cache
(%LOCALAPPDATA%\\DeepEngineers\\census\\batches\\<bx>.<by>.<bz>.c12).

Batches: --s7-census-batches all (default: every batch the Subnautica install has a BatchObjectsCache or CellsCache file
for, read-only directory listing; 2,975 batches with objects) or a text file with one batch per line ("bx,by,bz" or
"bx.by.bz"; '#' comments). The camera is placed at the batch centre (SN), heading 0, pitch -20; events `s7.census`
{phase: start | batch | done, index, total, batch, sn, dwellS}. Stdlib only.
"""
from __future__ import annotations

import glob
import os

LAND_ORIGIN = (-2048.0, -3040.0, -2048.0)   # LargeWorldStreamer land origin (S4 SnOctreeFormat.Origin*)
BATCH_M = 160.0
DEFAULT_SN = r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"

def batch_centre(b):
    return tuple(LAND_ORIGIN[i] + (b[i] + 0.5) * BATCH_M for i in range(3))

def parse_batch(text):
    t = text.strip().replace(".", ",").split(",")
    if len(t) != 3:
        raise ValueError("a batch is bx,by,bz: %r" % text)
    return tuple(int(x) for x in t)

def batches_from_install(sn_dir=None):
    """Every batch index with a BatchObjectsCache or CellsCache file (read-only listing)."""
    root = os.path.join(sn_dir or os.environ.get("DEEPENG_SUBNAUTICA", DEFAULT_SN), "Subnautica_Data", "StreamingAssets",
                        "SNUnmanagedData", "Build18")
    out = set()
    for folder, prefix in (("BatchObjectsCache", "batch-objects-"), ("CellsCache", "baked-batch-cells-")):
        for f in glob.glob(os.path.join(root, folder, prefix + "*.bin")):
            name = os.path.basename(f)[len(prefix):-4]
            try:
                out.add(tuple(int(x) for x in name.split("-")))
            except ValueError:
                continue
    return sorted(out)

def batches_from_file(path):
    out = []
    with open(path, "r", encoding="utf-8") as fh:
        for line in fh:
            line = line.split("#", 1)[0].strip()
            if line:
                out.append(parse_batch(line))
    return out

class S7CensusPath:
    """Moves the stand-in's S1 camera from batch centre to batch centre (one per dwell), emitting s7.census events."""

    def __init__(self, se, a, batches=None):
        self.se = se
        self.dwell_ticks = max(1, int(round(float(getattr(a, "s7_census_dwell", 3.0)) * float(getattr(a, "tick_hz", 60.0)))))
        spec = getattr(a, "s7_census_batches", "all") or "all"
        if batches is not None:
            self.batches = list(batches)
        elif spec == "all":
            self.batches = batches_from_install()
        else:
            self.batches = batches_from_file(spec)
        self.index = int(getattr(a, "s7_census_start", 0) or 0) - 1
        self.next_tick = None
        self.done = False

    def tick(self):
        if self.done:
            return
        se = self.se
        if self.next_tick is None:
            self.next_tick = se.tick
            se.emit("s7.census", "S7 census path: %d batch(es), %d ticks each" % (len(self.batches), self.dwell_ticks),
                    phase="start", total=len(self.batches), dwellTicks=self.dwell_ticks)
        if se.tick < self.next_tick:
            return
        self.index += 1
        if self.index >= len(self.batches):
            self.done = True
            se.emit("s7.census", "S7 census path done (%d batches)" % len(self.batches), phase="done", total=len(self.batches))
            return
        b = self.batches[self.index]
        sn = batch_centre(b)
        se.cam.teleport(sn, 0.0, -20.0, None, "s7census %d.%d.%d" % b)
        self.next_tick = se.tick + self.dwell_ticks
        se.emit("s7.census", None, phase="batch", index=self.index, total=len(self.batches), batch=list(b), sn=list(sn),
                dwellTicks=self.dwell_ticks)

def selftest():
    fails = []
    assert batch_centre((0, 0, 0)) == (-1968.0, -2960.0, -1968.0)
    if parse_batch("3,4,5") != (3, 4, 5) or parse_batch(" 3.4.5 ") != (3, 4, 5):
        fails.append("parse_batch")

    class _Cam:
        def __init__(self):
            self.moves = []

        def teleport(self, sn, h, p, fov, label):
            self.moves.append(sn)

    class _Se:
        def __init__(self):
            self.tick = 0
            self.cam = _Cam()
            self.events = []

        def emit(self, ev, msg=None, **kw):
            self.events.append(dict(kw, ev=ev))

    class _A:
        s7_census_dwell = 0.05
        tick_hz = 60.0
        s7_census_batches = "all"
        s7_census_start = 0

    se = _Se()
    path = S7CensusPath(se, _A(), batches=[(1, 2, 3), (4, 5, 6)])
    for t in range(20):
        se.tick = t
        path.tick()
    if se.cam.moves != [batch_centre((1, 2, 3)), batch_centre((4, 5, 6))]:
        fails.append("camera moves %r" % se.cam.moves)
    phases = [e["phase"] for e in se.events]
    if phases != ["start", "batch", "batch", "done"]:
        fails.append("phases %r" % phases)
    for f in fails:
        print("FAIL", f)
    print("PASS s7_census_path selftest" if not fails else "s7_census_path selftest: %d failure(s)" % len(fails))
    return 1 if fails else 0

if __name__ == "__main__":
    import sys
    sys.exit(selftest())
