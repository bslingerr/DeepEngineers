"""Taskbar buttons and Alt+Tab entries of a process (S2.N9), and a 100 ms foreground sampler (S2.N10).

 python -I tools/display/ui_automation.py check [--process Subnautica.exe] [--pattern Subnautica] [--json]
 python -I tools/display/ui_automation.py buttons [--all] # taskbar button names (matched ones by default)
 python -I tools/display/ui_automation.py fgwatch --seconds 60 [--interval 0.1] [--jsonl out.jsonl]
 [--forbid Subnautica.exe] [--expect Legacy.exe]
 python -I tools/display/ui_automation.py --selftest # rule table + UI Automation vtable check
 python -I tools/display/ui_automation.py --live-selftest # + an off-screen window restyled at run time

Two independent sources (ITaskbarList is write-only: it can delete a tab but cannot list tabs, so it is not a check):
1. UI Automation (stdlib ctypes COM, no package): CUIAutomation -> ElementFromHandle(Shell_TrayWnd) -> FindAll
 (descendants, true condition) -> Button elements and their names. The vtable indices used are verified at run time
 on the tray window itself (class name "Shell_TrayWnd", its explorer PID, control type Pane); a mismatch disables
 this source ("UIA unavailable") instead of returning wrong names.
2. The window-style rules of the shell, per top-level window of the process (EnumWindows): a window gets a taskbar
 button and an Alt+Tab entry when it is visible, not DWM-cloaked, has no WS_EX_TOOLWINDOW, and either has
 WS_EX_APPWINDOW or is unowned without WS_EX_NOACTIVATE. Measured here (--live-selftest): TOOLWINDOW removes the
 button even with APPWINDOW set (the documentation says APPWINDOW forces one), APPWINDOW|NOACTIVATE keeps it, and a
 restyle of a SHOWN window without the hide/show leaves the button (ITaskbarList:DeleteTab then removes it), so
 the ARCHITECTURE 5.1 order hide -> NOACTIVATE|TOOLWINDOW -> FRAMECHANGED -> show is what works.
S2.N9 pass (`check`): no taskbar button matching --pattern and no rule-eligible window of --process. The click where
`fgwatch` records the foreground every 100 ms meanwhile
(--forbid: a sample with that process in the foreground is a violation; --expect: the share of samples it held it).

Privacy: only button names matching --pattern are printed (--all prints every name); window titles of other processes
are never printed. Read-only except --live-selftest, which creates and destroys two 40x30 px windows of its own in the primary
monitor's bottom-right corner (under the taskbar; their taskbar buttons flash for a few seconds; nothing is
activated).
Exit codes: check 0 = no button and no entry (S2.N9 green), 1 = present, 2 = the process has no window; fgwatch 0 = no forbidden foreground sample;
selftest 0 = PASS; 2 = UIA unavailable for a `buttons` request; 4 usage.
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import walker as WK  # noqa: E402

WS_EX_TOOLWINDOW, WS_EX_APPWINDOW, WS_EX_NOACTIVATE = 0x80, 0x40000, 0x08000000
GWL_EXSTYLE = -20
UIA_ButtonControlTypeId, UIA_PaneControlTypeId = 50000, 50033
TreeScope_Descendants = 4

CLSID_CUIAutomation = "{FF48DBA4-60EF-4201-AA87-54103EEF594E}"
IID_IUIAutomation = "{30CBE57D-D9D0-452A-AB13-7AC5AC4825EE}"
CLSID_TaskbarList = "{56FDF344-FD6D-11D0-958A-006097C9A090}"
IID_ITaskbarList = "{56FDF342-FD6D-11D0-958A-006097C9A090}"
# vtable indices (IUnknown = 0..2), from UIAutomationClient.h / ShObjIdl.h; the UIA ones are verified at run time
IUIA_GetRootElement, IUIA_ElementFromHandle, IUIA_CreateTrueCondition = 5, 6, 21
IEL_FindAll, IEL_ProcessId, IEL_ControlType, IEL_Name, IEL_AutomationId, IEL_ClassName = 6, 20, 21, 23, 29, 30
IARR_Length, IARR_GetElement = 3, 4
ITBL_HrInit, ITBL_AddTab, ITBL_DeleteTab = 3, 4, 5

class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_ulong), ("Data2", ctypes.c_ushort), ("Data3", ctypes.c_ushort),
                ("Data4", ctypes.c_ubyte * 8)]

def _guid(s: str) -> GUID:
    g = GUID()
    ctypes.oledll.ole32.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(g))
    return g

def _vcall(ptr, index, *argtypes):
    """A bound COM method: HRESULT failures raise OSError (ctypes.HRESULT)."""
    vtbl = ctypes.cast(ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)))[0]
    proto = ctypes.WINFUNCTYPE(ctypes.HRESULT, ctypes.c_void_p, *argtypes)
    fn = proto(vtbl[index])
    return lambda *args: fn(ptr, *args)

def _release(ptr):
    if ptr:
        vtbl = ctypes.cast(ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)))[0]
        ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtbl[2])(ptr)

_com_ready = False

def com_init():
    global _com_ready
    if not _com_ready:
        try:
            ctypes.oledll.ole32.CoInitializeEx(None, 2)    # COINIT_APARTMENTTHREADED
        except OSError:
            pass                                           # RPC_E_CHANGED_MODE: already initialised differently
        _com_ready = True

def _create(clsid, iid):
    com_init()
    p = ctypes.c_void_p()
    ctypes.oledll.ole32.CoCreateInstance(ctypes.byref(_guid(clsid)), None, 1, ctypes.byref(_guid(iid)), ctypes.byref(p))
    return p.value

def _bstr_prop(el, index) -> str:
    b = ctypes.c_void_p()
    _vcall(el, index, ctypes.POINTER(ctypes.c_void_p))(ctypes.byref(b))
    if not b.value:
        return ""
    try:
        return ctypes.wstring_at(b.value)
    finally:
        ctypes.windll.oleaut32.SysFreeString(ctypes.c_void_p(b.value))

def _int_prop(el, index) -> int:
    v = ctypes.c_int()
    _vcall(el, index, ctypes.POINTER(ctypes.c_int))(ctypes.byref(v))
    return v.value

class Uia:
    """Minimal IUIAutomation client for the taskbar's buttons."""

    def __init__(self):
        self.ok = False
        self.why = ""
        self.uia = None
        try:
            self.uia = _create(CLSID_CUIAutomation, IID_IUIAutomation)
            self.tray = WK.h2i(ctypes.windll.user32.FindWindowW("Shell_TrayWnd", None))
            if not self.tray:
                self.why = "no Shell_TrayWnd"
                return
            el = self.element(self.tray)
            try:
                cls, ctl = _bstr_prop(el, IEL_ClassName), _int_prop(el, IEL_ControlType)
                pid = _int_prop(el, IEL_ProcessId)
            finally:
                _release(el)
            tpid = wt.DWORD()
            WK.GetWindowThreadProcessId(self.tray, ctypes.byref(tpid))
            if cls != "Shell_TrayWnd" or ctl != UIA_PaneControlTypeId or pid != tpid.value:
                self.why = "vtable check failed (class %r, control type %d, pid %d vs %d)" % (cls, ctl, pid, tpid.value)
                return
            self.ok = True
        except OSError as e:
            self.why = "COM error %s" % e

    def element(self, hwnd):
        el = ctypes.c_void_p()
        _vcall(self.uia, IUIA_ElementFromHandle, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p))(
            ctypes.c_void_p(hwnd), ctypes.byref(el))
        return el.value

    def taskbar_buttons(self) -> list:
        """[{name, cls, automationId}] of every Button under Shell_TrayWnd (and the secondary taskbars)."""
        if not self.ok:
            return []
        out = []
        cond = ctypes.c_void_p()
        _vcall(self.uia, IUIA_CreateTrueCondition, ctypes.POINTER(ctypes.c_void_p))(ctypes.byref(cond))
        trays = [self.tray]
        u = ctypes.windll.user32
        u.FindWindowExW.restype = wt.HWND
        u.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]
        h = u.FindWindowExW(None, None, "Shell_SecondaryTrayWnd", None)
        while h:
            trays.append(WK.h2i(h))
            h = u.FindWindowExW(None, h, "Shell_SecondaryTrayWnd", None)
        try:
            for t in trays:
                root = self.element(t)
                arr = ctypes.c_void_p()
                try:
                    _vcall(root, IEL_FindAll, ctypes.c_int, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p))(
                        TreeScope_Descendants, cond, ctypes.byref(arr))
                    n = ctypes.c_int()
                    _vcall(arr.value, IARR_Length, ctypes.POINTER(ctypes.c_int))(ctypes.byref(n))
                    for i in range(n.value):
                        e = ctypes.c_void_p()
                        _vcall(arr.value, IARR_GetElement, ctypes.c_int, ctypes.POINTER(ctypes.c_void_p))(i, ctypes.byref(e))
                        try:
                            if _int_prop(e.value, IEL_ControlType) == UIA_ButtonControlTypeId:
                                out.append({"name": _bstr_prop(e.value, IEL_Name), "cls": _bstr_prop(e.value, IEL_ClassName),
                                            "automationId": _bstr_prop(e.value, IEL_AutomationId)})
                        finally:
                            _release(e.value)
                finally:
                    _release(arr.value)
                    _release(root)
        finally:
            _release(cond.value)
        return out

    def close(self):
        _release(self.uia)
        self.uia = None

def rule(d: dict) -> dict:
    """Shell rules for one top-level window descriptor (walker.describe): taskbar button / Alt+Tab entry.
 Measured on this PC after a hide -> restyle -> show without
 activation: WS_EX_TOOLWINDOW removes the button even with WS_EX_APPWINDOW set (the documentation says APPWINDOW
 forces one); WS_EX_APPWINDOW without TOOLWINDOW keeps it even with WS_EX_NOACTIVATE; a plain unowned window shown
 without activation got none. A plain unowned window is still counted as eligible (conservative: the shell gives
 it a button once it is activated or shown normally). Alt+Tab uses the same rule (approximation: UI Automation
 cannot see the Alt+Tab list unless it is open)."""
    ex = d.get("exstyle", 0)
    visible = bool(d.get("visible")) and not d.get("cloaked")
    app = bool(ex & WS_EX_APPWINDOW)
    tool = bool(ex & WS_EX_TOOLWINDOW)
    noact = bool(ex & WS_EX_NOACTIVATE)
    owned = bool(d.get("owners"))
    if not visible:
        eligible, why = False, "invisible or cloaked"
    elif tool:
        eligible, why = False, "WS_EX_TOOLWINDOW (measured: wins over WS_EX_APPWINDOW)"
    elif app:
        eligible, why = True, "WS_EX_APPWINDOW"
    elif owned:
        eligible, why = False, "owned"
    elif noact:
        eligible, why = False, "WS_EX_NOACTIVATE"
    else:
        eligible, why = True, "plain unowned top-level window (button once activated)"
    return {"taskbar": eligible, "altTab": eligible, "why": why, "appwindow": app, "toolwindow": tool, "noactivate": noact}

def process_windows(image: str) -> list:
    out = []
    for h in WK.top_level_windows():
        d = WK.describe(h)
        if (d.get("image") or "").lower() == image.lower():
            out.append(d)
    return out

def check(image="Subnautica.exe", pattern="Subnautica", uia=None) -> dict:
    wins = process_windows(image)
    rows = []
    for d in wins:
        r = rule(d)
        rows.append({"hwnd": WK.hexh(d["hwnd"]), "cls": d["cls"], "visible": d["visible"], "cloaked": d["cloaked"],
                     "exstyle": "0x%08x" % d["exstyle"], "owned": bool(d["owners"]), **r})
    own = uia is None
    uia = uia or Uia()
    buttons = [b for b in uia.taskbar_buttons() if pattern.lower() in b["name"].lower()] if uia.ok else None
    if own:
        uia.close()
    eligible = [r for r in rows if r["taskbar"]]
    running = bool(wins)
    green = running and not eligible and (buttons == [] if buttons is not None else True)
    if not running:
        verdict = "%s has no top-level window (not running?): nothing checked" % image
    elif green:
        verdict = "no taskbar button and no Alt+Tab entry"
    else:
        verdict = "%d rule-eligible window(s), %s matching button(s)" % (len(eligible), "?" if buttons is None else
                                                                          len(buttons))
    return {"process": image, "pattern": pattern, "windows": rows, "eligible": len(eligible),
            "uia": uia.ok, "uiaWhy": uia.why, "buttons": buttons, "processRunning": running,
            "green": green, "verdict": verdict}

def fgwatch(seconds, interval=0.1, jsonl=None, forbid=None, expect=None) -> dict:
    k = ctypes.windll.kernel32
    f = open(jsonl, "a", encoding="utf-8") if jsonl else None
    n = viol = held = 0
    changes = []
    last = None
    end = time.monotonic() + seconds
    try:
        while time.monotonic() < end:
            q = ctypes.c_int64()
            k.QueryPerformanceCounter(ctypes.byref(q))
            fg = WK.foreground()
            n += 1
            img = (fg.get("image") or "").lower()
            if forbid and img == forbid.lower():
                viol += 1
            if expect and img == expect.lower():
                held += 1
            if img != last:
                changes.append({"qpc": q.value, "image": fg.get("image"), "pid": fg["pid"]})
                last = img
            if f:
                f.write(json.dumps({"qpc": q.value, "hwnd": WK.hexh(fg["hwnd"]), "pid": fg["pid"], "image": fg["image"]}) + "\n")
            time.sleep(interval)
    finally:
        if f:
            f.close()
    return {"samples": n, "forbidden": forbid, "forbiddenSamples": viol, "expected": expect,
            "expectedShare": round(held / n, 4) if n and expect else None, "changes": changes[:200]}

# ----------------------------------------------------------------------------------------------------------- selftest
def selftest(live=False) -> int:
    results = []

    def chk(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-72s %s %s" % (label, "PASS" if cond else "FAIL", detail))
    base = {"visible": True, "cloaked": 0, "owners": []}
    chk("rule: plain top-level window -> taskbar + Alt+Tab", rule(dict(base, exstyle=0))["taskbar"])
    chk("rule: NOACTIVATE|TOOLWINDOW (ARCHITECTURE 5.1 restyle) -> none",
        not rule(dict(base, exstyle=WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW))["taskbar"])
    chk("rule: NOACTIVATE only -> none", not rule(dict(base, exstyle=WS_EX_NOACTIVATE))["taskbar"])
    chk("rule: APPWINDOW left set with NOACTIVATE|TOOLWINDOW -> none (TOOLWINDOW wins, measured)",
        not rule(dict(base, exstyle=WS_EX_APPWINDOW | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW))["taskbar"])
    chk("rule mutant: TOOLWINDOW forgotten (APPWINDOW|NOACTIVATE) -> button stays",
        rule(dict(base, exstyle=WS_EX_APPWINDOW | WS_EX_NOACTIVATE))["taskbar"])
    chk("rule: owned window -> none", not rule(dict(base, exstyle=0, owners=[5]))["taskbar"])
    chk("rule: invisible / cloaked -> none", not rule(dict(base, exstyle=0, visible=False))["taskbar"] and
        not rule(dict(base, exstyle=0, cloaked=2))["taskbar"])
    u = Uia()
    chk("UI Automation: vtable verified on Shell_TrayWnd", u.ok, u.why)
    if u.ok:
        b = u.taskbar_buttons()
        chk("UI Automation: taskbar buttons enumerated", len(b) > 0, "%d buttons (names not printed)" % len(b))
    if live and u.ok:
        title = "DE_UIA_SELFTEST_%d" % os.getpid()
        w = WK._Wnd()
        try:
            # The taskbar names a button after the application and its window count (Windows 11 groups windows:
 # "Python 3.10 (64-bit) - 1 running window"), not after the window title, so presence is measured as a
 # change of the button-name set against a baseline taken before the window exists.
            base = sorted(x["name"] for x in u.taskbar_buttons())

            def changed(timeout=4.0, want=True):
                end_t = time.monotonic() + timeout
                while time.monotonic() < end_t:
                    w.pump()
                    now = sorted(x["name"] for x in u.taskbar_buttons())
                    if (now != base) == want:
                        return True
                    time.sleep(0.2)
                return False
            u32 = ctypes.windll.user32
            u32.SetWindowLongPtrW.restype = ctypes.c_ssize_t
            u32.SetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int, ctypes.c_ssize_t]
            u32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
            fg0 = WK.h2i(WK.GetForegroundWindow())

            def restyle(h, clear_app=True, hide_show=True, tool=True):
                if hide_show:
                    u32.ShowWindow(h, 0)
                ex = WK.GetWindowLongW(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | (WS_EX_TOOLWINDOW if tool else 0)
                if clear_app:
                    ex &= ~WS_EX_APPWINDOW
                u32.SetWindowLongPtrW(h, GWL_EXSTYLE, ex & 0xFFFFFFFF)
                u32.SetWindowPos(h, None, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x10 | 0x20)
                if hide_show:
                    u32.ShowWindow(h, 4)
            # On a monitor (the primary's bottom-right corner, under the taskbar): an off-screen window got no button
            sw, sh = u32.GetSystemMetrics(0), u32.GetSystemMetrics(1)
            pos = dict(x=sw - 48, y=sh - 36, w=40, h=30)
            # measured on this PC (first live runs): a WS_POPUP or captioned window shown without activation gets NO
 # button unless it has WS_EX_APPWINDOW (as a WinForms ShowInTaskbar window, and presumably Unity's, has)
            h0 = w.make(title + "_PLAIN", ex=0, **pos)
            print("INFO     plain window (no WS_EX_APPWINDOW) shown without activation: button %s" % (
                "APPEARS" if changed(timeout=3.0) else "absent"))
            w.close()
            w = WK._Wnd()
            h = w.make(title, ex=WS_EX_APPWINDOW, **pos)
            chk("live: WS_EX_APPWINDOW window -> taskbar button (UIA)", changed(), "rule: %s" % rule(WK.describe(h))["why"])
            restyle(h)
            chk("live: hide -> NOACTIVATE|TOOLWINDOW, APPWINDOW cleared -> FRAMECHANGED -> show: no button",
                changed(want=False))
            chk("live: rule agrees (not eligible)", not rule(WK.describe(h))["taskbar"])
            w.close()
            w = WK._Wnd()
            h2 = w.make(title + "_M", ex=WS_EX_APPWINDOW, **pos)
            changed()
            restyle(h2, clear_app=False, tool=False)
            time.sleep(0.5)
            chk("live mutant: restyle without WS_EX_TOOLWINDOW (APPWINDOW kept) -> the button stays (red)",
                changed(timeout=2.5, want=True) and rule(WK.describe(h2))["taskbar"])
            w.close()
            w = WK._Wnd()
            h3 = w.make(title + "_NS", ex=WS_EX_APPWINDOW, **pos)
            changed()
            restyle(h3, clear_app=True, hide_show=False)
            survived = changed(timeout=2.5, want=True)
            print("INFO     restyle of a SHOWN window without hide/show (APPWINDOW cleared): taskbar button %s "
                  "(R2.2 evidence)" % ("SURVIVES" if survived else "removed"))
            if survived:
                tb = _create(CLSID_TaskbarList, IID_ITaskbarList)
                try:
                    _vcall(tb, ITBL_HrInit)()
                    _vcall(tb, ITBL_DeleteTab, ctypes.c_void_p)(ctypes.c_void_p(h3))
                finally:
                    _release(tb)
                print("INFO     ITaskbarList::DeleteTab fallback: button %s" % (
                    "removed" if changed(timeout=3.0, want=False) else "STILL PRESENT"))
            chk("live: no test window took the foreground", WK.h2i(WK.GetForegroundWindow()) not in (h0, h, h2, h3),
                "fg before %s" % WK.hexh(fg0))
        finally:
            w.close()
    u.close()
    ok = all(results)
    print("UI_AUTOMATION SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="taskbar buttons / Alt+Tab entries (S2.N9) and foreground samples (S2.N10)")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--live-selftest", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    p = sub.add_parser("check")
    p.add_argument("--process", default="Subnautica.exe")
    p.add_argument("--pattern", default="Subnautica")
    p.add_argument("--json", action="store_true")
    p = sub.add_parser("buttons")
    p.add_argument("--all", action="store_true")
    p.add_argument("--pattern", default="Subnautica")
    p = sub.add_parser("fgwatch")
    p.add_argument("--seconds", type=float, default=60.0)
    p.add_argument("--interval", type=float, default=0.1)
    p.add_argument("--jsonl", default=None)
    p.add_argument("--forbid", default="Subnautica.exe")
    p.add_argument("--expect", default="Legacy.exe")
    a = ap.parse_args(argv)
    if os.name != "nt":
        return 4
    if a.selftest or a.live_selftest:
        return selftest(live=a.live_selftest)
    if a.cmd == "check":
        r = check(a.process, a.pattern)
        if a.json:
            print(json.dumps(r))
        else:
            print("S2.N9 %s: %s (UIA %s)" % ("GREEN" if r["green"] else ("NOT RUN" if not r["processRunning"] else "RED"),
                                             r["verdict"], "ok" if r["uia"] else r["uiaWhy"]))
            for w in r["windows"]:
                print("  %s %-20s visible=%s exstyle=%s taskbar=%s (%s)" % (w["hwnd"], w["cls"][:20], w["visible"],
                                                                          w["exstyle"], w["taskbar"], w["why"]))
            for b in r["buttons"] or []:
                print("  taskbar button: %s" % b["name"])
        return 0 if r["green"] else (2 if not r["processRunning"] else 1)
    if a.cmd == "buttons":
        u = Uia()
        if not u.ok:
            print("UIA unavailable: %s" % u.why)
            return 2
        for b in u.taskbar_buttons():
            if a.all or a.pattern.lower() in b["name"].lower():
                print("%-60s %s" % (b["name"][:60], b["cls"]))
        u.close()
        return 0
    if a.cmd == "fgwatch":
        r = fgwatch(a.seconds, a.interval, a.jsonl, a.forbid, a.expect)
        print(json.dumps(r))
        return 0 if not r["forbiddenSamples"] else 1
    ap.print_usage()
    return 4

if __name__ == "__main__":
    sys.exit(main())
