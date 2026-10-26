"""Z-order walker (ARCHITECTURE 5.1 "Z-order walker"; the S2.T-D1 z-order oracle and the definition behind C5 zOrderOk).

From SE's window walk the z-order downwards (GetWindow(GW_HWNDNEXT)), skipping windows that are invisible,

The first remaining window must be snHwnd. The
skipped windows are listed with their reason.

 python -I tools/display/walker.py # SE and Subnautica windows found automatically
 python -I tools/display/walker.py --se 0x1234 --sn 0x5678 --json
 python -I tools/display/walker.py --watch 60 --interval 0.1 --jsonl out.jsonl # S2.N10-style 100 ms samples
 python -I tools/display/walker.py --selftest # synthetic chains + live off-screen windows (no focus)

Automatic handles (`auto`, the default): the section's C0 seHwnd/snHwnd (and C7 snHwnd) when the section exists
(opened read-only, never created), else the first visible unowned top-level window of Legacy.exe / SpaceEngineers.exe
and of Subnautica.exe. Each sample also records the foreground window (GetForegroundWindow) and whether it is SE's.

Privacy: window titles are printed only for the two games, Notepad and our stand-ins (titles starting DE_);
any other window is listed by class, process image name and PID with its title redacted (--titles shows them).
Read-only: the walker never moves, activates or changes a window.

Exit codes: 0 snHwnd directly under seHwnd (or --watch done, or selftest PASS); 1 not directly under (or selftest
FAIL); 2 a handle is missing or not a window; 4 usage.
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
import time

GW_HWNDNEXT, GW_OWNER = 2, 4
DWMWA_CLOAKED, DWMWA_CLOAK = 14, 13
GWL_EXSTYLE = -20
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
GAME_IMAGES = ("legacy.exe", "spaceengineers.exe", "subnautica.exe")
SE_IMAGES = ("legacy.exe", "spaceengineers.exe")
SN_IMAGES = ("subnautica.exe",)
TITLE_OK_IMAGES = GAME_IMAGES + ("notepad.exe",)

_u32 = ctypes.WinDLL("user32", use_last_error=True) if os.name == "nt" else None
_k32 = ctypes.WinDLL("kernel32", use_last_error=True) if os.name == "nt" else None
try:
    _dwm = ctypes.WinDLL("dwmapi") if os.name == "nt" else None
except OSError:  # pragma: no cover
    _dwm = None

def _proto(lib, name, res, *args):
    f = getattr(lib, name)
    f.restype = res
    f.argtypes = list(args)
    return f

if _u32 is not None:
    GetWindow = _proto(_u32, "GetWindow", wt.HWND, wt.HWND, wt.UINT)
    IsWindow = _proto(_u32, "IsWindow", wt.BOOL, wt.HWND)
    IsWindowVisible = _proto(_u32, "IsWindowVisible", wt.BOOL, wt.HWND)
    GetWindowRect = _proto(_u32, "GetWindowRect", wt.BOOL, wt.HWND, ctypes.POINTER(wt.RECT))
    GetWindowTextW = _proto(_u32, "GetWindowTextW", ctypes.c_int, wt.HWND, wt.LPWSTR, ctypes.c_int)
    GetClassNameW = _proto(_u32, "GetClassNameW", ctypes.c_int, wt.HWND, wt.LPWSTR, ctypes.c_int)
    GetWindowThreadProcessId = _proto(_u32, "GetWindowThreadProcessId", wt.DWORD, wt.HWND, ctypes.POINTER(wt.DWORD))
    GetForegroundWindow = _proto(_u32, "GetForegroundWindow", wt.HWND)
    GetWindowLongW = _proto(_u32, "GetWindowLongW", ctypes.c_long, wt.HWND, ctypes.c_int)
    GetTopWindow = _proto(_u32, "GetTopWindow", wt.HWND, wt.HWND)
    WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    EnumWindows = _proto(_u32, "EnumWindows", wt.BOOL, WNDENUMPROC, wt.LPARAM)
    OpenProcess = _proto(_k32, "OpenProcess", wt.HANDLE, wt.DWORD, wt.BOOL, wt.DWORD)
    CloseHandle = _proto(_k32, "CloseHandle", wt.BOOL, wt.HANDLE)
    QueryFullProcessImageNameW = _proto(_k32, "QueryFullProcessImageNameW", wt.BOOL, wt.HANDLE, wt.DWORD, wt.LPWSTR,
                                        ctypes.POINTER(wt.DWORD))
    if _dwm is not None:
        DwmGetWindowAttribute = _proto(_dwm, "DwmGetWindowAttribute", ctypes.c_long, wt.HWND, wt.DWORD,
                                       ctypes.c_void_p, wt.DWORD)

def h2i(h) -> int:
    return int(h or 0)

def hexh(h) -> str:
    return "0x%x" % h2i(h)

_image_cache = {}

def image_name(pid: int) -> str:
    if pid in _image_cache:
        return _image_cache[pid]
    name = "?"
    h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid) if pid else None
    if h:
        try:
            buf = ctypes.create_unicode_buffer(520)
            n = wt.DWORD(520)
            if QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
                name = os.path.basename(buf.value)
        finally:
            CloseHandle(h)
    _image_cache[pid] = name
    return name

def describe(hwnd) -> dict:
    """The facts the walker needs about one window (live)."""
    h = h2i(hwnd)
    pid = wt.DWORD()
    GetWindowThreadProcessId(h, ctypes.byref(pid))
    r = wt.RECT()
    GetWindowRect(h, ctypes.byref(r))
    t = ctypes.create_unicode_buffer(256)
    GetWindowTextW(h, t, 256)
    c = ctypes.create_unicode_buffer(256)
    GetClassNameW(h, c, 256)
    cloaked = 0
    if _dwm is not None:
        v = wt.DWORD(0)
        if DwmGetWindowAttribute(h, DWMWA_CLOAKED, ctypes.byref(v), ctypes.sizeof(v)) == 0:
            cloaked = int(v.value)
    owners = []
    o = h2i(GetWindow(h, GW_OWNER))
    while o and len(owners) < 16:
        owners.append(o)
        o = h2i(GetWindow(o, GW_OWNER))
    return {"hwnd": h, "pid": int(pid.value), "image": image_name(int(pid.value)), "visible": bool(IsWindowVisible(h)),
            "rect": [r.left, r.top, r.right, r.bottom], "cloaked": cloaked, "owners": owners, "title": t.value,
            "cls": c.value, "exstyle": GetWindowLongW(h, GWL_EXSTYLE) & 0xFFFFFFFF}

def skip_reason(d: dict, se: int, sn: int):
    """Why the walker passes over a window (ARCHITECTURE 5.1), or None when it counts."""
    if not d.get("visible"):
        return "invisible"
    if d.get("cloaked"):
        return "cloaked(%d)" % d["cloaked"]
    r = d.get("rect") or [0, 0, 0, 0]
    if r[2] - r[0] <= 0 or r[3] - r[1] <= 0:
        return "zero-area"
    owners = d.get("owners") or []
    if se and se in owners:
        return "owned by seHwnd"
    if sn and sn in owners:
        return "owned by snHwnd"
    return None

def walk_chain(chain, se: int, sn: int) -> dict:
    """Pure walker over `chain`: the descriptors of the windows below seHwnd in z-order (top first)."""
    skipped = []
    first = None
    for d in chain:
        why = skip_reason(d, se, sn)
        if why:
            skipped.append(dict(d, reason=why))
            continue
        first = d
        break
    ok = first is not None and first["hwnd"] == sn and sn != 0
    return {"ok": ok, "seHwnd": se, "snHwnd": sn, "first": first, "skipped": skipped,
            "verdict": "snHwnd directly under seHwnd" if ok else (
                "no window below seHwnd" if first is None else "first window under seHwnd is %s, not snHwnd" % (
                    "snHwnd" if first["hwnd"] == sn else "%s %s (%s pid %d)" % (hexh(first["hwnd"]), first.get("cls"),
                                                                                first.get("image"), first.get("pid", 0))))}

def live_chain(se: int, limit: int = 256) -> list:
    """Descriptors of the windows below `se` (GetWindow(GW_HWNDNEXT) from seHwnd), up to and including the first that
 counts, plus at most `limit` windows."""
    out = []
    h = h2i(GetWindow(se, GW_HWNDNEXT))
    while h and len(out) < limit:
        out.append(describe(h))
        h = h2i(GetWindow(h, GW_HWNDNEXT))
    return out

def walk(se: int, sn: int, limit: int = 256) -> dict:
    """Live walk from seHwnd. Stops collecting at the first window that counts."""
    if not se or not IsWindow(se):
        return {"ok": False, "seHwnd": se, "snHwnd": sn, "first": None, "skipped": [], "verdict": "seHwnd is not a window",
                "error": "se"}
    if not sn or not IsWindow(sn):
        res = walk_chain([], se, sn)
        res.update(verdict="snHwnd is not a window", error="sn")
        return res
    chain = []
    h = h2i(GetWindow(se, GW_HWNDNEXT))
    while h and len(chain) < limit:
        d = describe(h)
        chain.append(d)
        if skip_reason(d, se, sn) is None:
            break
        h = h2i(GetWindow(h, GW_HWNDNEXT))
    return walk_chain(chain, se, sn)

def foreground(se: int = 0) -> dict:
    h = h2i(GetForegroundWindow())
    pid = wt.DWORD()
    if h:
        GetWindowThreadProcessId(h, ctypes.byref(pid))
    return {"hwnd": h, "pid": int(pid.value), "image": image_name(int(pid.value)) if h else None,
            "isSe": bool(se) and h == se}

def top_level_windows() -> list:
    out = []

    def cb(hwnd, _lp):
        out.append(h2i(hwnd))
        return True
    EnumWindows(WNDENUMPROC(cb), 0)
    return out

def main_window_of(images) -> int:
    """First visible, unowned, non-zero top-level window of a process with one of these image names (z-order)."""
    low = tuple(x.lower() for x in images)
    for h in top_level_windows():
        if not IsWindowVisible(h) or h2i(GetWindow(h, GW_OWNER)):
            continue
        pid = wt.DWORD()
        GetWindowThreadProcessId(h, ctypes.byref(pid))
        if image_name(int(pid.value)).lower() in low:
            r = wt.RECT()
            GetWindowRect(h, ctypes.byref(r))
            if r.right > r.left and r.bottom > r.top:
                return h
    return 0

def section_hwnds(name=None):
    """(seHwnd, snHwnd) from the section (read-only open; never creates), or (0, 0)."""
    try:
        root = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
        wire = os.path.join(root, "tests", "wire")
        if wire not in sys.path:
            sys.path.insert(0, wire)
        import layout_py as L  # noqa: E402
        import section_py as S  # noqa: E402
    except ImportError:
        return 0, 0
    sec = S.Section.open(name or L.SECTION_NAME, readonly=True)
    if sec is None:
        return 0, 0
    try:
        if sec.mapped_size < L.SECTION_SIZE or sec.hdr("magic") != L.MAGIC:
            return 0, 0
        se, sn = sec.hdr("seHwnd"), sec.hdr("snHwnd")
        if not sn:
            st, _seq, data = S.seqlock_read(sec, L.REGION["C7"].offset, L.SN_STATUS.size)
            if data:
                sn = L.SN_STATUS.unpack(data)["snHwnd"]
        return int(se), int(sn)
    finally:
        sec.close()

def resolve(se_arg, sn_arg, name=None):
    se = sn = 0
    src = {}
    if se_arg in (None, "auto") or sn_arg in (None, "auto"):
        s_se, s_sn = section_hwnds(name)
        if se_arg in (None, "auto") and s_se:
            se, src["se"] = s_se, "section C0 seHwnd"
        if sn_arg in (None, "auto") and s_sn:
            sn, src["sn"] = s_sn, "section snHwnd"
    if se_arg not in (None, "auto"):
        se, src["se"] = int(se_arg, 0), "argument"
    if sn_arg not in (None, "auto"):
        sn, src["sn"] = int(sn_arg, 0), "argument"
    if not se:
        se = main_window_of(SE_IMAGES)
        src["se"] = "process main window" if se else "not found"
    if not sn:
        sn = main_window_of(SN_IMAGES)
        src["sn"] = "process main window" if sn else "not found"
    return se, sn, src

def redact(d, titles=False) -> dict:
    if not d:
        return d
    out = {k: d[k] for k in ("hwnd", "pid", "image", "visible", "rect", "cloaked", "cls") if k in d}
    out["hwnd"] = hexh(d["hwnd"])
    if d.get("owners"):
        out["owners"] = [hexh(o) for o in d["owners"]]
    if "reason" in d:
        out["reason"] = d["reason"]
    ok_title = titles or (d.get("image") or "").lower() in TITLE_OK_IMAGES or (d.get("title") or "").startswith("DE_") \
        or d.get("pid") == os.getpid()
    out["title"] = d.get("title", "") if ok_title else "<redacted>"
    return out

def result_json(res, fg=None, titles=False) -> dict:
    out = {"ok": res["ok"], "verdict": res["verdict"], "seHwnd": hexh(res["seHwnd"]), "snHwnd": hexh(res["snHwnd"]),
           "first": redact(res.get("first"), titles), "skipped": [redact(s, titles) for s in res.get("skipped", [])]}
    if "error" in res:
        out["error"] = res["error"]
    if fg is not None:
        out["foreground"] = {"hwnd": hexh(fg["hwnd"]), "pid": fg["pid"], "image": fg["image"], "isSe": fg["isSe"]}
    return out

def print_result(res, fg, titles=False):
    print("walker: %s (seHwnd %s, snHwnd %s)" % (res["verdict"], hexh(res["seHwnd"]), hexh(res["snHwnd"])))
    for s in res.get("skipped", []):
        r = redact(s, titles)
        print("  skipped %s %-28s %-22s pid %-6d %s  [%s]" % (r["hwnd"], (r.get("cls") or "")[:28],
                                                            (r.get("image") or "")[:22], s.get("pid", 0),
                                                            r["title"][:40], s["reason"]))
    if res.get("first"):
        r = redact(res["first"], titles)
        print("  first   %s %-28s %-22s pid %-6d %s" % (r["hwnd"], (r.get("cls") or "")[:28], (r.get("image") or "")[:22],
                                                       res["first"].get("pid", 0), r["title"][:40]))
    if fg is not None:
        print("foreground: %s %s pid %d -> %s" % (hexh(fg["hwnd"]), fg["image"], fg["pid"],
                                                  "SE" if fg["isSe"] else "NOT SE"))

# ----------------------------------------------------------------------------------------------------------- selftest
class _Wnd:
    """Off-screen test windows of this process (never activated; nothing appears on any monitor)."""

    def __init__(self):
        u = _u32
        self.u = u
        self.WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
        u.DefWindowProcW.restype = ctypes.c_ssize_t
        u.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]

        class WNDCLASSW(ctypes.Structure):
            _fields_ = [("style", wt.UINT), ("lpfnWndProc", self.WNDPROC), ("cbClsExtra", ctypes.c_int),
                        ("cbWndExtra", ctypes.c_int), ("hInstance", wt.HINSTANCE), ("hIcon", wt.HICON),
                        ("hCursor", wt.HANDLE), ("hbrBackground", wt.HBRUSH), ("lpszMenuName", wt.LPCWSTR),
                        ("lpszClassName", wt.LPCWSTR)]
        self.proc = self.WNDPROC(lambda h, m, w, l: u.DefWindowProcW(h, m, w, l))
        self.cls = "DE_WalkerSelftest_%d" % os.getpid()
        wc = WNDCLASSW()
        wc.lpfnWndProc = self.proc
        wc.hInstance = _k32.GetModuleHandleW(None)
        wc.lpszClassName = self.cls
        u.RegisterClassW.restype = wt.ATOM
        u.RegisterClassW.argtypes = [ctypes.POINTER(WNDCLASSW)]
        if not u.RegisterClassW(ctypes.byref(wc)):
            raise OSError("RegisterClassW failed (%d)" % ctypes.get_last_error())
        u.CreateWindowExW.restype = wt.HWND
        u.CreateWindowExW.argtypes = [wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                      ctypes.c_int, wt.HWND, wt.HMENU, wt.HINSTANCE, wt.LPVOID]
        u.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
        u.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
        u.DestroyWindow.argtypes = [wt.HWND]
        self.made = []

    def make(self, title, x=-30000, y=-30000, w=200, h=120, owner=None, show=True, ex=0x08000080, style=0x80000000):
        """WS_POPUP, WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW by default: no taskbar button, never activated."""
        hwnd = self.u.CreateWindowExW(ex, self.cls, title, style, x, y, w, h, owner, None,
                                      _k32.GetModuleHandleW(None), None)
        if not hwnd:
            raise OSError("CreateWindowExW failed (%d)" % ctypes.get_last_error())
        self.made.append(hwnd)
        if show:
            self.u.ShowWindow(hwnd, 4)    # SW_SHOWNOACTIVATE
        return h2i(hwnd)

    def below(self, hwnd, after):
        """Places `hwnd` directly below `after` (own windows: always allowed), no activation."""
        return bool(self.u.SetWindowPos(hwnd, after, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))

    def cloak(self, hwnd, on=True):
        v = wt.BOOL(1 if on else 0)
        _dwm.DwmSetWindowAttribute.argtypes = [wt.HWND, wt.DWORD, ctypes.c_void_p, wt.DWORD]
        return _dwm.DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ctypes.byref(v), ctypes.sizeof(v)) == 0

    def pump(self):
        msg = wt.MSG()
        while self.u.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
            self.u.TranslateMessage(ctypes.byref(msg))
            self.u.DispatchMessageW(ctypes.byref(msg))

    def close(self):
        for h in reversed(self.made):
            self.u.DestroyWindow(h)
        self.made = []
        self.u.UnregisterClassW(self.cls, _k32.GetModuleHandleW(None))

def selftest(live=True) -> int:
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-66s %s %s" % (label, "PASS" if cond else "FAIL", detail))

    def W(h, visible=True, rect=(0, 0, 100, 100), cloaked=0, owners=(), cls="C", image="x.exe", pid=1):
        return {"hwnd": h, "visible": visible, "rect": list(rect), "cloaked": cloaked, "owners": list(owners),
                "cls": cls, "image": image, "pid": pid, "title": "t"}
    SE, SN = 10, 20
    check("synthetic: SN directly under SE", walk_chain([W(SN)], SE, SN)["ok"])
    r = walk_chain([W(1, visible=False), W(2, cloaked=2), W(3, rect=(5, 5, 5, 50)), W(4, owners=[SE]),
                    W(5, owners=[7, SN]), W(SN)], SE, SN)
    check("synthetic: invisible, cloaked, zero-area, owned by SE / by SN (chain) skipped", r["ok"] and
          [s["reason"] for s in r["skipped"]] == ["invisible", "cloaked(2)", "zero-area", "owned by seHwnd",
                                                   "owned by snHwnd"], r["verdict"])
    r = walk_chain([W(1, visible=False), W(99, cls="Notepad", image="notepad.exe"), W(SN)], SE, SN)
    check("synthetic mutant S2.M9: a visible Notepad between -> not ok, first = Notepad",
          not r["ok"] and r["first"]["hwnd"] == 99, r["verdict"])
    check("synthetic: nothing below SE -> not ok", not walk_chain([], SE, SN)["ok"])
    check("synthetic: snHwnd 0 -> not ok", not walk_chain([W(0)], SE, 0)["ok"])
    if live and _u32 is not None:
        w = _Wnd()
        fg0 = h2i(GetForegroundWindow())
        try:
            se = w.make("DE_WALKER_SE")
            sn = w.make("DE_WALKER_SN")
            w.below(sn, se)
            w.pump()
            r = walk(se, sn)
            check("live: off-screen SN placed directly under SE -> ok", r["ok"], r["verdict"])
            inv = w.make("DE_WALKER_INVISIBLE", show=False)
            w.below(inv, se)
            zero = w.make("DE_WALKER_ZERO", w=0, h=0)
            w.below(zero, inv)
            owned = w.make("DE_WALKER_OWNED_BY_SN", owner=sn)
            w.below(owned, zero)                                     # SE, invisible, zero-area, owned,..., SN
            clo = w.make("DE_WALKER_CLOAKED")
            cloaked = w.cloak(clo)
            w.below(clo, owned)
            w.pump()
            r = walk(se, sn)
            reasons = sorted(set(s["reason"].split("(")[0] for s in r["skipped"]))
            check("live: invisible / zero-area / owned-by-SN / cloaked windows skipped -> ok",
                  r["ok"] and {"invisible", "zero-area", "owned by snHwnd"} <= set(reasons) and
                  (not cloaked or "cloaked" in reasons), "%s; skipped %s; cloak set %s" % (r["verdict"], reasons, cloaked))
            pad = w.make("DE_WALKER_NOTEPAD_STANDIN")
            w.below(pad, se)
            w.pump()
            r = walk(se, sn)
            check("live mutant S2.M9: a visible window between SE and SN -> not ok, first = it",
                  not r["ok"] and r["first"] and r["first"]["hwnd"] == pad, r["verdict"])
            check("live: no window took the foreground", h2i(GetForegroundWindow()) == fg0 or
                  h2i(GetForegroundWindow()) not in w.made, "fg %s" % hexh(GetForegroundWindow()))
        finally:
            w.close()
    ok = all(results)
    print("WALKER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="z-order walker: is Subnautica's window directly under SE's?")
    ap.add_argument("--se", default="auto", help="seHwnd (0x...), or auto")
    ap.add_argument("--sn", default="auto", help="snHwnd (0x...), or auto")
    ap.add_argument("--name", default=None, help="section name for auto (default Local\\DeepEngineers_v1)")
    ap.add_argument("--json", action="store_true", help="print one JSON object")
    ap.add_argument("--titles", action="store_true", help="do not redact other windows' titles")
    ap.add_argument("--watch", type=float, default=0.0, help="sample for this many seconds")
    ap.add_argument("--interval", type=float, default=0.1)
    ap.add_argument("--jsonl", default=None, help="--watch: one JSON line per sample to this file")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--no-live", action="store_true", help="--selftest: synthetic chains only (no windows created)")
    a = ap.parse_args(argv)
    if os.name != "nt":
        print("walker: Windows only")
        return 4
    if a.selftest:
        return selftest(live=not a.no_live)
    se, sn, src = resolve(a.se, a.sn, a.name)
    if a.watch > 0:
        f = open(a.jsonl, "a", encoding="utf-8") if a.jsonl else None
        qpcf = ctypes.c_int64()
        _k32.QueryPerformanceFrequency(ctypes.byref(qpcf))
        n = bad = notse = 0
        end = time.monotonic() + a.watch
        try:
            while time.monotonic() < end:
                q = ctypes.c_int64()
                _k32.QueryPerformanceCounter(ctypes.byref(q))
                res = walk(se, sn)
                fg = foreground(se)
                n += 1
                bad += 0 if res["ok"] else 1
                notse += 0 if fg["isSe"] else 1
                rec = dict(result_json(res, fg, a.titles), qpc=q.value, qpcFrequency=qpcf.value,
                           utc=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()))
                if f:
                    f.write(json.dumps(rec) + "\n")
                time.sleep(a.interval)
        finally:
            if f:
                f.close()
        print("walker watch: %d samples, %d not directly under, %d with SE not foreground (seHwnd %s %s, snHwnd %s %s)"
              % (n, bad, notse, hexh(se), src.get("se"), hexh(sn), src.get("sn")))
        return 0
    if not se or not sn:
        print("walker: %s" % ("seHwnd not found (%s)" % src.get("se") if not se else "snHwnd not found (%s)" % src.get("sn")))
        return 2
    res = walk(se, sn)
    fg = foreground(se)
    if a.json:
        print(json.dumps(dict(result_json(res, fg, a.titles), sources=src)))
    else:
        print_result(res, fg, a.titles)
    if res.get("error"):
        return 2
    return 0 if res["ok"] else 1

if __name__ == "__main__":
    sys.exit(main())
