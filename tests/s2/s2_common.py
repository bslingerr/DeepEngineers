"""Shared helpers of the S2 analysers (tests/s2/analyze_*.py): the S2 JSONL event schema, a collected session folder,
oracle rows and the plain-text table. Stdlib only, `python -I` friendly (the analysers put this folder on sys.path).

S2 JSONL EVENT SCHEMA ("s2/1"; the S1 conventions of tests/s1/check_wm.py: one JSON object per line, the event name
in "ev" (SE plugin, stand-ins) or "event" (SN plugin), the payload top-level, inside "data", or as a JSON object
written after the event name in the "msg"/"text" line, e.g. `s2.state {"tick",...}`). `qpc` values are raw
QueryPerformanceCounter counts (one clock for both games and the launcher on this PC). View states and approach modes
may be written as names (Space, Approach, Underwater, Surfacing; None, Track, Prime) or as the C5 numbers.
SE side (se-*.jsonl):
 s2.state tick, qpc, from, to, fromMode, toMode (approach modes), y, vy, rho, crossingTick, reason, dwellS,
 snReady, zOrderOk, presentedSeTick (one per view-state or approach-mode transition)
 s2.display tick, qpc, action (marginsOn | marginsOff | skipOn | skipOff | alphaFillOn | alphaFillOff | toggle |
 reassert), hr (HRESULT of the DWM call, int), renderFrame, presentQpc (QPC after the first Present that
 carries the change), updateUs (update-thread cost of the request), source (F8 | de.display | state)
 s2.present renderFrame, qpc, sceneSkipped, marginsOn (render frames around each display change)
 s2.zorder tick, qpc, ok, retries, firstHwnd, snHwnd, seHwnd, skipped [..] (walker result of the plugin)
 s2.fg tick, qpc, isSe, fgHwnd, seHwnd (GetForegroundWindow == seHwnd, per toggle)
 s2.alpha tick, qpc, mode (seeThrough | scene), menuOpen, fracAlphaGt0, fracAlphaLt1, pixels (dev readback)
 s2.perf tick, qpc, simRatio, updateMsP50, updateMsP99, frameMsP50, frameMsP99, fps, gpuFrameMs, viewState
 s2.audio tick, qpc, viewState, game, music, hud, configGame, configMusic, configHud, focused, paused, guiOpen,
 written (the plugin wrote MyAudio volumes this tick), waterMod (emitter scale)
SN side (sn-*.jsonl):
 s2.perf frame, qpc, snFrame, fps, perfModeApplied, vSyncCount, targetFrameRate, frozen, timeScale, camerasOn,
 frameMsP50, frameMsP99 (1 Hz)
 s2.mix qpc, busVolume, target, muted, paused, freeze (on changes)
 s2.window qpc, hwnd, rect [4], exstyle, rectOk, displayIndex (1 Hz and on changes)
 s2.calib on, colour [3]
Launcher session log (tests/out/s2/<stamp>/s2-session.jsonl, written by `launcher.py s2`): start, part (begin/end),
cmd/reply, toggle (index, qpc, reply, fg {pid, hwnd, isSe}, walker {ok, first, skipped, chain}), sample (10 Hz
section reads: c0 heartbeats and phases, c5 view state and flags, c7 snFrame/presentedSeTick/flags, c1Tick),

"""
from __future__ import annotations

import json
import os
import re
import sys

VIEW_STATES = ("Space", "Approach", "Underwater", "Surfacing")
APPROACH_MODES = ("None", "Track", "Prime")
PERF_MODES = ("Idle", "Track", "Full")
_EMBED = re.compile(r"\b(s2\.[A-Za-z0-9_]+)\s+(\{.*\})\s*$")
_KV = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=(\([^)]*\)|\[[^\]]*\]|\S+)")
# Plugin text lines that carry the same payload as key=value pairs (fallback when a record is not structured)
TEXT_EVENTS = {"DE.State": "s2.state", "DE.Foreground": "s2.fg", "DE.Alpha": "s2.alpha", "DE.Perf2": "s2.perf",
               "DE.Mix": "s2.mix", "DE.Window2": "s2.window"}
BAD = ("FAIL", "MISSING")

def here_paths():
    """Puts tests/wire, tests/s1, tests/s2, tools/* on sys.path (python -I does not add the script folder)."""
    h = os.path.dirname(os.path.abspath(__file__))
    root = os.path.normpath(os.path.join(h, "..", ".."))
    for p in (h, os.path.join(root, "tests", "wire"), os.path.join(root, "tests", "s1"),
              os.path.join(root, "tools", "display"), os.path.join(root, "tools", "capture"),
              os.path.join(root, "tools", "audio")):
        if p not in sys.path:
            sys.path.insert(0, p)
    return root

ROOT = here_paths()

def read_jsonl(paths) -> list:
    """Every JSON object of the files (non-JSON lines skipped)."""
    out = []
    if isinstance(paths, str):
        paths = [paths]
    for path in paths or ():
        if not path or not os.path.exists(path):
            continue
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line.startswith("{"):
                    continue
                try:
                    rec = json.loads(line)
                except ValueError:
                    continue
                if isinstance(rec, dict):
                    out.append(rec)
    return out

def _value(s: str):
    s = s.strip().rstrip(",;")
    if s.startswith("(") or s.startswith("["):
        try:
            return [float(x) for x in s.strip("()[]").split(",") if x.strip()]
        except ValueError:
            return s
    low = s.lower()
    if low in ("true", "false"):
        return low == "true"
    try:
        if low.startswith("0x"):
            return int(s, 16)
        return int(s)
    except ValueError:
        pass
    try:
        return float(s)
    except ValueError:
        return s

def _kv(body: str) -> dict:
    return {k: _value(v) for k, v in _KV.findall(body)}

_RX_TOGGLE = re.compile(r"^DE\.Display toggle #(\d+) see-through (ON|OFF) via (\S+)")
_RX_SKIPLAT = re.compile(r"^DE\.Display see-through first skipped frame presented \+([0-9.]+) ms after the request")
_RX_ALPHA = re.compile(r"^DE\.Display alpha (\S+) .*?pixels=(\d+) alphaGt0=(\d+) \(([0-9.]+) %\) alphaLt1=(\d+) "
                       r"\(([0-9.]+) %\)")
_RX_MARGINS = re.compile(r"^DE\.Display margins (ON|OFF) \([-0-9]+\) hr=(\S+)( \(MUTANT S2\.M1)?")
_RX_MARGINS_FAIL = re.compile(r"^DE\.Display DwmExtendFrameIntoClientArea\((-?\d+)\) FAILED hr=(\S+)")
_RX_VIEW = re.compile(r"^DE\.View (\w+) -> (\w+) (.*)$")
_RX_ZORDER = re.compile(r"^DE\.ZOrder (OK|LOST|FAIL|retry)")
_RX_AUDIO_SE = re.compile(r"^DE\.Audio gate=(\w+) \((.*?)\) mix=(\S+) target=(\S+) seMixActive=(\S+) game=(\S+) "
                          r"music=(\S+) hud=(\S+) \(config game (\S+) music (\S+)\)")
# The SE plugin's own formats:
# DE.View -> <State> (<reason>) tick=N y=… vy=… rho=… zone=… snReady=True blocked=… perf=Full/0 request=0x5 renderSeq=N
# ack=N[ crossing->entry X ms (presentedSeTick P >= crossingTick C)] (ViewStateDriver.LogStep)
# DE.View Space (<why>; the machine restarts with a world-load event) (ViewStateDriver.NoFrame)
# DE.Audio gate=open|closed write=B bridgeUp=B mix=M seMixActive=B game=G music=M hud=H waterMod=W (config game G
# music M)[ MUTANT S2.M11: …] (AudioController.Tick)
_RX_VIEW_SE = re.compile(r"^DE\.View -> (\w+) \(([^)]*)\) (.*)$")
_RX_VIEW_RESET = re.compile(r"^DE\.View Space \(")
_RX_AUDIO_SE_PLUGIN = re.compile(r"^DE\.Audio gate=(open|closed) write=(\S+) bridgeUp=(\S+) mix=(\S+) seMixActive=(\S+) "
                                 r"game=(\S+) music=(\S+) hud=(\S+) waterMod=(\S+) \(config game (\S+) music (\S+)\)")
_RX_S2SEC = re.compile(r"^DE\.S2 sec (.*)$")
_RX_C5 = re.compile(r"(\w+)(?:/(Track|Prime|None))?/(\w+)/(-?\d+)")
_RX_SN_FADE = re.compile(r"^DE\.Audio fade (\S+) -> (\S+) over (\S+) ms")
_RX_SN_MUTE = re.compile(r"^DE\.Audio (MUTED|unmuted) \((.*?)\) bus:/ volume (\S+) target (\S+)")
_RX_FREEZE = re.compile(r"^DE\.Freeze .*?frozen=(true|false|True|False)")
_RX_CALIB = re.compile(r"^DE\.Calib (ON|OFF)(?: colour=(\S+))?")
# review fix (finding 1): the composed-pixel probe verdict, the design switch and the autoFallback switch
_RX_PROBE = re.compile(r"^DE\.Display probe (START|HONOURED|NOT HONOURED|UNDECIDED|MIXED|MEASURED|probe TIMEOUT|"
                       r"probe refused|probe GDI read failed)\b(.*)$")
_RX_MODE = re.compile(r"^DE\.Display mode (\S+) -> (\S+) \((.*)\)\s*$")
_RX_AUTOFALLBACK = re.compile(r"^DE\.Display autoFallback (on|off) via (\S+)")
# re-dive fix: the band-hold diagnostic of the SE plugin (ViewStateDriver.BandHold; Prime with the camera
# inside the waterline band -0.20 < y <= -0.05 m for more than 1 s; behaviour unchanged):
# DE.View band hold #N start|continues: Prime with y in (-0.2, -0.05] m for S s (...) tick=T y=Y vy=V snReady=B
# blocked=… heldS=S
# DE.View band hold #N ended after S s: <how> tick=T y=Y vy=V
# DE.View band hold #N ended (<not in world | site not bound>)
_RX_BANDHOLD = re.compile(r"^DE\.View band hold #(\d+) (start|continues|ended)\b(.*)$")

def _boolish(v):
    if isinstance(v, str):
        return v.lower().startswith("true")
    return v

def _from_text(text: str):
    """The plugins' DE.* text lines -> (event name, payload); unknown lines -> None.
 SE: DE.Display toggle / margins / alpha / see-through first skipped frame, DE.View A -> B, DE.ZOrder, DE.Audio gate=.
 SN: DE.S2 sec, DE.Audio fade / MUTED / unmuted, DE.Freeze, DE.Calib."""
    m = _RX_TOGGLE.match(text)
    if m:
        d = _kv(text)
        um = d.get("updateMs")
        return "s2.display", {"action": "toggle", "index": int(m.group(1)), "seeThrough": m.group(2) == "ON",
                              "via": m.group(3), "isSe": _boolish(d.get("foregroundIsSe")), "updateMs": um,
                              "updateUs": um * 1000.0 if isinstance(um, (int, float)) else None}
    m = _RX_SKIPLAT.match(text)
    if m:
        return "s2.display", {"action": "skipPresented", "requestToPresentMs": float(m.group(1))}
    m = _RX_ALPHA.match(text)
    if m:
        # "DE.Display alpha mode=F label=<l> margins=on scene=skipped|drawn|none alphaFill=done|no WxH format=..."
        kv = _kv(text.split(" pixels=", 1)[0])
        scene = str(kv.get("scene", ""))
        label = str(kv.get("label", ""))
        low = (label + " " + m.group(1)).lower()
        mode = "seeThrough" if scene == "skipped" else ("scene" if scene == "drawn" else (
            "seeThrough" if ("see" in low or "skip" in low or "under" in low) else "scene"))
        return "s2.alpha", {"context": m.group(1), "label": label, "scene": scene, "margins": kv.get("margins"),
                            "alphaFill": kv.get("alphaFill"), "pixels": int(m.group(2)),
                            "fracAlphaGt0": float(m.group(4)) / 100.0, "fracAlphaLt1": float(m.group(6)) / 100.0,
                            "mode": mode, "menuOpen": "menu" in low or "gui" in low}
    m = _RX_MARGINS.match(text)
    if m:
        hr = _value(m.group(2))
        d = {"action": "marginsOn" if m.group(1) == "ON" else "marginsOff",
             "hr": hr if isinstance(hr, int) else (0 if "S_OK" in text else hr), "mutantNoCall": bool(m.group(3))}
        kv = _kv(text)
        if "presents" in kv:
            d["presents"] = kv["presents"]
        return "s2.display", d
    m = _RX_MARGINS_FAIL.match(text)
    if m:
        return "s2.display", {"action": "marginsOn" if m.group(1) == "-1" else "marginsOff", "hr": _value(m.group(2)),
                              "failed": True}
    m = _RX_BANDHOLD.match(text)
    if m:
        rest = m.group(3)
        keep = ("tick", "y", "vy", "snReady", "blocked", "heldS") if m.group(2) != "ended" else ("tick", "y", "vy")
        d = {k: v for k, v in _kv(rest).items() if k in keep}
        d.update({"index": int(m.group(1)), "phase": m.group(2)})
        if "snReady" in d:
            d["snReady"] = _boolish(d["snReady"])
        a = re.search(r"ended after ([0-9.]+) s: (.*?)(?: tick=|$)", text)
        if a:
            d["heldS"], d["how"] = float(a.group(1)), a.group(2)
        elif m.group(2) == "ended":
            d["how"] = rest.strip(" ()")
        return "s2.bandhold", d
    m = _RX_VIEW.match(text)
    if m and m.group(1) in ("Space", "Track", "Prime", "Underwater", "Surfacing", "Approach"):
        rest = m.group(3)
        d = _kv(rest)
        r = re.search(r"\(([^()]*)\)(?: crossing->entry ([0-9.]+) ms)?", rest.split("presentedSeTick=", 1)[-1])
        d.update({"from": m.group(1), "to": m.group(2)})
        if r:
            d["reason"] = r.group(1)
            if r.group(2):
                d["crossingToEntryMs"] = float(r.group(2))
        for k in ("snReady", "zOrderOk", "marginsReady"):
            if k in d:
                d[k] = _boolish(d[k])
        return "s2.state", d
    m = _RX_VIEW_SE.match(text)
    if m and m.group(1) in ("Space", "Track", "Prime", "Underwater", "Surfacing"):
        rest = m.group(3)
        d = _kv(rest.split(" crossing->entry ", 1)[0])
        d.update({"from": None, "to": m.group(1), "reason": m.group(2)})   # "from": filled in log order by events
        c = re.search(r"crossing->entry (-?[0-9.]+) ms", rest)
        if c:
            d["crossingToEntryMs"] = float(c.group(1))
        c = re.search(r"presentedSeTick (\d+) >= crossingTick (\d+)", rest)
        if c:
            d["presentedSeTick"], d["crossingTick"] = int(c.group(1)), int(c.group(2))
        if "snReady" in d:
            d["snReady"] = _boolish(d["snReady"])
        return "s2.state", d
    if _RX_VIEW_RESET.match(text):
        return "s2.viewreset", {"to": "Space"}
    m = _RX_AUDIO_SE_PLUGIN.match(text)
    if m:
        return "s2.audio", {"gate": m.group(1), "written": _boolish(m.group(2)), "bridgeUp": _boolish(m.group(3)),
                            "mix": _value(m.group(4)), "seMixActive": _boolish(m.group(5)), "game": _value(m.group(6)),
                            "music": _value(m.group(7)), "hud": _value(m.group(8)), "waterMod": _value(m.group(9)),
                            "configGame": _value(m.group(10)), "configMusic": _value(m.group(11)),
                            "mutantM11": "MUTANT S2.M11" in text}
    m = _RX_ZORDER.match(text)
    if m:
        return "s2.zorder", {"ok": m.group(1) == "OK", "result": m.group(1)}
    m = _RX_AUDIO_SE.match(text)
    if m:
        gate = _kv(m.group(2))
        return "s2.audio", {"gate": m.group(1), "focused": _boolish(gate.get("focused")),
                            "paused": _boolish(gate.get("paused")), "guiOpen": _boolish(gate.get("gui")),
                            "mix": _value(m.group(3)), "target": _value(m.group(4)),
                            "seMixActive": _boolish(_value(m.group(5))), "game": _value(m.group(6)),
                            "music": _value(m.group(7)), "hud": _value(m.group(8)), "configGame": _value(m.group(9)),
                            "configMusic": _value(m.group(10)), "written": m.group(1) == "open"}
    m = _RX_S2SEC.match(text)
    if m:
        body = m.group(1)
        d = _kv(body)
        ap = re.search(r"applied=\((-?\d+), (-?\d+)\)", body)
        # c5=<view>/<perf>/<fps>; Approach states read "Approach/Prime/Full/0" (S2Host.SecondLine)
        c5 = _RX_C5.match(str(d.get("c5", "")))
        out = {"side": "sn", "phase": d.get("phase"), "fps": d.get("fps"), "maxGapMs": d.get("maxGapMs"),
               "frozen": _boolish(d.get("frozen")), "timeScale": d.get("timeScale"),
               "camerasOn": d.get("cameras") == "on", "rectOk": _boolish(d.get("rectOk")),
               "presentedSeTick": d.get("presentedSeTick"), "framesPresented": d.get("framesPresented")}
        if ap:
            out["vSyncCount"], out["targetFrameRate"] = int(ap.group(1)), int(ap.group(2))
        if c5:
            view = c5.group(2) if c5.group(1) == "Approach" and c5.group(2) else c5.group(1)
            out["view"], out["perfMode"], out["targetFps"] = view, c5.group(3), int(c5.group(4))
        g = re.search(r"gaps>1s=(\d+)", body)
        if g:
            out["gapsOver1s"] = int(g.group(1))
        return "s2.perf", out
    m = _RX_SN_FADE.match(text)
    if m:
        return "s2.mix", {"busVolume": _value(m.group(1)), "target": _value(m.group(2)), "fadeMs": _value(m.group(3))}
    m = _RX_SN_MUTE.match(text)
    if m:
        return "s2.mix", {"muted": m.group(1) == "MUTED", "why": m.group(2), "busVolume": _value(m.group(3)),
                          "target": _value(m.group(4))}
    m = _RX_FREEZE.match(text)
    if m:
        return "s2.freeze", {"frozen": m.group(1).lower() == "true"}
    m = _RX_CALIB.match(text)
    if m:
        return "s2.calib", {"on": m.group(1) == "ON", "colour": m.group(2)}
    m = _RX_PROBE.match(text)
    if m:
        word = m.group(1)
        if word == "START":
            return "s2.display", {"action": "probeStart"}
        verdict = {"HONOURED": "HONOURED", "NOT HONOURED": "NOT HONOURED"}.get(word, "UNDECIDED")
        return "s2.display", {"action": "probe", "verdict": verdict, "word": word, "text": text[len("DE.Display probe "):],
                              "standin": "stand-in" in text}
    m = _RX_MODE.match(text)
    if m:
        return "s2.display", {"action": "mode", "from": m.group(1), "to": m.group(2), "why": m.group(3)}
    m = _RX_AUTOFALLBACK.match(text)
    if m:
        return "s2.display", {"action": "autoFallback", "on": m.group(1) == "on", "via": m.group(2)}
    head = text.split(" ", 1)[0]
    name = TEXT_EVENTS.get(head)
    if not name:
        return None
    body = text[len(head):]
    data = _kv(body)
    m = re.search(r"\b([A-Za-z]+)\s*->\s*([A-Za-z]+)\b", body)
    if m and name == "s2.state":
        data.setdefault("from", m.group(1))
        data.setdefault("to", m.group(2))
    return name, data

def normalise(rec: dict, prefixes=("s2.",)):
    """One raw record -> an event dict with "_ev" and "_t", or None. Also accepts s1.* events (c1trace frames)."""
    name = rec.get("ev") or rec.get("event")
    data = None
    if isinstance(name, str) and any(name.startswith(p) for p in prefixes):
        data = dict(rec["data"]) if isinstance(rec.get("data"), dict) else \
            {k: v for k, v in rec.items() if k not in ("ev", "event", "data")}
    text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
    if isinstance(text, str):
        m = _EMBED.search(text)
        if m and any(m.group(1).startswith(p) for p in prefixes):
            try:
                emb = json.loads(m.group(2))
            except ValueError:
                emb = None
            if isinstance(emb, dict):
                name = m.group(1)
                data = dict(data or {})
                data.update(emb)
        if data is None and text.startswith("DE."):
            got = _from_text(text)
            if got and any(got[0].startswith(p) for p in prefixes):
                name, data = got
    if data is None or not isinstance(name, str):
        return None
    data["_ev"] = name
    data["_t"] = rec.get("t", rec.get("utc"))
    return data

def events(records, prefixes=("s2.",)) -> list:
    out = []
    prev = "Space"
    for r in records:
        e = normalise(r, prefixes)
        if e is None:
            continue
        # the SE plugin's "DE.View -> X" lines name only the new state: "from" is the previous transition's "to" in log
 # order (one writer per file), reset to Space by "DE.View Space (…)" (no step: not in world / site not bound)
        if e.get("_ev") == "s2.viewreset":
            prev = "Space"
        elif e.get("_ev") == "s2.state" and e.get("to"):
            if e.get("from") is None:
                e["from"] = prev
            prev = e["to"]
        out.append(e)
    return out

def by(evs, name) -> list:
    return [e for e in evs if e.get("_ev") == name]

def text_lines(records) -> list:
    out = []
    for rec in records:
        for k in ("msg", "text"):
            v = rec.get(k)
            if isinstance(v, str):
                out.append(v)
                break
    return out

def num(d, *names):
    for k in names:
        v = d.get(k) if isinstance(d, dict) else None
        if isinstance(v, bool):
            continue
        if isinstance(v, (int, float)):
            return v
    return None

def flag(d, *names):
    for k in names:
        v = d.get(k) if isinstance(d, dict) else None
        if isinstance(v, bool):
            return v
        if isinstance(v, (int, float)):
            return bool(v)
        if isinstance(v, str) and v.lower() in ("true", "false", "1", "0", "yes", "no"):
            return v.lower() in ("true", "1", "yes")
    return None

def view_name(v):
    if isinstance(v, int) and 0 <= v < len(VIEW_STATES):
        return VIEW_STATES[v]
    return v if isinstance(v, str) else None

def mode_name(v):
    if isinstance(v, int) and 0 <= v < len(APPROACH_MODES):
        return APPROACH_MODES[v]
    return v if isinstance(v, str) else None

def state_label(view, mode=None) -> str:
    """'Space', 'Track', 'Prime', 'Underwater', 'Surfacing' (Approach is named by its mode)."""
    v = view_name(view)
    if v == "Approach":
        m = mode_name(mode)
        return m if m in ("Track", "Prime") else "Approach"
    return v or "?"

def percentile(values, p):
    v = sorted(values)
    if not v:
        return None
    i = min(len(v) - 1, max(0, int(round(p * (len(v) - 1)))))
    return v[i]

def qpc_frequency(*event_lists, default=None) -> int:
    for evs in event_lists:
        for e in evs or ():
            f = num(e, "qpcFrequency")
            if f:
                return int(f)
    if default:
        return int(default)
    if os.name == "nt":
        try:
            import ctypes
            v = ctypes.c_int64()
            ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(v))
            if v.value > 0:
                return int(v.value)
        except (OSError, AttributeError):
            pass
    return 10_000_000

# ------------------------------------------------------------------------------------------------------------- rows
def row(rows, cid, status, measured, threshold, what, part="", required=True, **detail):
    r = {"id": cid, "status": status, "measured": measured, "threshold": threshold, "what": what, "part": part,
         "required": required}
    if detail:
        r["detail"] = detail
    rows.append(r)
    return r

def verdict(rows, strict=True) -> bool:
    bad = BAD if strict else ("FAIL",)
    return not any(r.get("required", True) and r["status"] in bad for r in rows)

def table(rows) -> str:
    out = ["%-12s %-8s %-34s %-26s %s" % ("check", "status", "measured", "threshold", "what"), "-" * 140]
    for r in rows:
        out.append("%-12s %-8s %-34s %-26s %s" % (r["id"], r["status"] + ("" if r.get("required", True) else "*"),
                                                  str(r["measured"])[:34], str(r["threshold"])[:26], r["what"]))
    return "\n".join(out)

class Session:
    """A collected S2 session folder: s2-session.jsonl (launcher), se-*.jsonl, sn-*.jsonl, and tool outputs."""

    def __init__(self, folder: str):
        import glob
        self.dir = folder
        self.session = read_jsonl(os.path.join(folder, "s2-session.jsonl"))
        self.se_files = sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        self.sn_files = sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))
        self.se_raw = read_jsonl(self.se_files)
        self.sn_raw = read_jsonl(self.sn_files)
        self.se = events(self.se_raw)
        self.sn = events(self.sn_raw)
        for i, e in enumerate(self.se):
            e["_i"] = i          # log order (one writer per file): orders lines that carry no QPC
        for i, e in enumerate(self.sn):
            e["_i"] = i
        self.se_s1 = events(self.se_raw, ("s1.",))
        start = next((r for r in self.session if r.get("ev") == "start"), {})
        self.parts = start.get("parts") or []
        self.qpcf = qpc_frequency(self.session, self.se, self.sn, default=start.get("qpcFrequency"))
        self.utc_offset = self._utc_offset()
        for e in self.se + self.sn:
            if e.get("qpc") is None:
                q = self.utc_to_qpc(e.get("_t"))
                if q is not None:
                    e["qpc"] = q
                    e["qpcFromUtc"] = True
        self.vsm = [r for r in self.se_raw if r.get("ev") == "vsm"]
        # SE plugin 1 Hz "sample" records (S0 PerfLog: frameTimeAvgMs, frameTimeMaxMs, fps, gameplayTick, paused,
 # foreground) and SE's own 60 s STATISTICS lines (FrameTime, FrameTimeCPU, FrameTimeGPU) of the game log
        self.se_samples = []
        for r in self.se_raw:
            if r.get("ev") == "sample":
                q = r["qpc"] if isinstance(r.get("qpc"), int) else self.utc_to_qpc(r.get("t"))
                if q is not None:
                    self.se_samples.append(dict(r, qpc=q))
        self.se_stats = self._statistics()
        self._samples_sorted = sorted((r for r in self.session if r.get("ev") == "sample" and r.get("qpc")),
                                      key=lambda r: r["qpc"])

    def _statistics(self) -> list:
        import datetime
        import glob
        out = []
        for path in sorted(glob.glob(os.path.join(self.dir, "SpaceEngineers_*.log"))):
            legend = None
            with open(path, encoding="utf-8", errors="replace") as f:
                for line in f:
                    i = line.find("STATISTICS")
                    if i < 0:
                        continue
                    body = line[i:].strip()
                    if body.startswith("STATISTICS LEGEND,"):
                        legend = [x.strip() for x in body[len("STATISTICS LEGEND,"):].split(",")]
                        continue
                    if not body.startswith("STATISTICS,") or not legend:
                        continue
                    vals = body[len("STATISTICS,"):].split(",")
                    rec = {}
                    for k, v in zip(legend, vals):
                        try:
                            rec[k] = float(v)
                        except ValueError:
                            rec[k] = v
                    try:      # the game log stamps local time: convert to UTC, then to QPC
                        t = datetime.datetime.strptime(line[:23], "%Y-%m-%d %H:%M:%S.%f")
                        utc = datetime.datetime.fromtimestamp(t.timestamp(), datetime.timezone.utc)
                        rec["qpc"] = self.utc_to_qpc(utc.strftime("%Y-%m-%dT%H:%M:%S.%f"))
                    except ValueError:
                        rec["qpc"] = None
                    out.append(rec)
        return out

    def state_at(self, q):
        """The launcher's last section sample at or before QPC q (c5/c7/c0 dicts), or None."""
        lo, hi = 0, len(self._samples_sorted)
        while lo < hi:
            mid = (lo + hi) // 2
            if self._samples_sorted[mid]["qpc"] <= q:
                lo = mid + 1
            else:
                hi = mid
        return self._samples_sorted[lo - 1] if lo > 0 else None

    def state_after(self, q):
        """The launcher's first section sample after QPC q, or None."""
        lo, hi = 0, len(self._samples_sorted)
        while lo < hi:
            mid = (lo + hi) // 2
            if self._samples_sorted[mid]["qpc"] <= q:
                lo = mid + 1
            else:
                hi = mid
        return self._samples_sorted[lo] if lo < len(self._samples_sorted) else None

    def window(self, part):
        q0, q1 = self.part_window(part)
        return (q0, q1 if q1 is not None else 1 << 62) if q0 is not None else (None, None)

    def in_window(self, recs, part):
        q0, q1 = self.window(part)
        if q0 is None:
            return []
        return [r for r in recs if r.get("qpc") is not None and q0 <= r["qpc"] <= q1]

    def sim_ratio(self, part):
        """(mean, min) simulation ratio over a part from the SE samples' gameplayTick (60 ticks per real second)."""
        recs = sorted(self.in_window(self.se_samples, part), key=lambda r: r["qpc"])
        rat = []
        for a, b in zip(recs, recs[1:]):
            dt = (b["qpc"] - a["qpc"]) / float(self.qpcf)
            dk = (b.get("gameplayTick") or 0) - (a.get("gameplayTick") or 0)
            if dt > 0.2 and dk >= 0 and not b.get("paused"):
                rat.append(dk / (60.0 * dt))
        if not rat:
            return None, None
        return sum(rat) / len(rat), min(rat)

    @staticmethod
    def _utc_seconds(u):
        """ISO-8601 UTC -> POSIX seconds (float), or None."""
        if not isinstance(u, str) or len(u) < 19:
            return None
        import datetime
        try:
            base = datetime.datetime.strptime(u[:19], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=datetime.timezone.utc)
        except ValueError:
            return None
        frac = re.match(r"\.(\d+)", u[19:])
        return base.timestamp() + (float("0." + frac.group(1)) if frac else 0.0)

    def _utc_offset(self):
        """QPC = utcSeconds * f + offset, from the launcher events that carry both (median)."""
        d = []
        for r in self.session:
            u = self._utc_seconds(r.get("utc"))
            if u is not None and isinstance(r.get("qpc"), int):
                d.append(r["qpc"] - u * self.qpcf)
        return sorted(d)[len(d) // 2] if d else None

    def utc_to_qpc(self, u):
        s = self._utc_seconds(u)
        if s is None or self.utc_offset is None:
            return None
        return int(s * self.qpcf + self.utc_offset)

    def ev(self, name):
        return [r for r in self.session if r.get("ev") == name]

    def part_window(self, part):
        """(qpcBegin, qpcEnd) of a launcher part, or (None, None)."""
        b = next((r for r in self.session if r.get("ev") == "part" and r.get("name") == part and
                  r.get("phase") == "begin"), None)
        e = next((r for r in self.session if r.get("ev") == "part" and r.get("name") == part and
                  r.get("phase") == "end"), None)
        return (b or {}).get("qpc"), (e or {}).get("qpc")

    def samples(self, part=None):
        return [r for r in self.session if r.get("ev") == "sample" and (part is None or r.get("part") == part)]

def write_results(folder, name, rows, extra=None) -> bool:
    ok = verdict(rows)
    res = {"tool": name, "ok": ok, "rows": rows}
    if extra:
        res.update(extra)
    if folder:
        with open(os.path.join(folder, "%s-results.json" % name), "w", encoding="utf-8") as f:
            json.dump(res, f, indent=1, default=str)
    return ok

# ------------------------------------------------------------------------------------------------- selftest (formats)
def _selftest_formats() -> int:
    """The plugins' real log lines (built like the C# string concatenations of ViewStateDriver.LogStep/NoFrame,
 AudioController.Tick, RenderDisplay, DisplayController, S2Host.SecondLine) parse into the events the analyzers
 read; the "from" chain follows log order and resets on "DE.View Space (". Integration check."""
    def rec(msg, side="se"):
        return {"t": "2026-10-12T09:00:00.000Z", "ev": "log", "msg": msg} if side == "se" else \
               {"t": "2026-10-12T09:00:00.000Z", "text": msg}
    se = [rec(x) for x in (
        "DE.View -> Track (enter Approach) tick=1000 y=250 vy=-5 rho=100 zone=CORE snReady=False blocked=notPrime,y "
        "perf=Track/10 request=0x0 renderSeq=0 ack=0",
        "DE.View -> Prime (enter Prime) tick=2000 y=9.9 vy=-5 rho=100 zone=CORE snReady=False blocked=notPrime,y,snPhase "
        "perf=Full/0 request=0x5 renderSeq=1 ack=0",
        "DE.View -> Underwater (enter Underwater) tick=2125 y=-0.25 vy=-5 rho=100 zone=CORE snReady=True blocked=none "
        "perf=Full/0 request=0x3 renderSeq=2 ack=1 crossing->entry 16.667 ms (presentedSeTick 2124 >= crossingTick 2124)",
        "DE.View -> Surfacing (surface: waterline) tick=2500 y=-0.04 vy=1 rho=100 zone=CORE snReady=True blocked=y "
        "perf=Full/0 request=0x5 renderSeq=3 ack=2",
        "DE.View Space (not in world; the machine restarts with a world-load event)",
        "DE.View -> Track (enter Approach) tick=10 y=100 vy=0 rho=100 zone=CORE snReady=False blocked=notPrime,y "
        "perf=Track/10 request=0x0 renderSeq=0 ack=0",
        "DE.View Prime below the waterline (y -0.3), Underwater held by: zOrder",
        "DE.Audio gate=open write=True bridgeUp=True mix=1 seMixActive=True game=0.56 music=0 hud=0.8 waterMod=0 "
        "(config game 0.8 music 0.6)",
        "DE.Audio gate=closed write=True bridgeUp=True mix=0 seMixActive=False game=0.8 music=0.6 hud=0.8 waterMod=1 "
        "(config game 0.8 music 0.6) MUTANT S2.M11: written without the gate",
        "DE.Display margins ON (-1) hr=0x00000000 S_OK label=Prime presents=12",
        "DE.Display margins OFF (0) hr=0x00000000 (MUTANT S2.M1: DwmExtendFrameIntoClientArea NOT called) label=x presents=3",
        "DE.Display toggle #3 see-through ON via F8 mode=F foregroundIsSe=True updateMs=0.042",
        "DE.Display see-through first skipped frame presented +7.1 ms after the request (toggle #3, margins on for 2 presents)",
        "DE.Display alpha mode=F label=toggle #3 margins=on scene=skipped alphaFill=no 2560x1440 format=29 pixels=3686400 "
        "alphaGt0=1200 (0.033 %) alphaLt1=3686000 (99.989 %) alpha0=3685000 [T-D1: …]",
        "DE.ZOrder OK after attempt 1 (Prime): OK first=0x1234",
        # re-dive fix: the band-hold diagnostic (ViewStateDriver.BandHold)
        "DE.View band hold #1 start: Prime with y in (-0.2, -0.05] m for 1.017 s (Underwater needs y <= -0.2 m, Surfacing "
        "happens at y > -0.05 m) tick=3000 y=-0.125 vy=0 snReady=True blocked=y heldS=1.017",
        "DE.View band hold #1 continues: Prime with y in (-0.2, -0.05] m for 6.017 s (Underwater needs y <= -0.2 m, "
        "Surfacing happens at y > -0.05 m) tick=3300 y=-0.11 vy=0.2 snReady=True blocked=y heldS=6.017",
        "DE.View band hold #1 ended after 6.333 s: y at or below -0.2 m (still Prime: blocked=crossing) tick=3320 y=-0.5 vy=0",
        "DE.View band hold #2 ended (not in world)",
    )]
    sn = [rec(x, "sn") for x in (
        "DE.S2 sec phase=Active c5=Approach/Prime/Full/0 se=Alive recipe=Full(1, -1) applied=(1, -1) fps=164.8 "
        "maxGapMs=9 gaps>100ms=0 gaps>1s=0 cameras=on priority=Normal frozen=false causes=none timeScale=1 "
        "freezeTop=none freezeCalls=1/1/0 audio{mute=true} win{rect=ok} rectOk=true presentedSeTick=2000 framesPresented=55",
        "DE.S2 sec phase=ReadyIdle c5=Space/Idle/10 se=Alive recipe=Idle(0, 10) applied=(0, 10) fps=10 maxGapMs=101 "
        "gaps>100ms=1 gaps>1s=0 cameras=off priority=BelowNormal frozen=true causes=Idle timeScale=0",
        "DE.Audio fade 0 -> 1 over 300 ms (C5 snBusTarget 1)",
        "DE.Audio MUTED (no live SE) bus:/ volume 0 target 0",
        "DE.Freeze Begin (Idle) -> frozen=True causes=Idle timeScale=0 top=FeedbackPanel",
        "DE.Calib ON colour=#ff00ff (255,0,255) overlay canvas order 32767",
    )]
    ok = True

    def check(name, cond, info=""):
        nonlocal ok
        ok = ok and bool(cond)
        print("  %-62s %s %s" % (name, "ok" if cond else "FAIL", info))

    e = events(se)
    st = by(e, "s2.state")
    check("4 + 1 DE.View transitions parsed", len(st) == 5, [(s.get("from"), s.get("to")) for s in st])
    check("from chain in log order", [(s["from"], s["to"]) for s in st] == [
        ("Space", "Track"), ("Track", "Prime"), ("Prime", "Underwater"), ("Underwater", "Surfacing"), ("Space", "Track")])
    uw = st[2]
    check("Underwater entry: tick, snReady, crossing latency, ticks", uw.get("tick") == 2125 and uw.get("snReady") is True
          and uw.get("crossingToEntryMs") == 16.667 and uw.get("crossingTick") == 2124 and uw.get("presentedSeTick") == 2124, uw)
    check("reason kept", st[3].get("reason") == "surface: waterline")
    au = by(e, "s2.audio")
    check("DE.Audio gate= lines (plugin format)", len(au) == 2 and au[0]["gate"] == "open" and au[0]["seMixActive"] is True and
          au[0]["music"] == 0 and au[1]["mutantM11"] is True, au)
    dp = by(e, "s2.display")
    acts = [d.get("action") for d in dp]
    check("margins / toggle / skip latency", acts == ["marginsOn", "marginsOff", "toggle", "skipPresented"], acts)
    check("margins S_OK -> hr 0; mutant M1 flagged", dp[0]["hr"] == 0 and dp[1]["mutantNoCall"] is True)
    check("toggle updateMs", dp[2]["updateMs"] == 0.042 and dp[2]["isSe"] is True)
    al = by(e, "s2.alpha")
    check("alpha readback", len(al) == 1 and al[0]["scene"] == "skipped" and abs(al[0]["fracAlphaGt0"] - 0.00033) < 1e-9, al)
    check("z-order", [z["result"] for z in by(e, "s2.zorder")] == ["OK"])
    bh = by(e, "s2.bandhold")
    check("band-hold lines (re-dive fix 2026-10-16), not DE.View transitions", [
        (b["index"], b["phase"], b.get("tick")) for b in bh] == [(1, "start", 3000), (1, "continues", 3300),
                                                                 (1, "ended", 3320), (2, "ended", None)] and
          bh[0]["y"] == -0.125 and bh[0]["blocked"] == "y" and bh[0]["snReady"] is True and bh[1]["heldS"] == 6.017 and
          bh[2]["heldS"] == 6.333 and bh[2]["how"].startswith("y at or below") and bh[3]["how"] == "not in world", bh)
    # review fix (finding 1): probe verdicts, the design switch, the autoFallback switch
    e2 = events([rec(x) for x in (
        "DE.Display probe START (composed-pixel measurement: margins on, one see-through moment, two GDI GetPixel reads)",
        "DE.Display probe HONOURED expected 255/0/255 +-24: 5/5 match; points: (1,1)=255/0/255 | read 2: HONOURED ...",
        "DE.Display probe NOT HONOURED expected 255/0/255 +-24: 0/5 match; points: ... | read 2: NOT HONOURED ...",
        "DE.Display probe UNDECIDED (the two reads disagree or are mixed) read 1: NOT HONOURED | read 2: HONOURED",
        "DE.Display probe probe TIMEOUT (GDI read did not finish)",
        "DE.Display autoFallback: NOT HONOURED probe #1 in a row, design F kept (autoFallback off)",
        "DE.Display mode F -> A' (composed-pixel probe: DWM alpha not honoured in 2 probes in a row)",
        "DE.Display autoFallback off via C11 (A' after 2 NOT HONOURED probes in a row when on)",
    )])
    d2 = by(e2, "s2.display")
    check("probe verdicts, design switch, autoFallback switch", [(d.get("action"), d.get("verdict") or d.get("to") or d.get("on"))
          for d in d2] == [("probeStart", None), ("probe", "HONOURED"), ("probe", "NOT HONOURED"), ("probe", "UNDECIDED"),
                           ("probe", "UNDECIDED"), ("mode", "A'"), ("autoFallback", False)], d2)
    s = events(sn)
    pf = by(s, "s2.perf")
    check("DE.S2 sec Approach/Prime/Full/0", pf[0].get("view") == "Prime" and pf[0].get("perfMode") == "Full" and
          pf[0].get("targetFps") == 0 and pf[0].get("vSyncCount") == 1 and pf[0].get("targetFrameRate") == -1, pf[0])
    check("DE.S2 sec Space/Idle/10", pf[1].get("view") == "Space" and pf[1].get("targetFps") == 10 and pf[1]["frozen"] is True
          and pf[1]["camerasOn"] is False, pf[1])
    mx = by(s, "s2.mix")
    check("SN fade + mute", len(mx) == 2 and mx[0]["target"] == 1 and mx[1]["muted"] is True)
    check("SN freeze + calib", by(s, "s2.freeze")[0]["frozen"] is True and by(s, "s2.calib")[0]["colour"] == "#ff00ff")
    print("S2_COMMON FORMATS SELFTEST " + ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    if "--selftest" in sys.argv[1:]:
        sys.exit(_selftest_formats())
    print("usage: python -I tests/s2/s2_common.py --selftest")
    sys.exit(2)
