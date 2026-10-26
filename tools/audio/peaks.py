"""Per-process audio peak meters every 20 ms (ROADMAP S2 tools, S2.T-A1 oracle input; docs/recon/probe-display-input.md
5.3): the Windows audio-session peak meter (IAudioMeterInformation) of Legacy.exe (SE under Pulsar) and
Subnautica.exe, written to a JSONL file for tests/s2/analyze_ta1.py.

 python -I tools/audio/peaks.py --out tests/out/s2/x/peaks.jsonl [--seconds 120] [--interval 0.02]
 [--process Legacy.exe --process Subnautica.exe] [--backend auto|pycaw|ctypes|section]
 python -I tools/audio/peaks.py --list # current audio sessions: process name, peak (read-only)
 python -I tools/audio/peaks.py --selftest # works without pycaw (the fake backend, the file format, guards)
 python -I tools/audio/peaks.py --selftest-tone # + a 0.6 s tone at 1 % amplitude from this process, metered

Backends:
- pycaw (ROADMAP's tool; a pip download that is NOT approved yet). Imported behind a guard: when it is missing the
 tool says so and continues with the next backend; nothing is ever installed by this script.
- ctypes (stdlib only): the same Core Audio interfaces pycaw wraps (IMMDeviceEnumerator -> every active render
 endpoint -> IAudioSessionManager2 -> IAudioSessionEnumerator -> IAudioSessionControl2.GetProcessId,
 IAudioMeterInformation.GetPeakValue, ISimpleAudioVolume). `auto` (default) = pycaw if importable, else ctypes, so
 S2.T-A1 does not wait for the pip approval; pycaw stays a cross-check.
- section (stand-in dry runs only, flagged "emulated"): peaks derived from the shared section's C5/C7 (Subnautica =
 the C5 snBusTarget followed over crossfadeMs, 0 while C7 frozen or C5 Unfocused; SE = 0.4 unless Unfocused).
Sessions are re-enumerated every 2 s (a game's session appears when it first plays). A process with several sessions
(several endpoints or streams) reports the maximum. The meter is read-only: no volume or mute is ever changed.

File: one JSON line per sample {"ev": "peaks", "qpc", "p": {"Legacy.exe": peak, "Subnautica.exe": peak}, "v":
{process: [masterVolume, mute]}}; a first "peaks.start" line (backend, processes, interval, qpcFrequency, emulated)
and a last "peaks.end" line (samples, late samples, per-process maxima). Exit codes: 0 done / selftest PASS; 1
selftest FAIL; 2 no backend available; 4 usage.
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
import time

DEFAULT_PROCESSES = ("Legacy.exe", "Subnautica.exe")
PYCAW_MISSING = ("pycaw is not installed (the pip download needs the owner's approval, ROADMAP S2 owner actions); "
                 "continuing with the stdlib ctypes Core Audio backend")

# --------------------------------------------------------------------------------------------------------- ctypes COM
class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_ulong), ("Data2", ctypes.c_ushort), ("Data3", ctypes.c_ushort),
                ("Data4", ctypes.c_ubyte * 8)]

def _guid(s):
    g = GUID()
    ctypes.oledll.ole32.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(g))
    return g

def _vcall(ptr, index, *argtypes):
    vtbl = ctypes.cast(ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)))[0]
    fn = ctypes.WINFUNCTYPE(ctypes.HRESULT, ctypes.c_void_p, *argtypes)(vtbl[index])
    return lambda *a: fn(ptr, *a)

def _release(ptr):
    if ptr:
        vtbl = ctypes.cast(ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)))[0]
        ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtbl[2])(ptr)

def _qi(ptr, iid):
    out = ctypes.c_void_p()
    _vcall(ptr, 0, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))(ctypes.byref(_guid(iid)), ctypes.byref(out))
    return out.value

CLSID_MMDeviceEnumerator = "{BCDE0395-E52F-467C-8E3D-C4579291692E}"
IID_IMMDeviceEnumerator = "{A95664D2-9614-4F35-A746-DE8DB63617E6}"
IID_IAudioSessionManager2 = "{77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F}"
IID_IAudioSessionControl2 = "{BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D}"
IID_IAudioMeterInformation = "{C02216F6-8C67-4B5B-9D00-D008E73E0064}"
IID_ISimpleAudioVolume = "{87CE5498-68D6-44E5-9215-6DA47EF883D8}"

def _image(pid):
    k = ctypes.windll.kernel32
    h = k.OpenProcess(0x1000, False, pid)
    if not h:
        return None
    try:
        buf = ctypes.create_unicode_buffer(520)
        n = wt.DWORD(520)
        if k.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
            return os.path.basename(buf.value)
        return None
    finally:
        k.CloseHandle(h)

class CtypesBackend:
    name = "ctypes"
    emulated = False

    def __init__(self):
        try:
            ctypes.oledll.ole32.CoInitializeEx(None, 0)       # COINIT_MULTITHREADED
        except OSError:
            pass
        p = ctypes.c_void_p()
        ctypes.oledll.ole32.CoCreateInstance(ctypes.byref(_guid(CLSID_MMDeviceEnumerator)), None, 23,
                                             ctypes.byref(_guid(IID_IMMDeviceEnumerator)), ctypes.byref(p))
        self.enum = p.value
        self.sessions = []        # [(image, pid, meter, volume, control)]

    def refresh(self):
        for _i, _p, m, v, c in self.sessions:
            _release(m)
            _release(v)
            _release(c)
        self.sessions = []
        coll = ctypes.c_void_p()
        _vcall(self.enum, 3, ctypes.c_int, wt.DWORD, ctypes.POINTER(ctypes.c_void_p))(0, 1, ctypes.byref(coll))
        try:
            n = wt.UINT()
            _vcall(coll.value, 3, ctypes.POINTER(wt.UINT))(ctypes.byref(n))
            for i in range(n.value):
                dev = ctypes.c_void_p()
                _vcall(coll.value, 4, wt.UINT, ctypes.POINTER(ctypes.c_void_p))(i, ctypes.byref(dev))
                try:
                    mgr = ctypes.c_void_p()
                    _vcall(dev.value, 3, ctypes.POINTER(GUID), wt.DWORD, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p))(
                        ctypes.byref(_guid(IID_IAudioSessionManager2)), 23, None, ctypes.byref(mgr))
                    try:
                        self._sessions_of(mgr.value)
                    finally:
                        _release(mgr.value)
                finally:
                    _release(dev.value)
        finally:
            _release(coll.value)

    def _sessions_of(self, mgr):
        en = ctypes.c_void_p()
        _vcall(mgr, 5, ctypes.POINTER(ctypes.c_void_p))(ctypes.byref(en))
        try:
            n = ctypes.c_int()
            _vcall(en.value, 3, ctypes.POINTER(ctypes.c_int))(ctypes.byref(n))
            for i in range(n.value):
                ctl = ctypes.c_void_p()
                _vcall(en.value, 4, ctypes.c_int, ctypes.POINTER(ctypes.c_void_p))(i, ctypes.byref(ctl))
                try:
                    c2 = _qi(ctl.value, IID_IAudioSessionControl2)
                    pid = wt.DWORD()
                    _vcall(c2, 14, ctypes.POINTER(wt.DWORD))(ctypes.byref(pid))
                    img = _image(pid.value) if pid.value else "System sounds"
                    meter = _qi(ctl.value, IID_IAudioMeterInformation)
                    vol = _qi(ctl.value, IID_ISimpleAudioVolume)
                    self.sessions.append((img or "?", int(pid.value), meter, vol, c2))
                finally:
                    _release(ctl.value)
        finally:
            _release(en.value)

    def read(self, targets):
        peaks, vols = {}, {}
        low = {t.lower(): t for t in targets} if targets else None
        for img, _pid, meter, vol, _c in self.sessions:
            key = low.get(img.lower()) if low is not None else img
            if key is None:
                continue
            v = ctypes.c_float()
            try:
                _vcall(meter, 3, ctypes.POINTER(ctypes.c_float))(ctypes.byref(v))
            except OSError:
                continue
            peaks[key] = max(peaks.get(key, 0.0), float(v.value))
            mv, mu = ctypes.c_float(), wt.BOOL()
            try:
                _vcall(vol, 4, ctypes.POINTER(ctypes.c_float))(ctypes.byref(mv))
                _vcall(vol, 6, ctypes.POINTER(wt.BOOL))(ctypes.byref(mu))
                vols[key] = [round(float(mv.value), 4), bool(mu.value)]
            except OSError:
                pass
        return peaks, vols

    def close(self):
        for _i, _p, m, v, c in self.sessions:
            _release(m)
            _release(v)
            _release(c)
        self.sessions = []
        _release(self.enum)

class PycawBackend:
    name = "pycaw"
    emulated = False

    def __init__(self):
        from pycaw.pycaw import AudioUtilities, IAudioMeterInformation, ISimpleAudioVolume  # noqa: F401 (guarded)
        self.AU, self.IM, self.IV = AudioUtilities, IAudioMeterInformation, ISimpleAudioVolume
        self.sessions = []

    def refresh(self):
        self.sessions = []
        for s in self.AU.GetAllSessions():
            img = s.Process.name() if s.Process else "System sounds"
            self.sessions.append((img, s._ctl.QueryInterface(self.IM), s.SimpleAudioVolume))

    def read(self, targets):
        peaks, vols = {}, {}
        low = {t.lower(): t for t in targets} if targets else None
        for img, meter, vol in self.sessions:
            key = low.get(img.lower()) if low is not None else img
            if key is None:
                continue
            peaks[key] = max(peaks.get(key, 0.0), float(meter.GetPeakValue()))
            vols[key] = [round(float(vol.GetMasterVolume()), 4), bool(vol.GetMute())]
        return peaks, vols

    def close(self):
        self.sessions = []

class SectionBackend:
    """Stand-in dry runs only: peaks emulated from the section (no audio is measured)."""
    name = "section"
    emulated = True

    def __init__(self, section_name=None):
        root = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
        wire = os.path.join(root, "tests", "wire")
        if wire not in sys.path:
            sys.path.insert(0, wire)
        import layout_py as L  # noqa: E402
        import section_py as S  # noqa: E402
        self.L, self.S = L, S
        self.name_ = section_name or L.SECTION_NAME
        self.sec = None
        self.bus = 0.0
        self.t = time.perf_counter()

    def refresh(self):
        if self.sec is None:
            self.sec = self.S.Section.open(self.name_, readonly=True)

    def read(self, targets):
        L, S = self.L, self.S
        now = time.perf_counter()
        dt, self.t = now - self.t, now
        if self.sec is None:
            return {}, {}
        _st, _q, c5 = S.seqlock_read(self.sec, L.REGION["C5"].offset, L.SE_STATE.size)
        _st, _q, c7 = S.seqlock_read(self.sec, L.REGION["C7"].offset, L.SN_STATUS.size)
        c5 = L.SE_STATE.unpack(c5) if c5 else {}
        c7 = L.SN_STATUS.unpack(c7) if c7 else {}
        unfocused = bool(c5.get("overlays", 0) & (1 << L.OVERLAYS["Unfocused"]))
        frozen = bool(c7.get("flags", 0) & (1 << L.SN_STATUS_FLAGS["frozen"]))
        target = 0.0 if unfocused or frozen else float(c5.get("snBusTarget", 0.0))
        step = dt * 1000.0 / max(1.0, float(c5.get("crossfadeMs", 300.0) or 300.0))
        self.bus = min(target, self.bus + step) if target > self.bus else max(target, self.bus - step)
        out = {}
        for t in targets:
            if "subnautica" in t.lower():
                out[t] = round(0.5 * self.bus, 4)
            else:
                out[t] = 0.0 if unfocused else round(0.4 * (1.0 - 0.3 * self.bus), 4)
        return out, {}

    def close(self):
        if self.sec:
            self.sec.close()

class FakeBackend:
    """Selftest only: a scripted sequence of peaks."""
    name = "fake"
    emulated = True

    def __init__(self, script):
        self.script = list(script)
        self.i = 0

    def refresh(self):
        pass

    def read(self, targets):
        v = self.script[min(self.i, len(self.script) - 1)]
        self.i += 1
        return {t: v.get(t, 0.0) for t in targets}, {}

    def close(self):
        pass

def make_backend(kind, section_name=None, say=print):
    if kind in ("auto", "pycaw"):
        try:
            return PycawBackend()
        except ImportError:
            if kind == "pycaw":
                say("peaks: " + PYCAW_MISSING.split(";")[0] + " (--backend pycaw requested): no backend")
                return None
            say("peaks: " + PYCAW_MISSING)
        except Exception as e:   # pycaw present but broken (comtypes): fall back
            if kind == "pycaw":
                say("peaks: pycaw failed: %s" % e)
                return None
            say("peaks: pycaw failed (%s); continuing with the ctypes backend" % e)
    if kind in ("auto", "ctypes"):
        if os.name != "nt":
            return None
        return CtypesBackend()
    if kind == "section":
        return SectionBackend(section_name)
    return None

def qpc_now():
    c = ctypes.c_int64()
    ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(c))
    return c.value

def qpc_freq():
    f = ctypes.c_int64()
    ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(f))
    return f.value

def run(backend, out, seconds, interval, targets, stop=None, clock=None) -> dict:
    """Samples until `seconds` elapse (or stop is true); returns the end record."""
    clock = clock or qpc_now
    f = open(out, "a", encoding="utf-8") if out else None
    maxima = {t: 0.0 for t in targets}
    n = late = 0
    try:
        if f:
            f.write(json.dumps({"ev": "peaks.start", "backend": backend.name, "emulated": backend.emulated,
                                "processes": list(targets), "intervalS": interval, "qpc": clock(),
                                "qpcFrequency": qpc_freq() if os.name == "nt" else 10_000_000,
                                "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}) + "\n")
        try:
            ctypes.windll.winmm.timeBeginPeriod(1)
        except (OSError, AttributeError):
            pass
        backend.refresh()
        t0 = time.perf_counter()
        next_t, next_refresh = t0, t0 + 2.0
        while (seconds <= 0 or time.perf_counter() - t0 < seconds) and not (stop and stop()):
            now = time.perf_counter()
            if now >= next_refresh:
                backend.refresh()
                next_refresh = now + 2.0
            peaks, vols = backend.read(targets)
            q = clock()
            for t in targets:
                maxima[t] = max(maxima[t], peaks.get(t, 0.0))
            rec = {"ev": "peaks", "qpc": q, "p": {t: round(peaks.get(t, 0.0), 5) for t in targets}}
            if vols:
                rec["v"] = vols
            if f:
                f.write(json.dumps(rec, separators=(",", ":")) + "\n")
                if n % 50 == 49:
                    f.flush()          # about once per second: a stopped run loses at most one second
            n += 1
            next_t += interval
            d = next_t - time.perf_counter()
            if d > 0:
                time.sleep(d)
            else:
                late += 1
                next_t = time.perf_counter()
        end = {"ev": "peaks.end", "samples": n, "late": late, "max": maxima, "qpc": clock()}
        if f:
            f.write(json.dumps(end) + "\n")
        return end
    finally:
        try:
            ctypes.windll.winmm.timeEndPeriod(1)
        except (OSError, AttributeError):
            pass
        if f:
            f.close()

def selftest() -> int:
    import tempfile
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-66s %s %s" % (label, "PASS" if cond else "FAIL", detail))
    msgs = []
    try:
        import pycaw  # noqa: F401
        have = True
    except ImportError:
        have = False
    if not have:
        b = make_backend("pycaw", say=msgs.append)
        check("import guard: --backend pycaw without pycaw -> no backend, clear message",
              b is None and msgs and "not installed" in msgs[0], msgs[0] if msgs else "")
        msgs.clear()
        b = make_backend("auto", say=msgs.append)
        check("import guard: --backend auto without pycaw -> ctypes backend + message",
              b is not None and b.name == "ctypes" and msgs and "ctypes" in msgs[0])
        if b:
            b.close()
    else:
        print("INFO     pycaw is installed: the import-guard path is not exercised")
    fake = FakeBackend([{"Legacy.exe": 0.3, "Subnautica.exe": 0.0}] * 5 + [{"Legacy.exe": 0.2, "Subnautica.exe": 0.6}] * 5)
    path = os.path.join(tempfile.mkdtemp(prefix="de_peaks_"), "peaks.jsonl")
    clock_v = [1000]

    def clock():
        clock_v[0] += 200_000
        return clock_v[0]
    end = run(fake, path, 0.22, 0.02, list(DEFAULT_PROCESSES), clock=clock)
    lines = [json.loads(x) for x in open(path, encoding="utf-8")]
    check("file: start line, samples every 20 ms, end line", lines[0]["ev"] == "peaks.start" and
          lines[-1]["ev"] == "peaks.end" and 8 <= end["samples"] <= 13, "%d samples" % end["samples"])
    check("file: maxima per process", abs(end["max"]["Subnautica.exe"] - 0.6) < 1e-9 and
          abs(end["max"]["Legacy.exe"] - 0.3) < 1e-9)
    check("file: QPC stamps increase", all(a["qpc"] < b["qpc"] for a, b in zip(lines[1:-1], lines[2:-1])))
    if os.name == "nt":
        try:
            b = CtypesBackend()
            b.refresh()
            peaks, _v = b.read(None)
            check("ctypes Core Audio backend: sessions enumerated (read-only)", True,
                  "%d sessions, %d processes (names not printed)" % (len(b.sessions), len(peaks)))
            b.close()
        except OSError as e:
            check("ctypes Core Audio backend: sessions enumerated (read-only)", False, str(e))
    ok = all(results)
    print("PEAKS SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def selftest_tone() -> int:
    """Plays a 0.6 s, 440 Hz tone at 1 % amplitude from this process (winsound, stdlib) and checks that the ctypes
 backend reads a peak > 0 on python.exe's own session, then 0 again after the tone (the meter path end to end)."""
    import io
    import math
    import struct
    import threading
    import wave
    import winsound
    rate, secs, amp = 44100, 0.6, 0.01
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", int(32767 * amp * math.sin(2 * math.pi * 440 * i / rate)))
                               for i in range(int(rate * secs))))
    data = buf.getvalue()
    me = os.path.basename(sys.executable)
    b = CtypesBackend()
    seen = []
    try:
        th = threading.Thread(target=lambda: winsound.PlaySound(data, winsound.SND_MEMORY), daemon=True)
        th.start()
        t0 = time.perf_counter()
        while time.perf_counter() - t0 < 1.6:
            b.refresh()
            p, _v = b.read([me])
            seen.append((round(time.perf_counter() - t0, 2), p.get(me, 0.0)))
            time.sleep(0.05)
        th.join(2.0)
    finally:
        b.close()
    during = max((v for t, v in seen if 0.15 <= t <= 0.5), default=0.0)
    after = max((v for t, v in seen if t >= 1.2), default=1.0)
    ok = during > 0.0 and after == 0.0
    print("SELFTEST tone: own session peak during the tone %.5f (> 0), after it %.5f (= 0)   %s" % (
        during, after, "PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="per-process audio peak meters (S2.T-A1)")
    ap.add_argument("--out", default=None)
    ap.add_argument("--seconds", type=float, default=60.0, help="0: until interrupted")
    ap.add_argument("--interval", type=float, default=0.02)
    ap.add_argument("--process", action="append", default=[])
    ap.add_argument("--backend", default="auto", choices=("auto", "pycaw", "ctypes", "section"))
    ap.add_argument("--name", default=None, help="section name (backend section)")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--selftest-tone", action="store_true", help="also play a 0.6 s tone at 1 %% amplitude and meter it")
    a = ap.parse_args(argv)
    if a.selftest or a.selftest_tone:
        rc = selftest()
        if a.selftest_tone and os.name == "nt":
            rc = selftest_tone() or rc
        return rc
    b = make_backend(a.backend, a.name, say=lambda m: sys.stderr.write(m + "\n"))
    if b is None:
        return 2
    try:
        if a.list:
            b.refresh()
            peaks, vols = b.read(None)
            for k in sorted(peaks):
                print("%-32s peak %.4f %s" % (k, peaks[k], vols.get(k, "")))
            return 0
        if not a.out:
            ap.error("--out is required")
        end = run(b, a.out, a.seconds, a.interval, a.process or list(DEFAULT_PROCESSES))
        print(json.dumps(end))
        return 0
    except KeyboardInterrupt:
        return 0
    finally:
        b.close()

if __name__ == "__main__":
    sys.exit(main())
