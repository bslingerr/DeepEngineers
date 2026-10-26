"""Composed-desktop video capture and a black-frame detector (ROADMAP S2 tools: S2.N0 composed-capture tooling, the
S2.T-D1 "no black frame at any switch" check, S2.M1 "opaque"/calibration colour absent).

 python -I tools/capture/composed_video.py record --out DIR [--seconds 10] [--fps 165] [--method ddagrab|gdigrab]
 [--monitor 0] [--region x,y,w,h] [--probe centre=1280,720...] [--expect centre=255,0,255]
 [--keep-video] [--guard-processes Legacy,Subnautica] [--guard-title-prefix DE_]
 python -I tools/capture/composed_video.py analyze VIDEO [--probe...] [--out DIR] # a file recorded before
 python -I tools/capture/composed_video.py --find-ffmpeg
 python -I tools/capture/composed_video.py --selftest # lavfi synthetic videos (no desktop) + pure detector
 python -I tools/capture/composed_video.py --selftest-live # 1 s ddagrab of the primary monitor, statistics only

ffmpeg: `shutil.which("ffmpeg")`, else the environment variable DEEPENG_FFMPEG, else the copy `um win setup` installed
at %LOCALAPPDATA%\\universal-modder\\ffmpeg\\bin\\ffmpeg.exe. No pip package.

Capture (HYPOTHESIS method of ROADMAP S2.N0, settled by its first real run): `ddagrab` (Desktop Duplication: the
composed desktop, so DWM transparency shows as the player sees it) with dup_frames=0, so a frame is produced only when
the desktop changed (the frame times are composition times), or `gdigrab` (GDI, constant rate). One ffmpeg process
decodes nothing to disk unless --keep-video: the filter graph splits every captured frame into (1) a 128x72 grayscale
thumbnail streamed to this script (per frame: mean luma, black fraction, a change flag against the previous frame),
(2) the probe pixels (1x1 crops at full resolution, RGB) written to a small raw file, and (3) with --keep-video a
half-size H.264 file for a human to watch. `showinfo` gives each frame's PTS. The PTS clock is recorded: when it
matches QPC seconds (ffmpeg's monotonic clock on Windows) frames can be aligned with the plugins' QPC stamps directly
(`ptsClock: "qpc"`); ddagrab's PTS start at 0, so every thumbnail is
also stamped with the QPC at which it reaches this script and each frame gets qpcEst = pts + min(qpcRecv - pts): an
upper-bound alignment (later than the true capture by the minimum pipeline delay), enough to bound "skip frame ->
composed" from above.

Black frame (S2.T-D1): >= 98 % of the thumbnail pixels at luma <= 4 (of 255). A cleared backbuffer composed without
the DWM margins is pure black (T-D0 control: (0,0,0)); a deep, dark Subnautica scene is not (dark navy reads 20-40).
Both numbers are options. Results: <out>/video-frames.jsonl (one line per frame), <out>/video-summary.json.

Privacy guard: with --guard-processes / --guard-title-prefix the region's centre and corners are hit-tested every
second while recording; a window of another process there sets guardViolation, and the kept video (if any) is deleted;
the thumbnails are never stored as images.
Exit codes: 0 done (selftest PASS); 1 black frames found, an --expect probe mismatched, or selftest FAIL; 2 ffmpeg
missing; 3 guard refused the start; 4 usage.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time

THUMB_W, THUMB_H = 128, 72
BLACK_LUMA = 4
BLACK_FRACTION = 0.98
_SHOWINFO = re.compile(r"\bn:\s*(\d+)\s+pts:\s*(-?\d+)\s+pts_time:\s*(-?[0-9.]+)")

def find_ffmpeg():
    p = shutil.which("ffmpeg")
    if p:
        return p
    env = os.environ.get("DEEPENG_FFMPEG")
    if env and os.path.exists(env):
        return env
    um = os.path.join(os.environ.get("LOCALAPPDATA", ""), "universal-modder", "ffmpeg", "bin", "ffmpeg.exe")
    return um if os.path.exists(um) else None

def qpc():
    if os.name != "nt":
        return int(time.perf_counter() * 1e7), 10_000_000
    c, f = ctypes.c_int64(), ctypes.c_int64()
    ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(c))
    ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(f))
    return c.value, f.value

# --------------------------------------------------------------------------------------------------- frame statistics
_BLACK_TABLES = {}

def _black_table(thr):
    t = _BLACK_TABLES.get(thr)
    if t is None:
        t = bytes(1 if i <= thr else 0 for i in range(256))
        _BLACK_TABLES[thr] = t
    return t

def frame_stats(gray: bytes, prev: bytes = None, black_luma=BLACK_LUMA, black_fraction=BLACK_FRACTION) -> dict:
    """Stats of one grayscale thumbnail (bytes, one per pixel)."""
    n = len(gray) or 1
    dark = gray.translate(_black_table(black_luma)).count(1)
    frac = dark / n
    return {"meanLuma": round(sum(gray) / n, 2), "blackFraction": round(frac, 4), "black": frac >= black_fraction,
            "changed": prev is None or gray != prev}

def parse_probes(specs):
    out = []
    for i, s in enumerate(specs or ()):
        name, _, xy = s.partition("=") if "=" in s else ("p%d" % i, "", s)
        x, y = (int(v) for v in xy.split(","))
        out.append((name, x, y))
    return out

def parse_expect(specs):
    out = {}
    for s in specs or ():
        name, _, rgb = s.partition("=")
        out[name] = tuple(int(v) for v in rgb.split(","))
    return out

# ------------------------------------------------------------------------------------------------------------ guard
def _guard_check(points, procs, prefix):
    if os.name != "nt" or (not procs and not prefix):
        return True, []
    u = ctypes.windll.user32
    u.WindowFromPoint.restype = ctypes.c_void_p
    u.GetAncestor.restype = ctypes.c_void_p
    u.GetAncestor.argtypes = [ctypes.c_void_p, ctypes.c_uint]
    bad = []
    for (x, y) in points:
        pt = ctypes.c_int64((y << 32) | (x & 0xFFFFFFFF))
        h = u.GetAncestor(u.WindowFromPoint(pt), 2)
        pid = ctypes.c_ulong()
        u.GetWindowThreadProcessId(ctypes.c_void_p(h), ctypes.byref(pid))
        title = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(ctypes.c_void_p(h), title, 256)
        name = _image(pid.value)
        ok = (prefix and title.value.startswith(prefix)) or (procs and name.lower() in procs)
        if not ok:
            bad.append({"x": x, "y": y, "pid": pid.value, "process": name})
    return not bad, bad

def _image(pid):
    k = ctypes.windll.kernel32
    h = k.OpenProcess(0x1000, False, pid)
    if not h:
        return "?"
    try:
        buf = ctypes.create_unicode_buffer(520)
        n = ctypes.c_ulong(520)
        if k.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
            return os.path.basename(buf.value)
        return "?"
    finally:
        k.CloseHandle(h)

def _monitor_rect(idx):
    """(x, y, w, h) of monitor `idx` in EnumDisplayMonitors order (0 is usually the primary)."""
    rects = []
    if os.name == "nt":
        u = ctypes.windll.user32

        class RECT(ctypes.Structure):
            _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long), ("r", ctypes.c_long), ("b", ctypes.c_long)]
        CB = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(RECT), ctypes.c_void_p)

        def cb(_h, _dc, r, _lp):
            rects.append((r[0].l, r[0].t, r[0].r - r[0].l, r[0].b - r[0].t))
            return 1
        u.EnumDisplayMonitors(None, None, CB(cb), 0)
    return rects[idx] if idx < len(rects) else (0, 0, 2560, 1440)

# ----------------------------------------------------------------------------------------------------------- ffmpeg
def build_graph(probes, region_wh, keep_video, src_prefix):
    """Filter graph: src -> showinfo -> split -> thumbnail gray (out 'thumb'), probes Nx1 rgb24 ('probes'), video."""
    n = 2 + (1 if keep_video else 0) + (0 if probes else -1)
    parts = ["[0:v]%sshowinfo,split=%d[ta]%s%s" % (src_prefix, n, "[pa]" if probes else "", "[va]" if keep_video else "")]
    parts.append("[ta]scale=%d:%d:flags=area,format=gray[thumb]" % (THUMB_W, THUMB_H))
    if probes:
        w, h = region_wh
        crops = []
        for i, (_n, x, y) in enumerate(probes):
            x, y = min(max(0, x), w - 1), min(max(0, y), h - 1)
            crops.append("[p%d]format=rgb24,crop=1:1:%d:%d[c%d]" % (i, x, y, i))
        if len(probes) == 1:
            parts.append("[pa]" + crops[0].split("]", 1)[1].replace("[c0]", "[probes]"))
        else:
            parts.append("[pa]split=%d%s" % (len(probes), "".join("[p%d]" % i for i in range(len(probes)))))
            parts.extend(crops)
            parts.append("%shstack=inputs=%d[probes]" % ("".join("[c%d]" % i for i in range(len(probes))), len(probes)))
    if keep_video:
        parts.append("[va]scale=trunc(iw/4)*2:trunc(ih/4)*2,format=yuv420p[vid]")
    return ";".join(parts)

class Capture:
    """One ffmpeg run (live capture or a file) analysed frame by frame."""

    def __init__(self, ffmpeg, out_dir, probes=(), expect=None, black_luma=BLACK_LUMA, black_fraction=BLACK_FRACTION):
        self.ffmpeg, self.out = ffmpeg, out_dir
        self.probes = list(probes)
        self.expect = expect or {}
        self.black_luma, self.black_fraction = black_luma, black_fraction
        self.pts = []
        self.stderr_tail = []

    def _stderr(self, pipe):
        for raw in iter(pipe.readline, b""):
            line = raw.decode("utf-8", "replace").rstrip()
            m = _SHOWINFO.search(line)
            if m:
                self.pts.append(float(m.group(3)))
            elif line:
                self.stderr_tail.append(line)
                del self.stderr_tail[:-40]

    def run(self, input_args, region_xywh, seconds=None, keep_video=False, src_prefix="", guard=None):
        os.makedirs(self.out, exist_ok=True)
        probes_raw = os.path.join(self.out, "video-probes.raw")
        video = os.path.join(self.out, "video.mp4") if keep_video else None
        rel = [(n, x - region_xywh[0], y - region_xywh[1]) for (n, x, y) in self.probes]
        graph = build_graph(rel, region_xywh[2:], keep_video, src_prefix)
        cmd = [self.ffmpeg, "-hide_banner", "-nostats", "-loglevel", "info", "-y"] + input_args
        if seconds:
            cmd += ["-t", "%.3f" % seconds]
        cmd += ["-filter_complex", graph, "-map", "[thumb]", "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1"]
        if self.probes:
            cmd += ["-map", "[probes]", "-fps_mode", "passthrough", "-f", "rawvideo", probes_raw]
        if keep_video:
            cmd += ["-map", "[vid]", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", video]
        q0, qf = qpc()
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, stdin=subprocess.DEVNULL,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        t_err = threading.Thread(target=self._stderr, args=(proc.stderr,), daemon=True)
        t_err.start()
        guard_bad = []
        stop_guard = threading.Event()
        if guard:
            def gl():
                while not stop_guard.wait(1.0):
                    ok, bad = guard()
                    if not ok:
                        guard_bad.extend(bad)
            threading.Thread(target=gl, daemon=True).start()
        size = THUMB_W * THUMB_H
        frames = []
        prev = None
        fpath = os.path.join(self.out, "video-frames.jsonl")
        with open(fpath, "w", encoding="utf-8") as f:
            while True:
                buf = proc.stdout.read(size)
                if not buf or len(buf) < size:
                    break
                st = frame_stats(buf, prev, self.black_luma, self.black_fraction)
                st["n"] = len(frames)
                st["qpcRecv"] = qpc()[0]
                frames.append(st)
                prev = buf
        rc = proc.wait()
        t_err.join(2.0)
        stop_guard.set()
        probes = self._read_probes(probes_raw, len(frames))
        with open(fpath, "w", encoding="utf-8") as f:
            for i, st in enumerate(frames):
                st["pts"] = self.pts[i] if i < len(self.pts) else None
                if probes:
                    st["probes"] = probes[i]
                f.write(json.dumps(st) + "\n")
        if guard_bad and video and os.path.exists(video):
            os.remove(video)
            video = None
        return self.summary(frames, rc, q0, qf, cmd, video, guard_bad)

    def _read_probes(self, path, nframes):
        if not self.probes or not os.path.exists(path):
            return None
        data = open(path, "rb").read()
        k = 3 * len(self.probes)
        out = []
        for i in range(min(nframes, len(data) // k)):
            row = data[i * k:(i + 1) * k]
            out.append({name: [row[3 * j], row[3 * j + 1], row[3 * j + 2]] for j, (name, _x, _y) in enumerate(self.probes)})
        return out

    def summary(self, frames, rc, q0, qf, cmd, video, guard_bad):
        pts = [f["pts"] for f in frames if f.get("pts") is not None]
        dur = (pts[-1] - pts[0]) if len(pts) > 1 else 0.0
        q0s = q0 / float(qf)
        clock = "qpc" if pts and abs(pts[0] - q0s) < 5.0 else "relative"
        # PTS -> QPC: each thumbnail is stamped when it reaches this script (qpcRecv = capture time + pipeline delay);
 # offset = min(qpcRecv - pts), so qpcEst = pts + offset is at most the minimum pipeline delay LATER than the
 # true capture (a conservative, upper-bound alignment with the plugins' QPC stamps)
        pairs = [(f["qpcRecv"], f["pts"]) for f in frames if f.get("pts") is not None and f.get("qpcRecv")]
        offset = min(r - int(p_ * qf) for r, p_ in pairs) if pairs else None
        if offset is not None:
            for f in frames:
                if f.get("pts") is not None:
                    f["qpcEst"] = int(f["pts"] * qf) + offset
            fpath = os.path.join(self.out, "video-frames.jsonl")
            with open(fpath, "w", encoding="utf-8") as fh:
                for f in frames:
                    fh.write(json.dumps(f) + "\n")
        black = [{"n": f["n"], "pts": f.get("pts"), "blackFraction": f["blackFraction"]} for f in frames if f["black"]]
        gaps = [b - a for a, b in zip(pts, pts[1:])]
        exp = {}
        for name, rgb in self.expect.items():
            vals = [f["probes"][name] for f in frames if f.get("probes") and name in f["probes"]]
            hit = sum(1 for v in vals if all(abs(a - b) <= 6 for a, b in zip(v, rgb)))
            exp[name] = {"expected": list(rgb), "frames": len(vals), "matching": hit,
                         "fraction": round(hit / len(vals), 4) if vals else None}
        # S2.N0: two frames about 1 s apart differ (Subnautica animates under the see-through area)
        differs = None
        if len(pts) > 1 and dur >= 1.0:
            j = next((i for i, p in enumerate(pts) if p - pts[0] >= 1.0), None)
            if j is not None:
                differs = frames[0].get("meanLuma") != frames[j].get("meanLuma") or frames[j]["changed"]
        s = {"tool": "composed_video", "frames": len(frames), "durationS": round(dur, 3),
             "fps": round((len(pts) - 1) / dur, 2) if dur > 0 else None, "ptsClock": clock,
             "qpcAtStart": q0, "qpcFrequency": qf, "firstPts": pts[0] if pts else None, "ptsToQpcOffset": offset,
             "alignment": "qpcEst = pts * qpcFrequency + ptsToQpcOffset (upper bound: later than the capture by the "
                          "minimum pipeline delay)",
             "blackFrames": black, "blackCount": len(black), "blackLuma": self.black_luma,
             "blackFractionThreshold": self.black_fraction, "maxFrameGapMs": round(max(gaps) * 1000, 2) if gaps else None,
             "changedFrames": sum(1 for f in frames if f["changed"]), "differs1s": differs, "expect": exp,
             "video": video, "guardViolations": guard_bad[:20], "ffmpegRc": rc, "ffmpegTail": self.stderr_tail[-8:],
             "command": subprocess.list2cmdline(cmd)}
        with open(os.path.join(self.out, "video-summary.json"), "w", encoding="utf-8") as f:
            json.dump(s, f, indent=1)
        return s

def record(a) -> int:
    ff = find_ffmpeg()
    if not ff:
        print("composed_video: ffmpeg not found (PATH, DEEPENG_FFMPEG, %LOCALAPPDATA%\\universal-modder\\ffmpeg\\bin)")
        return 2
    region = tuple(int(v) for v in a.region.split(",")) if a.region else _monitor_rect(a.monitor)
    x, y, w, h = region
    procs = [p.strip().lower() for p in (a.guard_processes or "").split(",") if p.strip()]
    procs = [p if p.endswith(".exe") else p + ".exe" for p in procs]
    pts = [(x + w // 2, y + h // 2), (x + 8, y + 8), (x + w - 9, y + 8), (x + 8, y + h - 9), (x + w - 9, y + h - 9)]
    guard = (lambda: _guard_check(pts, procs, a.guard_title_prefix)) if (procs or a.guard_title_prefix) else None
    if guard:
        ok, bad = guard()
        if not ok:
            print(json.dumps({"refused": True, "why": "guard: a foreign window lies in the region", "points": bad}))
            return 3
    if a.method == "ddagrab":
        mon_x, mon_y, _mw, _mh = _monitor_rect(a.monitor)
        src = "ddagrab=output_idx=%d:framerate=%g:draw_mouse=0:dup_frames=0" % (a.monitor, a.fps)
        if a.region:
            src += ":offset_x=%d:offset_y=%d:video_size=%dx%d" % (x - mon_x, y - mon_y, w, h)
        inp, prefix = ["-f", "lavfi", "-i", src], "hwdownload,format=bgra,"
    else:
        inp = ["-f", "gdigrab", "-framerate", "%g" % a.fps, "-draw_mouse", "0", "-offset_x", str(x), "-offset_y", str(y),
               "-video_size", "%dx%d" % (w, h), "-i", "desktop"]
        prefix = ""
    cap = Capture(ff, a.out, parse_probes(a.probe), parse_expect(a.expect), a.black_luma, a.black_fraction)
    s = cap.run(inp, region, a.seconds, a.keep_video, prefix, guard)
    s["method"], s["region"] = a.method, list(region)
    with open(os.path.join(a.out, "video-summary.json"), "w", encoding="utf-8") as f:
        json.dump(s, f, indent=1)
    print(json.dumps({k: s[k] for k in ("frames", "durationS", "fps", "ptsClock", "blackCount", "maxFrameGapMs",
                                        "differs1s", "expect", "guardViolations", "ffmpegRc")}))
    bad_exp = any(e["frames"] and e["fraction"] is not None and e["fraction"] < a.expect_fraction for e in s["expect"].values())
    return 1 if s["blackCount"] or bad_exp else 0

def analyze(a) -> int:
    ff = find_ffmpeg()
    if not ff:
        print("composed_video: ffmpeg not found")
        return 2
    out = a.out or os.path.dirname(os.path.abspath(a.video))
    probe = json.loads(subprocess.run([ff.replace("ffmpeg.exe", "ffprobe.exe") if ff.endswith("ffmpeg.exe") else "ffprobe",
                                       "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
                                       "-of", "json", a.video], capture_output=True, text=True).stdout or "{}")
    st = (probe.get("streams") or [{}])[0]
    region = (0, 0, int(st.get("width", 0) or 0), int(st.get("height", 0) or 0))
    cap = Capture(ff, out, parse_probes(a.probe), parse_expect(a.expect), a.black_luma, a.black_fraction)
    s = cap.run(["-i", a.video], region, None, False, "")
    print(json.dumps({k: s[k] for k in ("frames", "durationS", "fps", "blackCount", "differs1s", "expect")}))
    return 1 if s["blackCount"] else 0

# ----------------------------------------------------------------------------------------------------------- selftest
def selftest(live=False) -> int:
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-70s %s %s" % (label, "PASS" if cond else "FAIL", detail))
    # pure detector
    black = bytes(THUMB_W * THUMB_H)
    navy = bytes([24]) * (THUMB_W * THUMB_H)
    grey = bytes([128]) * (THUMB_W * THUMB_H)
    hud = bytearray(black)
    hud[:int(len(hud) * 0.015)] = bytes([255]) * int(len(hud) * 0.015)
    check("detector: pure black thumbnail -> black", frame_stats(black)["black"])
    check("detector: dark navy (luma 24, deep water) -> not black", not frame_stats(navy)["black"])
    check("detector: black with a 1.5 % HUD -> still black (cleared backbuffer + HUD)", frame_stats(bytes(hud))["black"])
    check("detector: grey -> not black, change flag vs black", not frame_stats(grey)["black"] and frame_stats(grey, black)["changed"])
    check("detector: identical frames -> unchanged", not frame_stats(grey, grey)["changed"])
    check("seeded failure: threshold luma 30 calls the navy frame black (why the default is 4)",
          frame_stats(navy, black_luma=30)["black"])
    ff = find_ffmpeg()
    check("ffmpeg found", ff is not None, ff or "")
    if ff:
        tmp = tempfile.mkdtemp(prefix="de_cv_")
        try:
            # 2 s of a calibration colour at 60 fps with 4 black frames (30, 31, 32, 90): exactly those must be found
            src = ("color=c=0xFF00FF:s=320x180:r=60:d=2,drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:"
                   "enable='between(n\\,30\\,32)+eq(n\\,90)'")
            cap = Capture(ff, os.path.join(tmp, "a"), [("centre", 160, 90)], {"centre": (255, 0, 255)})
            s = cap.run(["-f", "lavfi", "-i", src], (0, 0, 320, 180))
            got = [b["n"] for b in s["blackFrames"]]
            check("lavfi: 120 frames decoded with PTS", s["frames"] == 120 and s["firstPts"] is not None, "%d frames, fps %s" % (s["frames"], s["fps"]))
            check("lavfi: black frames found at exactly 30, 31, 32, 90", got == [30, 31, 32, 90], str(got))
            e = s["expect"]["centre"]
            check("lavfi: probe = calibration colour on the 116 other frames", e["matching"] == 116, json.dumps(e))
            # dark scene (no black frame) and an animated scene (differs after 1 s)
            cap = Capture(ff, os.path.join(tmp, "b"))
            s = cap.run(["-f", "lavfi", "-i", "color=c=0x0A1428:s=320x180:r=60:d=1.5"], (0, 0, 320, 180))
            check("lavfi: dark navy scene -> 0 black frames, frozen (differs1s false)",
                  s["frames"] == 90 and s["blackCount"] == 0 and s["differs1s"] is False, "differs1s=%s" % s["differs1s"])
            cap = Capture(ff, os.path.join(tmp, "c"))
            s = cap.run(["-f", "lavfi", "-i", "testsrc2=s=320x180:r=60:d=1.5"], (0, 0, 320, 180))
            check("lavfi: animated scene -> differs1s true, every frame changed", s["differs1s"] is True and
                  s["changedFrames"] == s["frames"], "%d/%d changed" % (s["changedFrames"], s["frames"]))
            # seeded failure: the S2.M1 picture (see-through without the DWM call composes black) is caught
            cap = Capture(ff, os.path.join(tmp, "d"), [("centre", 160, 90)], {"centre": (255, 0, 255)})
            s = cap.run(["-f", "lavfi", "-i", "color=c=black:s=320x180:r=60:d=1"], (0, 0, 320, 180))
            check("seeded S2.M1 model: black composition -> every frame black, calibration colour absent",
                  s["frames"] == 60 and s["blackCount"] == s["frames"] and s["expect"]["centre"]["matching"] == 0,
                  "%d frames" % s["frames"])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        if live:
            tmp = tempfile.mkdtemp(prefix="de_cv_live_")
            try:
                rect = _monitor_rect(0)
                cap = Capture(ff, tmp)
                q0, qf = qpc()
                s = cap.run(["-f", "lavfi", "-i", "ddagrab=output_idx=0:framerate=60:draw_mouse=0:dup_frames=0"], rect,
                            1.5, False, "hwdownload,format=bgra,")
                check("live ddagrab: frames captured (statistics only, nothing stored)", s["frames"] > 0,
                      "%d frames in %.2f s, ptsClock=%s, firstPts=%s, qpcAtStart=%.3f s" % (
                          s["frames"], s["durationS"], s["ptsClock"], s["firstPts"], q0 / float(qf)))
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
    ok = all(results)
    print("COMPOSED_VIDEO SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="composed-desktop video + black-frame detector")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--selftest-live", action="store_true")
    ap.add_argument("--find-ffmpeg", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    for name in ("record", "analyze"):
        p = sub.add_parser(name)
        if name == "record":
            p.add_argument("--out", required=True)
            p.add_argument("--seconds", type=float, default=10.0)
            p.add_argument("--fps", type=float, default=165.0)
            # Default gdigrab since the S2 real run of: ddagrab (Desktop Duplication) delivers a frame
 # only when the desktop changes and stalled the td1/n2z windows (TimeoutExpired, no frames); gdigrab gave
 # 125 frames / 5 s on the same desktop (T-D0 proved GDI sees the DWM composition). The switch-latency row
 # then reads an upper bound (~40 ms at 25 fps) instead of "<= 2 refreshes".
            p.add_argument("--method", choices=("ddagrab", "gdigrab"), default="gdigrab")
            p.add_argument("--monitor", type=int, default=0)
            p.add_argument("--region", default=None, help="x,y,w,h in virtual-screen coordinates")
            p.add_argument("--keep-video", action="store_true")
            p.add_argument("--guard-processes", default="")
            p.add_argument("--guard-title-prefix", default="")
        else:
            p.add_argument("video")
            p.add_argument("--out", default=None)
        p.add_argument("--probe", action="append", default=[], help="name=x,y (screen coordinates; repeatable)")
        p.add_argument("--expect", action="append", default=[], help="name=r,g,b expected at that probe")
        p.add_argument("--expect-fraction", type=float, default=0.95)
        p.add_argument("--black-luma", type=int, default=BLACK_LUMA)
        p.add_argument("--black-fraction", type=float, default=BLACK_FRACTION)
    a = ap.parse_args(argv)
    if a.find_ffmpeg:
        print(find_ffmpeg() or "not found")
        return 0 if find_ffmpeg() else 2
    if a.selftest or a.selftest_live:
        return selftest(live=a.selftest_live)
    if a.cmd == "record":
        return record(a)
    if a.cmd == "analyze":
        return analyze(a)
    ap.print_usage()
    return 4

if __name__ == "__main__":
    sys.exit(main())
