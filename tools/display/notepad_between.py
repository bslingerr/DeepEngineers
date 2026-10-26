"""Places a visible Notepad window between the two game windows (S2.N5 set-up and the S2.M9 seeded failure).

 python -I tools/display/notepad_between.py place [--se auto] [--sn auto] [--rect 300,300,900,600] [--hold 10]
 python -I tools/display/notepad_between.py close --hwnd 0x1234 # or --pid N (WM_CLOSE, never killed)
 python -I tools/display/notepad_between.py --selftest # stand-in windows only (nothing on screen)
 python -I tools/display/notepad_between.py --selftest-notepad # a real Notepad, placed off-screen, closed

`place` starts notepad.exe with SW_SHOWNOACTIVATE (STARTUPINFO), finds its top-level window (class "Notepad": the
started PID, or a Notepad window that did not exist before the start, for the Store version whose window belongs to
another process), and moves it with SetWindowPos(notepad, seHwnd, x, y, w, h, SWP_NOACTIVATE): directly below SE, so
above Subnautica when Subnautica was directly under SE. It never activates anything; if Notepad took the foreground
anyway, the previous foreground window is restored with SetForegroundWindow (logged: focusStolen/restored). The z-order
walker (tools/display/walker.py) then reads the chain: with Notepad between, the walker must report "not directly
under" (S2.M9: proves the T-D1 z-order oracle) and, after SE re-asserts the order (S2.N5: Alt+Tab back, or the plugin's
re-assert), Subnautica directly under SE again. `--hold S` samples the walker every 0.1 s for S seconds (records when
the order is restored), then closes Notepad with WM_CLOSE (an untouched Notepad closes without a prompt).

Output: one JSON object (notepadPid, notepadHwnd, placed, walker, foreground before/after, focusStolen, restored,
restoredAfterS). Exit codes: 0 placed (and, with --hold, closed); 1 not placed; 2 handles missing; 4 usage.
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import walker as WK  # noqa: E402

SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x1, 0x2, 0x10
SW_SHOWNOACTIVATE, SW_SHOWMINNOACTIVE = 4, 7
WM_CLOSE = 0x0010
STARTF_USESHOWWINDOW = 0x1

_u = WK._u32
if _u is not None:
    _u.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
    _u.SetWindowPos.restype = wt.BOOL
    _u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
    _u.SetForegroundWindow.argtypes = [wt.HWND]
    _u.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]

def notepad_windows() -> dict:
    """{hwnd: pid} of visible top-level windows of class Notepad."""
    out = {}
    for h in WK.top_level_windows():
        c = ctypes.create_unicode_buffer(64)
        WK.GetClassNameW(h, c, 64)
        if c.value == "Notepad" and WK.IsWindowVisible(h):
            pid = wt.DWORD()
            WK.GetWindowThreadProcessId(h, ctypes.byref(pid))
            out[h] = int(pid.value)
    return out

def start_notepad(show=SW_SHOWNOACTIVATE, timeout=10.0):
    """(proc, hwnd): notepad.exe started without activation, and its window."""
    before = set(notepad_windows())
    si = subprocess.STARTUPINFO()
    si.dwFlags |= STARTF_USESHOWWINDOW
    si.wShowWindow = show
    proc = subprocess.Popen(["notepad.exe"], startupinfo=si, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL)
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        now = notepad_windows()
        mine = [h for h, p in now.items() if p == proc.pid]
        new = [h for h in now if h not in before]
        if mine or new:
            return proc, (mine or new)[0]
        time.sleep(0.05)
    return proc, 0

def place(se: int, sn: int, rect=(300, 300, 900, 600), restore_focus=True, show=SW_SHOWNOACTIVATE) -> dict:
    """Starts Notepad and puts it directly below seHwnd. Returns the record (see the module docstring)."""
    fg0 = WK.foreground(se)
    proc, hwnd = start_notepad(show)
    rec = {"notepadPid": proc.pid, "notepadHwnd": WK.hexh(hwnd), "hwnd": hwnd, "foregroundBefore": WK.hexh(fg0["hwnd"]),
           "seHwnd": WK.hexh(se), "snHwnd": WK.hexh(sn), "rect": list(rect)}
    if not hwnd:
        rec.update(placed=False, error="no Notepad window within 10 s")
        return rec
    x, y, w, h = rect
    if show == SW_SHOWMINNOACTIVE:
        _u.ShowWindow(hwnd, SW_SHOWNOACTIVATE)
    ok = bool(_u.SetWindowPos(hwnd, se, x, y, w, h, SWP_NOACTIVATE))
    rec["placed"] = ok
    rec["setWindowPosError"] = 0 if ok else ctypes.get_last_error()
    time.sleep(0.15)
    fg1 = WK.foreground(se)
    stolen = fg1["hwnd"] == hwnd
    rec["focusStolen"] = stolen
    rec["restored"] = None
    if stolen and restore_focus and fg0["hwnd"]:
        rec["restored"] = bool(_u.SetForegroundWindow(fg0["hwnd"]))
        time.sleep(0.1)
    fg2 = WK.foreground(se)
    rec["foregroundAfter"] = WK.hexh(fg2["hwnd"])
    rec["foregroundUnchanged"] = fg2["hwnd"] == fg0["hwnd"]
    res = WK.walk(se, sn)
    rec["walker"] = WK.result_json(res)
    rec["notepadDirectlyUnderSe"] = bool(res.get("first")) and res["first"]["hwnd"] == hwnd
    return rec

def close(hwnd: int = 0, pid: int = 0, timeout=5.0) -> bool:
    """WM_CLOSE to the Notepad window (never a kill). True when it is gone."""
    if not hwnd and pid:
        hwnd = next((h for h, p in notepad_windows().items() if p == pid), 0)
    if not hwnd:
        return True
    _u.PostMessageW(hwnd, WM_CLOSE, 0, 0)
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if not WK.IsWindow(hwnd):
            return True
        time.sleep(0.05)
    return False

def hold(rec, se, sn, seconds, interval=0.1) -> dict:
    """Samples the walker; records the first time Subnautica is directly under SE again (S2.N5 re-assert)."""
    t0 = time.monotonic()
    samples = 0
    restored_at = None
    while time.monotonic() - t0 < seconds:
        res = WK.walk(se, sn)
        samples += 1
        if res["ok"] and restored_at is None:
            restored_at = round(time.monotonic() - t0, 3)
        time.sleep(interval)
    rec["holdSamples"] = samples
    rec["restoredAfterS"] = restored_at
    return rec

def selftest(notepad=False) -> int:
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-70s %s %s" % (label, "PASS" if cond else "FAIL", detail))
    w = WK._Wnd()
    fg0 = WK.h2i(WK.GetForegroundWindow())
    try:
        se = w.make("DE_NOTEPAD_SE")
        sn = w.make("DE_NOTEPAD_SN")
        w.below(sn, se)
        w.pump()
        check("stand-ins: SN directly under SE before the placement", WK.walk(se, sn)["ok"])
        if not notepad:
            pad = w.make("DE_NOTEPAD_STANDIN", x=-29000, y=-29000)
            ok = bool(_u.SetWindowPos(pad, se, -29000, -29000, 300, 200, SWP_NOACTIVATE))
            w.pump()
            res = WK.walk(se, sn)
            check("placement below seHwnd (SetWindowPos insert-after SE, NOACTIVATE)", ok)
            check("S2.M9: walker red with the window between, first = it", not res["ok"] and res["first"] and
                  res["first"]["hwnd"] == pad, res["verdict"])
            w.below(sn, se)        # the re-assert SE performs: SetWindowPos(snHwnd, seHwnd,...)
            w.pump()
            check("re-assert SetWindowPos(sn, se): walker green again", WK.walk(se, sn)["ok"])
        else:
            rec = place(se, sn, rect=(-29000, -29000, 400, 300), show=SW_SHOWMINNOACTIVE)
            check("real Notepad started and found", bool(rec.get("hwnd")), rec.get("notepadHwnd"))
            check("real Notepad placed directly below the SE stand-in", rec.get("notepadDirectlyUnderSe"),
                  rec.get("walker", {}).get("verdict"))
            check("walker red (S2.M9) with Notepad between", rec.get("walker") and not rec["walker"]["ok"])
            check("foreground unchanged (or restored)", rec.get("foregroundUnchanged") or rec.get("restored"),
                  "stolen=%s restored=%s" % (rec.get("focusStolen"), rec.get("restored")))
            w.below(sn, se)
            w.pump()
            check("re-assert: walker green again", WK.walk(se, sn)["ok"])
            check("Notepad closed with WM_CLOSE", close(rec.get("hwnd", 0)))
        check("no stand-in took the foreground", WK.h2i(WK.GetForegroundWindow()) not in w.made,
              "fg %s (before %s)" % (WK.hexh(WK.GetForegroundWindow()), WK.hexh(fg0)))
    finally:
        w.close()
    ok = all(results)
    print("NOTEPAD_BETWEEN SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="a Notepad window between the two game windows (S2.N5, S2.M9)")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--selftest-notepad", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    p = sub.add_parser("place")
    p.add_argument("--se", default="auto")
    p.add_argument("--sn", default="auto")
    p.add_argument("--name", default=None)
    p.add_argument("--rect", default="300,300,900,600", help="x,y,w,h of the Notepad window")
    p.add_argument("--hold", type=float, default=0.0, help="sample the walker for S seconds, then close Notepad")
    p.add_argument("--keep", action="store_true", help="with --hold: leave Notepad open")
    p = sub.add_parser("close")
    p.add_argument("--hwnd", default="0")
    p.add_argument("--pid", type=int, default=0)
    a = ap.parse_args(argv)
    if os.name != "nt":
        return 4
    if a.selftest or a.selftest_notepad:
        return selftest(notepad=a.selftest_notepad)
    if a.cmd == "close":
        ok = close(int(a.hwnd, 0), a.pid)
        print(json.dumps({"closed": ok}))
        return 0 if ok else 1
    if a.cmd != "place":
        ap.print_usage()
        return 4
    se, sn, src = WK.resolve(a.se, a.sn, a.name)
    if not se or not sn:
        print(json.dumps({"placed": False, "error": "handles missing", "sources": src}))
        return 2
    rect = tuple(int(v) for v in a.rect.split(","))
    rec = place(se, sn, rect)
    if a.hold > 0 and rec.get("hwnd"):
        hold(rec, se, sn, a.hold)
        if not a.keep:
            rec["closed"] = close(rec["hwnd"])
    rec.pop("hwnd", None)
    print(json.dumps(rec))
    return 0 if rec.get("placed") else 1

if __name__ == "__main__":
    sys.exit(main())
