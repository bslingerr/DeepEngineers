"""S8 minimum, launcher side.

 python -I src/DeepEngineers.Launcher/launcher.py restart-renderer [--cooldown 20] [--wait 180] [--se-pid N]
 [--old-sn-pid N] [--no-foreground-wait] [--no-wait-exits] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py restore-settings [--yes] [--only water,secfg,registry]
 python -I src/DeepEngineers.Launcher/launcher.py kill --pid N # asks for the typed PID; game processes only

`run` calls this module at two points (hooks in launcher.py marked "S8-settings" and "S8-exit"):

- before the games start (`before_session`): the settings backup is created at the first run (Water Mod
 `WaterClientSettings.xml` `<Volume>`, `SpaceEngineers.cfg` `GameVolume` / `Music_Volume` / `VoiceChatVolume`; the
 Subnautica `Screenmanager` registry keys keep their own backup, launcher.py `registry_backup_check`); after a session
 that did not end cleanly, a value the bridge can leave behind (the fallback path's Water Mod volume 0; SE's game
 volume at 0.7 x or music at 0 of the pre-session values) is restored from the pre-session snapshot ("the next clean
 start"); every other difference is only reported (`restore-settings --yes` restores everything from the backup);
 then the pre-session snapshot is written (`session-settings.json`).
- the exit path (`wait_exits`): both process handles are polled every 0.25 s and the section is sampled read-only once
 per second (opened and closed each time, so the launcher never keeps it alive): SE's `seExiting` (a normal exit: the
 ExitThreadSafe prefix ran before Pulsar's kill), both heartbeats and phases. SE gone and Subnautica still running
 15 s later: one CloseMainWindow; 30 s later: the launcher OFFERS a kill by exact PID (it never kills by itself; in an
 interactive console the player presses K and types the PID, otherwise `launcher.py kill --pid N`). A game whose
 heartbeat is 30 s old (150 s in a loading phase) while its process lives gets the same offer. Subnautica exiting
 while SE runs is never followed by a relaunch: the launcher prints how to restart the renderer. At the end the session
 is recorded clean (SE wrote `seExiting`, or never linked) or not, and the settings are compared with the snapshot.

`restart-renderer` is the player's renderer restart (no automatic relaunch in v1): SE must be running; the old
Subnautica PID and every UnityCrashHandler64 must be gone; then a 20 s cool-down from the old process's exit (the run
loop's record, else its last heartbeat in the section, else from the moment it was seen gone); the relaunch preflight
accepts a present section whose recorded snPid is dead and refuses one held by another Subnautica; Subnautica is
started like `run` starts it, the ready wait checks that
snPid is the started process, and SE is never touched. Before the start it waits (up to 30 s, `--no-foreground-wait`
skips) until SE's window is in the foreground, so the new window is created by a background launcher (HYPOTHESIS:
Windows' foreground lock then keeps SE in front; S8.T-L1d measures it). It then waits on both like `run`.

The decision parts are pure (`ExitWatch`, `relaunch_check`, `restore_plan`, the cfg / XML readers and writers) and
tested with fixtures and the Python stand-ins in tests/s8/test_s8_launcher.py (python -I); no game is needed.
"""
from __future__ import annotations

import datetime
import json
import os
import re
import shutil
import subprocess
import sys
import time

# ----------------------------------------------------------------------------------------------------------------
# Constants (CONTRACT §7, ROADMAP S8 Launcher row). The test knobs (environment) only shorten them for stand-in tests.
# ----------------------------------------------------------------------------------------------------------------

HANG_OFFER_S = float(os.environ.get("DEEPENG_S8_HANG_S") or 30.0)      # "offers... a kill by exact PID after 30 s of hang"
ASK_QUIT_AFTER_SE_S = float(os.environ.get("DEEPENG_S8_ASK_S") or 15.0)  # CONTRACT §7: quitting within 10 s, + 5 s margin
RELAUNCH_COOLDOWN_S = 20.0                                                # CONTRACT §7 "Relaunch"
LOADING_HANG_S = 150.0                                                    # a loading phase stalls only after 120 s (+30 s)
POLL_S = 0.25
SAMPLE_S = POLL_S          # the section is sampled at every poll (open, read a few header words, close: well under 1 ms)
FOREGROUND_WAIT_S = 30.0

COMMANDS = ("restart-renderer", "restore-settings", "kill")

SE_CFG_KEYS = ("GameVolume", "Music_Volume", "VoiceChatVolume")
# Wire AudioMix (CONTRACT §5.4 audio table): the plugin's underwater factors; a persisted value equal to the
# pre-session value times one of them is "bridge-made" (only possible through an unclean end)
SE_GAME_UNDERWATER = 0.7
SE_MUSIC_UNDERWATER = 0.0
WATER_UNDERWATER = 0.0
EPS = 1e-3

_CFG_RE = r"(<Key>%s</Key>\s*<Value>\s*<Value[^>]*>)([^<]*)(</Value>)"
_WATER_RE = r"(<Volume>)([^<]*)(</Volume>)"

# ----------------------------------------------------------------------------------------------------------------
# Settings files (pure: paths in, values out; writes keep every other byte)
# ----------------------------------------------------------------------------------------------------------------

def _decode(raw: bytes):
    """(text, encoding, bom): UTF-8 with or without BOM, or UTF-16 LE/BE with BOM (the Water Mod file declares
 utf-16 but is written as UTF-8 by SE's storage API; the declaration is never trusted)."""
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw[3:].decode("utf-8"), "utf-8", b"\xef\xbb\xbf"
    if raw.startswith(b"\xff\xfe"):
        return raw[2:].decode("utf-16-le"), "utf-16-le", b"\xff\xfe"
    if raw.startswith(b"\xfe\xff"):
        return raw[2:].decode("utf-16-be"), "utf-16-be", b"\xfe\xff"
    return raw.decode("utf-8"), "utf-8", b""

def read_cfg_audio(path: str):
    """{key: text} of SpaceEngineers.cfg's audio keys (None when the file is unreadable)."""
    try:
        with open(path, "rb") as f:
            text, _enc, _bom = _decode(f.read())
    except (OSError, UnicodeDecodeError):
        return None
    out = {}
    for k in SE_CFG_KEYS:
        m = re.search(_CFG_RE % re.escape(k), text)
        if m:
            out[k] = m.group(2).strip()
    return out

def read_water_volume(path: str):
    """WaterClientSettings.xml `<Volume>` as text (None when absent or unreadable)."""
    try:
        with open(path, "rb") as f:
            text, _enc, _bom = _decode(f.read())
    except (OSError, UnicodeDecodeError):
        return None
    m = re.search(_WATER_RE, text)
    return m.group(2).strip() if m else None

def _rewrite(path: str, replacements, backup_dir: str = None) -> list:
    """replacements: [(regex with 3 groups, new text)]. Writes only when something changes; the original is copied
 to backup_dir first. Returns the list of (old, new) changed."""
    with open(path, "rb") as f:
        raw = f.read()
    text, enc, bom = _decode(raw)
    changed = []
    for rx, new in replacements:
        m = re.search(rx, text)
        if not m or m.group(2).strip() == new:
            continue
        changed.append((m.group(2), new))
        text = text[:m.start(2)] + new + text[m.end(2):]
    if not changed:
        return []
    if backup_dir:
        os.makedirs(backup_dir, exist_ok=True)
        stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
        shutil.copy2(path, os.path.join(backup_dir, "%s-%s" % (stamp, os.path.basename(path))))
    tmp = path + ".de-tmp"
    with open(tmp, "wb") as f:
        f.write(bom + text.encode(enc))
    os.replace(tmp, path)
    return changed

def write_cfg_audio(path: str, values: dict, backup_dir: str = None) -> list:
    return _rewrite(path, [(_CFG_RE % re.escape(k), v) for k, v in values.items() if k in SE_CFG_KEYS], backup_dir)

def write_water_volume(path: str, value: str, backup_dir: str = None) -> list:
    return _rewrite(path, [(_WATER_RE, value)], backup_dir)

def flat_values(cfg: dict, water) -> dict:
    """{"secfg.GameVolume": "1",..., "water.Volume": "1"} (absent keys left out)."""
    out = {}
    for k, v in (cfg or {}).items():
        out["secfg." + k] = v
    if water is not None:
        out["water.Volume"] = water
    return out

def _num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None

def restore_plan(backup: dict, current: dict, mode: str, presession: dict = None, last_end: dict = None) -> list:
    """[(key, current, target, why)] for the flat values.

 mode "verb" (restore-settings): every key whose current value differs from the backup goes back to the backup.
 mode "clean-start" (run, before the games start): nothing after a clean end; after an unclean or unknown end only
 a bridge-made value goes back to the pre-session value (else the backup): the Water Mod Volume at 0 (the fallback
 path keeps it in memory; the file can only hold it after an unclean end), SE's GameVolume at 0.7 x and Music_Volume
 at 0 x the reference. VoiceChatVolume is never touched by the bridge and never restored at start."""
    plan = []
    if mode == "verb":
        for k in sorted(backup or {}):
            if k in (current or {}) and _num(current[k]) != _num(backup[k]) and current[k] != backup[k]:
                plan.append((k, current[k], backup[k], "differs from the launcher's backup"))
        return plan
    if last_end is not None and last_end.get("clean"):
        return plan
    ref = presession or backup or {}
    for k, factor, why in (("water.Volume", WATER_UNDERWATER, "the Water Mod volume of the fallback audio path (0) persisted by an unclean end"),
                           ("secfg.GameVolume", SE_GAME_UNDERWATER, "SE's underwater game volume (0.7 x) persisted by an unclean end"),
                           ("secfg.Music_Volume", SE_MUSIC_UNDERWATER, "SE's underwater music volume (0) persisted by an unclean end")):
        cur, r = _num((current or {}).get(k)), _num(ref.get(k))
        if cur is None or r is None or abs(cur - r) <= EPS:
            continue
        if abs(cur - r * factor) <= EPS:
            plan.append((k, current[k], ref[k], why))
    return plan

def apply_plan(plan: list, cfg_path: str, water_path: str, backup_dir: str) -> list:
    """Writes the plan; returns [(key, old, new)] actually changed."""
    done = []
    cfg = {k.split(".", 1)[1]: t for k, _c, t, _w in plan if k.startswith("secfg.")}
    if cfg:
        for old, new in write_cfg_audio(cfg_path, cfg, backup_dir):
            done.append(("secfg", old, new))
    for k, _c, t, _w in plan:
        if k == "water.Volume":
            for old, new in write_water_volume(water_path, t, backup_dir):
                done.append(("water.Volume", old, new))
    return done

# ----------------------------------------------------------------------------------------------------------------
# The exit path (pure state machine + the loop)
# ----------------------------------------------------------------------------------------------------------------

class ExitWatch:
    """One step per poll: events from the two processes and the section facts.

 Events: se_exit, sn_exit, ask_sn_quit (SE gone, Subnautica still there after ASK_QUIT_AFTER_SE_S: one
 CloseMainWindow), offer_kill_sn / offer_kill_se (a hang: SE gone and Subnautica still there after HANG_OFFER_S, or
 a heartbeat older than HANG_OFFER_S, LOADING_HANG_S in a loading phase, with the process alive; once per hang),
 responding_sn / responding_se (the heartbeat fresh again: the offer is withdrawn), done (both gone). Never a kill
 and never a relaunch."""

    def __init__(self, hang_s: float = HANG_OFFER_S, ask_s: float = ASK_QUIT_AFTER_SE_S, loading_hang_s: float = LOADING_HANG_S):
        self.hang_s, self.ask_s, self.loading_hang_s = hang_s, ask_s, loading_hang_s
        self.se_exit_t = None
        self.sn_exit_t = None
        self.asked = False
        self.offered = {}
        self.offers = 0
        self.se_exiting_seen = False
        self.se_linked_seen = False
        self.sn_quitting_seen = False

    def step(self, now: float, se_alive: bool, sn_alive: bool, sn_hb_age=None, se_hb_age=None, sn_loading=False,
             se_loading=False, se_exiting=False, sn_quitting=False, se_linked=False) -> list:
        ev = []
        self.se_exiting_seen |= bool(se_exiting)
        self.sn_quitting_seen |= bool(sn_quitting)
        self.se_linked_seen |= bool(se_linked)
        if self.se_exit_t is None and not se_alive:
            self.se_exit_t = now
            ev.append("se_exit")
        if self.sn_exit_t is None and not sn_alive:
            self.sn_exit_t = now
            ev.append("sn_exit")
        if not se_alive and not sn_alive:
            ev.append("done")
            return ev
        if self.se_exit_t is not None and sn_alive:
            waited = now - self.se_exit_t
            if not self.asked and waited >= self.ask_s:
                self.asked = True
                ev.append("ask_sn_quit")
            if "sn" not in self.offered and waited >= self.hang_s:
                self.offered["sn"] = now
                self.offers += 1
                ev.append("offer_kill_sn")
            return ev
        for side, alive, age, loading in (("sn", sn_alive, sn_hb_age, sn_loading), ("se", se_alive, se_hb_age, se_loading)):
            if not alive or age is None:
                continue
            limit = self.loading_hang_s if loading else self.hang_s
            if age >= limit and side not in self.offered:
                self.offered[side] = now
                self.offers += 1
                ev.append("offer_kill_" + side)
            elif age < limit and side in self.offered:
                del self.offered[side]
                ev.append("responding_" + side)
        return ev

    def clean(self) -> bool:
        """A clean end for the settings: SE wrote seExiting before its process ended (its ExitThreadSafe prefix ran), or
 SE never linked to the section (nothing of the bridge ran in it)."""
        return self.se_exiting_seen or not self.se_linked_seen

def sample_section(S, L, name: str, sn_pid: int, se_pid: int) -> dict:
    """Read-only facts for ExitWatch.step (opened and closed here: the launcher never keeps the section alive)."""
    try:
        sec = S.Section.open(name, readonly=True)
    except OSError:
        sec = None
    if sec is None:
        return {}
    try:
        if sec.mapped_size < L.HEADER_SIZE or sec.hdr("magic") != L.MAGIC:
            return {}
        now = S.tick64()
        snp, _sng = sec.identity("sn")
        sep, _seg = sec.identity("se")
        sn_phase, se_phase = sec.hdr("snPhase"), sec.hdr("sePhase")
        f = {}
        if snp == sn_pid:
            hb = sec.hdr("snHeartbeatMs")
            f["sn_hb_age"] = max(0, now - hb) / 1000.0
            f["sn_loading"] = sn_phase in L.SN_LOADING_PHASES
            f["sn_quitting"] = sn_phase == L.SN_PHASE["Quitting"]
        if sep != 0:
            f["se_linked"] = sep == se_pid
            f["se_exiting"] = se_phase == L.SE_PHASE["Exiting"]
            if sep == se_pid:
                hb = sec.hdr("seHeartbeatMs")
                f["se_hb_age"] = max(0, now - hb) / 1000.0
                f["se_loading"] = se_phase in L.SE_LOADING_PHASES
        return f
    finally:
        sec.close()

class PidProcess:
    """Popen-like view (pid, poll, returncode) of a process the launcher did not start (restart-renderer's SE)."""

    def __init__(self, S, pid: int):
        self.pid = pid
        self.returncode = None
        self._w = S.ProcessWatch(pid)

    def poll(self):
        if self.returncode is None and self._w.exited():
            self.returncode = self._w.exit_code()
            if self.returncode is None:
                self.returncode = -1
            self._w.close()
        return self.returncode

def _kbhit_k() -> bool:
    """True when the player pressed K in an interactive console (never blocks; False without a console)."""
    try:
        if not sys.stdin or not sys.stdin.isatty():
            return False
        import msvcrt
        hit = False
        while msvcrt.kbhit():
            if msvcrt.getwch().lower() == "k":
                hit = True
        return hit
    except Exception:
        return False

# Subnautica's switches only: restart-renderer restarts Subnautica and leaves SE (and SE's DEEPENGINEERS_S8A_TREADMILL) running;
# the restarted renderer reads the chart from the S8a header (S8aTerrain: hdr.chartKey), so the treadmill needs no record here
PLAY_ENV_VARS = ("DEEPENGINEERS_S6", "DEEPENGINEERS_S11", "DEEPENGINEERS_S8A")   #: S8A recorded too, so a renderer restart keeps the planet on

def play_env_path(LCH) -> str:
    return os.path.join(LCH.LOCAL, "play-env.json")

def record_play_env(LCH, values: dict):
    """S11  `run` records the S6/S11 switches it started Subnautica with, for restart-renderer."""
    try:
        doc = {k: (v if v in ("on", "off") else ("off" if v is None else str(v))) for k, v in values.items()}
        doc["epoch"] = time.time()
        _save(play_env_path(LCH), doc)
    except OSError:
        pass

def play_env(LCH) -> dict:
    """The switches of the session being restarted: its record, else "off" (what `run` used before S11)."""
    rec = _load(play_env_path(LCH)) or {}
    return {k: (rec.get(k) if rec.get(k) in ("on", "off") else "off") for k in PLAY_ENV_VARS}

def renderer_exit_path(LCH) -> str:
    return os.path.join(LCH.LOCAL, "renderer-exit.json")

def wait_exits(LCH, log, a, sn, se, settings: bool = True) -> int:
    """The `run` exit path (replaces launcher.wait_exits through the S8-exit hook)."""
    S, L = LCH.S, LCH.L
    w = ExitWatch()
    t0 = time.monotonic()
    next_sample = 0.0
    facts = {}
    offered_pid = {}
    # SE's process handle: the loop sleeps on it, so SE's exit is seen at once and the section is sampled right after it
 # (Subnautica still holds it then: sePhase Exiting tells a normal exit from a crash)
    se_watch = S.ProcessWatch(se.pid)
    se_exit_sampled = False
    log.event("s8_wait", sn=sn.pid, se=se.pid, hangOfferS=w.hang_s, askS=w.ask_s)
    try:
        while True:
            now = time.monotonic()
            se_alive, sn_alive = se.poll() is None, sn.poll() is None
            if (se_alive or sn_alive) and (now >= next_sample or (not se_alive and not se_exit_sampled)):
                next_sample = now + SAMPLE_S
                se_exit_sampled = se_exit_sampled or not se_alive
                f = sample_section(S, L, a.name, sn.pid, se.pid)
                facts = f if f or not (facts.get("se_exiting") or facts.get("se_linked")) else \
                    {k: v for k, v in facts.items() if k in ("se_exiting", "se_linked")}
            elif not (se_alive or sn_alive):
                facts = {}
            evs = w.step(now, se_alive, sn_alive, **facts)
            for e in evs:
                if e == "se_exit":
                    log.say("SE exited (code %s) after %.0f s: %s" % (
                        se.returncode, now - t0, "normal exit (seExiting written before the process ended)" if w.se_exiting_seen else
                        ("no seExiting seen: a crash or a kill" if w.se_linked_seen else "SE was not linked to the renderer")),
                        ev="se_exit", code=se.returncode, seExiting=w.se_exiting_seen, linked=w.se_linked_seen)
                elif e == "sn_exit":
                    after = (", %.1f s after SE" % (now - w.se_exit_t)) if w.se_exit_t is not None else ""
                    log.say("Subnautica exited (code %s) after %.0f s%s" % (sn.returncode, now - t0, after), ev="sn_exit",
                            code=sn.returncode, afterSeS=round(now - w.se_exit_t, 3) if w.se_exit_t is not None else None)
                    if se_alive:
                        _record_renderer_exit(LCH, sn.pid)
                        log.say("Space Engineers keeps running with its own water view (the HUD says 'underwater renderer "
                                "offline'). No automatic relaunch: to restart the renderer, run `launcher.py restart-renderer` "
                                "(it waits for the old process, its crash handler and a %.0f s cool-down)." % RELAUNCH_COOLDOWN_S,
                                ev="renderer_offline")
                elif e == "ask_sn_quit":
                    posted = S.close_main_window(sn.pid)
                    log.say("Subnautica still running %.0f s after SE: CloseMainWindow(%d) %s" % (
                        now - w.se_exit_t, sn.pid, "posted" if posted else "(no main window)"), ev="ask_sn_quit", posted=posted)
                elif e.startswith("offer_kill_"):
                    side = e[-2:]
                    pid = sn.pid if side == "sn" else se.pid
                    what = "Subnautica" if side == "sn" else "Space Engineers"
                    why = ("has not quit %.0f s after SE" % (now - w.se_exit_t)) if (side == "sn" and w.se_exit_t is not None) else \
                          ("has not responded for %.0f s (heartbeat)" % facts.get(side + "_hb_age", 0.0))
                    offered_pid[side] = pid
                    log.say("%s (pid %d) %s. The launcher never ends a game by itself. To end it by its exact PID: press K "
                            "here and type the PID, or run `launcher.py kill --pid %d`; otherwise keep waiting." % (what, pid, why, pid),
                            ev="offer_kill", side=side, pid=pid)
                elif e.startswith("responding_"):
                    side = e[-2:]
                    offered_pid.pop(side, None)
                    log.say("%s is responding again (offer withdrawn)" % ("Subnautica" if side == "sn" else "Space Engineers"),
                            ev="responding", side=side)
            if "done" in evs:
                break
            if offered_pid and _kbhit_k():
                for side, pid in list(offered_pid.items()):
                    if S.process_state(pid) == "alive":
                        LCH.confirm_kill(log, pid, "Subnautica" if side == "sn" else "Space Engineers", True)
                offered_pid.clear()
            if se_alive:
                se_watch.wait(POLL_S)
            else:
                time.sleep(POLL_S)
    except KeyboardInterrupt:
        log.say("stopped waiting; processes left running (launcher.py stop closes them)")
        return 130
    finally:
        se_watch.close()
    clean = w.clean()
    log.say("session end: %s (SE seExiting %s, linked %s; Subnautica %s; %d kill offer(s), none performed by the launcher)" % (
        "clean" if clean else "NOT clean (settings restore at the next start)", w.se_exiting_seen, w.se_linked_seen,
        "quit by itself" if not w.asked else "asked to close", w.offers), ev="session_end", clean=clean, seExiting=w.se_exiting_seen,
        linked=w.se_linked_seen, offers=w.offers, asked=w.asked)
    if settings and not _standin(a):
        try:
            after_session(LCH, log, clean, se_code=se.returncode, sn_code=sn.returncode)
        except Exception as e:  # never fail the exit path on a settings read
            log.say("settings comparison skipped: %s" % e, ev="settings_error")
    return 0

def _record_renderer_exit(LCH, pid: int):
    try:
        os.makedirs(LCH.LOCAL, exist_ok=True)
        with open(renderer_exit_path(LCH), "w", encoding="utf-8") as f:
            json.dump({"pid": pid, "exitEpoch": time.time()}, f)
    except OSError:
        pass

def _standin(a) -> bool:
    return bool(getattr(a, "sn_cmd", None) or getattr(a, "se_cmd", None))

# ----------------------------------------------------------------------------------------------------------------
# Settings around a session (before_session, after_session) and the restore-settings verb
# ----------------------------------------------------------------------------------------------------------------

def settings_paths(LCH, a=None) -> dict:
    g = (lambda k: getattr(a, k, None)) if a is not None else (lambda k: None)
    backup = g("backup") or os.path.join(LCH.LOCAL, "settings-backup.json")
    return {
        "cfg": g("cfg") or os.path.join(LCH.SE_APPDATA, "SpaceEngineers.cfg"),
        "water": g("water") or os.path.join(LCH.SE_APPDATA, "Storage", "WaterClientSettings.xml"),
        "backup": backup,
        "session": g("session") or os.path.join(LCH.LOCAL, "session-settings.json"),
        # the copies of the originals before a restore go next to the backup (tests: their own folder)
        "restoreDir": os.path.join(os.path.dirname(backup), "settings-restore"),
    }

def read_values(p: dict) -> dict:
    return flat_values(read_cfg_audio(p["cfg"]), read_water_volume(p["water"]))

def _load(path: str):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None

def _save(path: str, doc: dict):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1, sort_keys=True)
    os.replace(tmp, path)

def ensure_backup(log, p: dict, current: dict):
    """The settings backup is written once (first run), like the registry backup; never overwritten by the launcher."""
    doc = _load(p["backup"])
    if doc is not None:
        return doc
    doc = {"created": datetime.datetime.now().isoformat(timespec="seconds"), "values": current,
           "files": {"cfg": p["cfg"], "water": p["water"]},
           "note": "SpaceEngineers.cfg audio keys and the Water Mod volume backed up by launcher.py at its first S8 run (CONTRACT 1.1)"}
    _save(p["backup"], doc)
    log.say("settings backup created: %s -> %s" % (", ".join("%s=%s" % kv for kv in sorted(current.items())), p["backup"]),
            ev="settings_backup", values=current)
    return doc

def before_session(LCH, log, a) -> None:
    """S8-settings: backup at the first run, the restore at the next clean start, the pre-session snapshot."""
    if _standin(a) or getattr(a, "dry_run", False):
        return
    try:
        p = settings_paths(LCH)
        current = read_values(p)
        if not current:
            log.say("settings: SpaceEngineers.cfg / WaterClientSettings.xml not readable (%s, %s): nothing backed up" % (p["cfg"], p["water"]),
                    ev="settings_check", status="WARN")
            return
        backup = ensure_backup(log, p, current)
        last = _load(p["session"])
        last_end = last.get("ended") if last else None
        presession = dict(last.get("values") or {}) if last else None
        # the SE plugin's restore.json (CONTRACT 1.1): a Water Mod volume restore still pending means the session that
 # changed it ended uncleanly, whatever the launcher saw; its "original" is the project's value
        pend = (_load(os.path.join(LCH.LOCAL, "restore.json")) or {}).get("waterModVolume") or {}
        if pend.get("pending") is True and pend.get("original") is not None:
            last = last or {}
            presession = dict(presession or {})
            presession["water.Volume"] = "%g" % float(pend["original"])
            last_end = {"clean": False, "why": "restore.json pending"}
        if last is not None:
            plan = restore_plan(backup.get("values", {}), current, "clean-start", presession, last_end)
            if plan:
                running = LCH.processes_named(LCH.GAME_PROCESSES)
                if running:
                    log.say("settings: %d value(s) to restore but a game is running; not written (%s)" % (
                        len(plan), "; ".join("%s %s -> %s" % (k, c, t) for k, c, t, _w in plan)), ev="settings_restore", written=False)
                else:
                    done = apply_plan(plan, p["cfg"], p["water"], p["restoreDir"])
                    for k, c, t, why in plan:
                        log.say("settings restored at the next clean start: %s %s -> %s (%s)" % (k, c, t, why), ev="settings_restore",
                                key=k, old=c, new=t, written=bool(done))
                    current = read_values(p)
        diff = restore_plan(backup.get("values", {}), current, "verb")
        if diff:
            log.say("settings: %s differ from the launcher's backup (kept: they may be your own changes; "
                    "`launcher.py restore-settings --yes` puts the backup back)" % ", ".join("%s %s (backup %s)" % (k, c, t) for k, c, t, _w in diff),
                    ev="settings_check", status="INFO")
        _save(p["session"], {"started": datetime.datetime.now().isoformat(timespec="seconds"), "values": current, "ended": None})
    except Exception as e:  # the settings part never blocks a start
        log.say("settings check skipped: %s: %s" % (type(e).__name__, e), ev="settings_error")

def after_session(LCH, log, clean: bool, se_code=None, sn_code=None) -> None:
    p = settings_paths(LCH)
    doc = _load(p["session"]) or {}
    current = read_values(p)
    changed = {k: (doc.get("values", {}).get(k), v) for k, v in current.items() if doc.get("values", {}).get(k) != v}
    doc["ended"] = {"clean": bool(clean), "at": datetime.datetime.now().isoformat(timespec="seconds"), "seCode": se_code,
                    "snCode": sn_code, "changed": changed}
    _save(p["session"], doc)
    if changed:
        log.say("settings changed during the session: %s%s" % (
            ", ".join("%s %s -> %s" % (k, o, n) for k, (o, n) in sorted(changed.items())),
            "" if clean else " (unclean end: bridge-made values are restored at the next start)"), ev="settings_after_session",
            changed=changed, clean=clean)
    else:
        log.say("settings unchanged by the session (SpaceEngineers.cfg audio keys, Water Mod volume)", ev="settings_after_session",
                changed={}, clean=clean)

def restore_settings(LCH, log, a) -> int:
    p = settings_paths(LCH, a)
    only = set((a.only or "water,secfg,registry").split(","))
    backup = _load(p["backup"])
    rc = 0
    if backup is None and ({"water", "secfg"} & only):
        log.say("no settings backup at %s (it is created by the first `run`)" % p["backup"])
        rc = 1
    elif {"water", "secfg"} & only:
        current = read_values(p)
        plan = [x for x in restore_plan(backup.get("values", {}), current, "verb") if x[0].split(".", 1)[0] in only]
        if not plan:
            log.say("settings: nothing to restore (%s equal the backup)" % ", ".join(sorted(current)) if current else
                    "settings: the files are not readable")
        else:
            log.say("settings to restore: %s" % "; ".join("%s %s -> %s" % (k, c, t) for k, c, t, _w in plan))
            if not a.yes:
                log.say("dry run (pass --yes to write them)")
            elif LCH.processes_named(("Legacy.exe", "SpaceEngineers.exe")) and not getattr(a, "cfg", None):
                log.say("Space Engineers is running: close it first (it rewrites these files)")
                rc = 1
            else:
                done = apply_plan(plan, p["cfg"], p["water"], p["restoreDir"])
                log.say("restored %d value(s); the originals copied to %s" % (len(done), p["restoreDir"]), ev="settings_restore",
                        restored=[list(d) for d in done])
    if "registry" in only and not getattr(a, "cfg", None):
        r = LCH.restore_registry(log, a.yes)
        rc = rc or r
    return rc

# ----------------------------------------------------------------------------------------------------------------
# restart-renderer (CONTRACT §7 "Relaunch")
# ----------------------------------------------------------------------------------------------------------------

def relaunch_check(f: dict):
    """('go' | 'wait' | 'refuse', why). f: se_alive, old_sn_pid, old_sn_alive, crash_handlers [pids], since_exit_s
 (None: unknown), cooldown_s, section ('absent' | 'present' | 'error'), recorded_sn_pid, recorded_sn_alive,
 sn_holders [pids of Subnautica processes holding the section]."""
    if not f.get("se_alive"):
        return "refuse", "Space Engineers is not running: start both with `launcher.py run` (restart-renderer is for a renderer " \
                         "that closed while SE runs)"
    if f.get("old_sn_alive"):
        return "wait", "the old Subnautica (pid %s) is still running; it quits by itself or is closed normally" % f.get("old_sn_pid")
    if f.get("crash_handlers"):
        return "wait", "UnityCrashHandler64 still running (pid %s): it ends when the crash report is done; if it hangs, end it " \
                       "with `launcher.py kill --pid N`" % ", ".join(str(p) for p in f["crash_handlers"])
    sec = f.get("section")
    if sec == "error":
        return "refuse", "the section could not be inspected (%s)" % f.get("section_note", "")
    if sec == "present":
        if f.get("recorded_sn_alive"):
            return "refuse", "the section's snPid %s is alive: another Subnautica owns the renderer" % f.get("recorded_sn_pid")
        if f.get("sn_holders"):
            return "refuse", "a Subnautica process (pid %s) still holds the section" % ", ".join(str(p) for p in f["sn_holders"])
    since, cd = f.get("since_exit_s"), f.get("cooldown_s", RELAUNCH_COOLDOWN_S)
    if since is None or since < cd:
        return "wait", "cool-down: %.0f s left of %.0f s after the old renderer's exit" % (cd - (since or 0.0), cd)
    return "go", "old renderer gone %.0f s ago, no crash handler, section %s" % (since, "free" if sec == "absent" else
                                                                                   "held by SE only (re-initialised by creator rule 3)")

def _relaunch_facts(LCH, a, se_pid: int, state: dict) -> dict:
    S, L = LCH.S, LCH.L
    procs = S.list_processes()
    names = {p["pid"]: p["name"] for p in procs}
    f = {"se_alive": S.process_state(se_pid) == "alive", "cooldown_s": a.cooldown}
    sec_state, holders, note = S.section_holders(a.name)
    f["section"], f["section_note"] = sec_state, note
    rec_pid, rec_hb_age = 0, None
    if sec_state == "present":
        try:
            sec = S.Section.open(a.name, readonly=True)
        except OSError:
            sec = None
        if sec is not None:
            try:
                if sec.mapped_size >= L.HEADER_SIZE:
                    rec_pid, _g = sec.identity("sn")
                    rec_hb_age = max(0, S.tick64() - sec.hdr("snHeartbeatMs")) / 1000.0
            finally:
                sec.close()
        f["sn_holders"] = [p for p in (holders or []) if names.get(p, "").lower() == "subnautica.exe"]
    old = a.old_sn_pid or rec_pid or state.get("old_pid") or 0
    f["old_sn_pid"] = old
    f["recorded_sn_pid"] = rec_pid
    f["recorded_sn_alive"] = bool(rec_pid) and rec_pid != old and S.process_state(rec_pid) == "alive"
    f["old_sn_alive"] = bool(old) and S.process_state(old) == "alive"
    if a.sn_cmd is None:
        f["old_sn_alive"] = f["old_sn_alive"] or any(p["name"].lower() == "subnautica.exe" for p in procs)
    f["crash_handlers"] = [p["pid"] for p in procs if p["name"].lower() in {n.lower() for n in LCH.CRASH_HANDLERS}]
    # the old renderer's exit time: the run loop's record, else its last heartbeat in the section, else first seen gone
    since = None
    rec = _load(renderer_exit_path(LCH))
    if rec and old and rec.get("pid") == old:
        since = time.time() - float(rec.get("exitEpoch", time.time()))
    elif rec_pid and rec_pid == old and rec_hb_age is not None:
        since = rec_hb_age
    if f["old_sn_alive"]:
        state.pop("gone_at", None)
    else:
        state.setdefault("gone_at", time.monotonic())
        if since is None or since < 0:
            since = time.monotonic() - state["gone_at"]   # unknown exit time: the cool-down runs from now
    f["since_exit_s"] = since
    return f

def restart_renderer(LCH, log, a) -> int:
    S, L = LCH.S, LCH.L
    se_pid = a.se_pid
    if not se_pid:
        cands = [p for p in LCH.processes_named(("Legacy.exe", "SpaceEngineers.exe"))]
        cl = LCH.command_lines([p["pid"] for p in cands if p["name"].lower() == "spaceengineers.exe"])
        cands = [p for p in cands if "-report" not in cl.get(p["pid"], "")]
        se_pid = cands[0]["pid"] if len(cands) == 1 else 0
        if len(cands) > 1:
            log.say("several Space Engineers processes (%s): pass --se-pid" % ", ".join(str(p["pid"]) for p in cands))
            return 1
    state = {}
    deadline = time.monotonic() + a.wait
    last_why = None
    told_switch = False
    while True:
        f = _relaunch_facts(LCH, a, se_pid, state)
        verdict, why = relaunch_check(f)
        if why != last_why or verdict != "wait":
            log.say("restart-renderer: %s: %s" % (verdict.upper(), why), ev="relaunch_check", verdict=verdict, why=why,
                    facts={k: v for k, v in f.items() if k != "section_note"})
            last_why = why
        if verdict == "refuse":
            return 1
        if verdict == "go":
            break
        if not told_switch and not a.no_foreground_wait:
            told_switch = True
            log.say("Switch back to Space Engineers now: the renderer starts in the background after the cool-down and must "
                    "not take the focus.")
        if a.dry_run or time.monotonic() >= deadline:
            log.say("restart-renderer: %s" % ("dry run: not started" if a.dry_run else "gave up after %.0f s (nothing started)" % a.wait),
                    ev="relaunch_end", started=False)
            return 0 if a.dry_run else 1
        time.sleep(1.0)
    if a.dry_run:
        log.say("restart-renderer: dry run: would start the renderer now", ev="relaunch_end", started=False)
        return 0
    if not a.no_foreground_wait and se_pid:
        t_fg = time.monotonic()
        while time.monotonic() - t_fg < FOREGROUND_WAIT_S:
            fg, _h = S.foreground_pid()
            if fg == se_pid:
                break
            time.sleep(0.25)
        fg, _h = S.foreground_pid()
        log.say("foreground before the start: pid %s (%s)" % (fg, "Space Engineers" if fg == se_pid else "not SE: the new window "
                                                                "may come to the front once"), ev="relaunch_foreground", seForeground=fg == se_pid)
    _sn_mutex, _se_mutex, launcher_object = L.mutex_names(a.name)
    sn_cmd, _se_cmd = LCH.session_commands(a)
    sn_cwd = LCH.SUBNAUTICA if not a.sn_cmd else LCH.REPO
    token = S.NamedObjectHolder(launcher_object)
    env = dict(os.environ)
    env[L.BRIDGE_ENV] = "1"
    # S11  S6/S11 as the `run` session being restarted had them (its play-env record), else off as before S11
    for _k, _v in play_env(LCH).items():
        env.setdefault(_k, _v)
    try:
        sn = LCH.popen_detached(sn_cmd, sn_cwd, env, a.stdout if a.sn_cmd and getattr(a, "stdout", None) else None)
        log.say("Subnautica started: pid %d (renderer restart; SE pid %s untouched)" % (sn.pid, se_pid or "?"), ev="sn_started",
                pid=sn.pid, relaunch=True)
        ready = LCH.wait_ready(log, a.name, L.SN_PHASE[a.ready_phase], a.timeout, sn, check_pid=not getattr(a, "any_sn_pid", False))
    finally:
        token.close()
        log.event("token_released")
    try:
        os.remove(renderer_exit_path(LCH))
    except OSError:
        pass
    if not ready:
        log.say("the restarted renderer did not reach %s: SE keeps its own water view (Subnautica left running; close it "
                "normally)" % a.ready_phase, ev="relaunch_end", started=True, ready=False)
        return 1
    log.say("renderer back: SE sees a new Subnautica generation, re-synchronises and resends everything; the next dive uses it",
            ev="relaunch_end", started=True, ready=True)
    if a.no_wait_exits or not se_pid:
        return 0
    log.say("Waiting on both (Ctrl-C leaves them running).")
    return wait_exits(LCH, log, a, sn, PidProcess(S, se_pid), settings=False)

# ----------------------------------------------------------------------------------------------------------------
# kill (exact PID, typed confirmation)
# ----------------------------------------------------------------------------------------------------------------

def kill(LCH, log, a) -> int:
    names = {p["pid"]: p["name"] for p in LCH.S.list_processes()}
    name = names.get(a.pid)
    if name is None:
        log.say("pid %d is not running" % a.pid)
        return 1
    allowed = {n.lower() for n in tuple(LCH.GAME_PROCESSES) + tuple(LCH.CRASH_HANDLERS)}
    if name.lower() not in allowed:
        log.say("pid %d is %s, not a game or crash-handler process: refused" % (a.pid, name))
        return 1
    return 0 if LCH.confirm_kill(log, a.pid, name, True) else 1

# ----------------------------------------------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------------------------------------------

def add_parsers(sub, LCH):
    p = sub.add_parser("restart-renderer", help="restart Subnautica (the renderer) while SE runs: waits for the old process, "
                                                "its crash handler and a 20 s cool-down (CONTRACT 7 Relaunch)")
    p.add_argument("--cooldown", type=float, default=RELAUNCH_COOLDOWN_S, help="seconds after the old renderer's exit (20)")
    p.add_argument("--wait", type=float, default=180.0, help="give up after this many seconds of waiting")
    p.add_argument("--se-pid", type=int, default=0, help="the running SE (default: the one Legacy/SpaceEngineers process)")
    p.add_argument("--old-sn-pid", type=int, default=0, help="the renderer that closed (default: the section's snPid)")
    p.add_argument("--no-foreground-wait", action="store_true", help="start without waiting for SE to be in front")
    p.add_argument("--no-wait-exits", action="store_true", help="return once the renderer is ready")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="ready wait (s)")
    p.add_argument("--dry-run", action="store_true", help="run the checks only")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in tests only)")
    p.add_argument("--stdout", default=None, help="stand-in tests: the stand-in's JSONL output file")
    p.add_argument("--any-sn-pid", action="store_true")
    p.set_defaults(se_cmd=None, cont=False)
    p = sub.add_parser("restore-settings", help="put SpaceEngineers.cfg's audio keys, the Water Mod volume and the Subnautica "
                                                "Screenmanager keys back from the launcher's backups (CONTRACT 1.1)")
    p.add_argument("--yes", action="store_true", help="write (default: list what would change)")
    p.add_argument("--only", default="water,secfg,registry", help="comma list of water, secfg, registry")
    p.add_argument("--cfg", default=None, help=argparse_hidden())
    p.add_argument("--water", default=None, help=argparse_hidden())
    p.add_argument("--backup", default=None, help=argparse_hidden())
    p = sub.add_parser("kill", help="end a game or crash-handler process by its exact PID after you type the PID (the launcher "
                                    "never does it by itself)")
    p.add_argument("--pid", type=int, required=True)

def argparse_hidden():
    import argparse
    return argparse.SUPPRESS

def run(LCH, log, a) -> int:
    if a.cmd == "restart-renderer":
        return restart_renderer(LCH, log, a)
    if a.cmd == "restore-settings":
        return restore_settings(LCH, log, a)
    if a.cmd == "kill":
        return kill(LCH, log, a)
    return 2
