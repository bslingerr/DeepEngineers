"""Steam, game and Workshop discovery for the S9 installer and the release launcher. Python 3.10 stdlib.

Read-only: nothing here writes a file or a registry value. Every function takes its roots as arguments so the tests
(tests/release) run on fake Steam libraries; the registry is read only by steam_root_from_registry and
active_steam_user_from_registry, both of which return None off Windows or when the key is absent.

 steam_root HKCU\\Software\\Valve\\Steam SteamPath, else HKLM (WOW6432Node) InstallPath, else
 the two usual folders; DEEPENG_STEAM_ROOT overrides (tests, unusual installs)
 library_folders(steam_root) steamapps/libraryfolders.vdf ("path" entries, old and new format) + the root itself
 find_app(libraries, app_id) the library whose steamapps holds appmanifest_<id>.acf and its install folder
 read_acf(path) / parse_vdf(text) Valve KeyValues text (quoted keys and values, nested braces, // comments)
 workshop_item_dir(...) <library>/steamapps/workshop/content/<app>/<item>
 water_mod_version(item_dir) `Version = "x"` in (the launcher's reading)
 sn_changeset(sn_dir) plastic_status.ignore (the SN plugin's reading of the game build)
 terrain_source(sn_dir,...) the SE terrain gate's listing check (SnSourceManifest.Measure/Matches, re-done)
 sn_host_slots(sn_dir) SNAppData/SavedGames/slot*/gameinfo.json (session, gameMode, changeSet, corrupted)
 steam_user_ids(steam_root) config/loginusers.vdf (SteamID64, MostRecent first)
"""
from __future__ import annotations

import json
import os
import re

try:
    import winreg
except ImportError:  # pragma: no cover
    winreg = None

STEAMID64_BASE = 76561197960265728
_BATCH = re.compile(r"^compiled-batch-(\d+)-(\d+)-(\d+)\.optoctrees$")
FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3

# ----------------------------------------------------------------------------------------------------------------
# KeyValues (VDF / ACF) text
# ----------------------------------------------------------------------------------------------------------------

def _tokens(text: str):
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c in " \t\r\n":
            i += 1
        elif c == "/" and text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j + 1
        elif c in "{}":
            yield c
            i += 1
        elif c == '"':
            out = []
            i += 1
            while i < n and text[i] != '"':
                if text[i] == "\\" and i + 1 < n:
                    nxt = text[i + 1]
                    out.append({"n": "\n", "t": "\t", "\\": "\\", '"': '"'}.get(nxt, "\\" + nxt))
                    i += 2
                else:
                    out.append(text[i])
                    i += 1
            i += 1
            yield ("s", "".join(out))
        else:
            j = i
            while j < n and text[j] not in " \t\r\n{}\"":
                j += 1
            yield ("s", text[i:j])
            i = j

def parse_vdf(text: str) -> dict:
    """Valve KeyValues text -> nested dicts (keys kept as written; a repeated key keeps its last value)."""
    root: dict = {}
    stack = [root]
    key = None
    for tok in _tokens(text.lstrip("\ufeff")):
        if tok == "{":
            child: dict = {}
            stack[-1][key if key is not None else ""] = child
            stack.append(child)
            key = None
        elif tok == "}":
            if len(stack) > 1:
                stack.pop()
            key = None
        elif key is None:
            key = tok[1]
        else:
            stack[-1][key] = tok[1]
            key = None
    return root

def read_vdf(path: str):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return parse_vdf(f.read())
    except OSError:
        return None

def ci_get(d: dict, key: str, default=None):
    """Case-insensitive key lookup (Valve files mix "AppState" / "appstate")."""
    if not isinstance(d, dict):
        return default
    if key in d:
        return d[key]
    low = key.lower()
    for k, v in d.items():
        if k.lower() == low:
            return v
    return default

# ----------------------------------------------------------------------------------------------------------------
# Steam root, libraries, apps
# ----------------------------------------------------------------------------------------------------------------

def _reg_value(hive, path: str, name: str):
    if winreg is None:
        return None
    for view in (0, getattr(winreg, "KEY_WOW64_32KEY", 0)):
        try:
            k = winreg.OpenKey(hive, path, 0, winreg.KEY_READ | view)
        except OSError:
            continue
        try:
            return winreg.QueryValueEx(k, name)[0]
        except OSError:
            continue
        finally:
            winreg.CloseKey(k)
    return None

def steam_root_from_registry():
    if winreg is None:
        return None
    for hive, path, name in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
                             (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                             (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath")):
        v = _reg_value(hive, path, name)
        if isinstance(v, str) and v.strip():
            p = os.path.normpath(v.strip().replace("/", os.sep))
            return os.path.realpath(p) if os.path.isdir(p) else p     # SteamPath is stored lower-case
    return None

def steam_root(env=None):
    """The Steam folder (holds steamapps/ and config/), or None."""
    env = os.environ if env is None else env
    cands = []
    if env.get("DEEPENG_STEAM_ROOT"):
        cands.append(env["DEEPENG_STEAM_ROOT"])
    else:
        reg = steam_root_from_registry()
        if reg:
            cands.append(reg)
        for base in (env.get("ProgramFiles(x86)"), env.get("ProgramFiles")):
            if base:
                cands.append(os.path.join(base, "Steam"))
    for c in cands:
        if c and os.path.isdir(os.path.join(c, "steamapps")):
            return os.path.normpath(c)
    return None

def library_folders(root: str) -> list:
    """Library roots (each holds steamapps/): the Steam root first, then libraryfolders.vdf's entries, de-duplicated."""
    libs = []

    def add(p):
        if not p:
            return
        p = os.path.normpath(p.replace("\\\\", "\\"))
        if os.path.normcase(p) not in {os.path.normcase(x) for x in libs} and os.path.isdir(os.path.join(p, "steamapps")):
            libs.append(p)

    add(root)
    for rel in (("steamapps", "libraryfolders.vdf"), ("config", "libraryfolders.vdf")):
        doc = read_vdf(os.path.join(root, *rel)) if root else None
        lf = ci_get(doc or {}, "libraryfolders") or ci_get(doc or {}, "LibraryFolders") or {}
        for k, v in (lf.items() if isinstance(lf, dict) else ()):
            if isinstance(v, dict):
                add(ci_get(v, "path"))
            elif isinstance(v, str) and k.isdigit():     # pre-2021 format: "1" "D:\\SteamLibrary"
                add(v)
    return libs

def read_acf(path: str):
    doc = read_vdf(path)
    return ci_get(doc or {}, "AppState") if doc else None

class AppInfo:
    """One installed Steam app: library root, install folder, the app manifest's fields."""

    def __init__(self, library: str, manifest_path: str, state: dict):
        self.library = library
        self.manifest_path = manifest_path
        self.state = state or {}
        self.installdir = ci_get(self.state, "installdir", "")
        self.path = os.path.join(library, "steamapps", "common", self.installdir) if self.installdir else None

    @property
    def build_id(self) -> str:
        return str(ci_get(self.state, "buildid", "") or "")

    @property
    def state_flags(self) -> int:
        try:
            return int(ci_get(self.state, "StateFlags", "0"))
        except ValueError:
            return 0

    @property
    def auto_update(self) -> int:
        """AutoUpdateBehavior: 0 always keep updated, 1 only update when launched, 2 high priority."""
        try:
            return int(ci_get(self.state, "AutoUpdateBehavior", "0"))
        except ValueError:
            return 0

    def as_dict(self) -> dict:
        return {"library": self.library, "path": self.path, "manifest": self.manifest_path, "buildId": self.build_id,
                "stateFlags": self.state_flags, "autoUpdate": self.auto_update}

def find_app(libraries, app_id: str):
    """The first library holding appmanifest_<id>.acf with an existing install folder (else the first manifest)."""
    found = None
    for lib in libraries:
        acf = os.path.join(lib, "steamapps", "appmanifest_%s.acf" % app_id)
        if not os.path.isfile(acf):
            continue
        info = AppInfo(lib, acf, read_acf(acf))
        if info.path and os.path.isdir(info.path):
            return info
        found = found or info
    return found

AUTO_UPDATE_TEXT = {0: "always keep this game updated", 1: "only update this game when I launch it",
                    2: "high priority"}

# ----------------------------------------------------------------------------------------------------------------
# Workshop, Water Mod
# ----------------------------------------------------------------------------------------------------------------

def workshop_item_dir(library: str, app_id: str, item_id: str) -> str:
    return os.path.join(library, "steamapps", "workshop", "content", app_id, item_id)

def workshop_item_present(path: str) -> bool:
    try:
        return os.path.isdir(path) and any(os.scandir(path))
    except OSError:
        return False

def water_mod_version(item_dir: str, rel=("Data", "Scripts", "Water", "Water", "WaterData.cs")):
    """(version or None, path, error or None): the same reading as launcher.py water_mod_version_check."""
    path = os.path.join(item_dir, *rel)
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError as e:
        return None, path, str(e)
    m = re.search(r'Version\s*=\s*"([^"]+)"', text)
    return (m.group(1) if m else None), path, (None if m else "no Version constant")

# ----------------------------------------------------------------------------------------------------------------
# Subnautica: build, terrain source, host saves
# ----------------------------------------------------------------------------------------------------------------

def sn_changeset(sn_dir: str, rel=("Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "plastic_status.ignore")):
    try:
        with open(os.path.join(sn_dir, *rel), encoding="utf-8-sig", errors="replace") as f:
            return f.read().strip() or None
    except OSError:
        return None

def fnv1a64(data: bytes, h: int = FNV_OFFSET) -> int:
    for b in data:
        h ^= b
        h = (h * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h

def terrain_source(sn_dir: str, octree_rel, index_lines, build_id_expected: str) -> dict:
    """SnSourceManifest.Measure + Matches in Python: the SE terrain gate's own pinned reading of Subnautica's
 CompiledOctreesCache (listing hash, file count, bytes, batch rows, index.txt lines 2-5, Steam build id)."""
    r = {"field": None, "found": None, "files": 0, "bytes": 0, "rows": None, "hash": None, "index": False,
         "buildId": None}
    oct_dir = os.path.join(sn_dir, *octree_rel)
    r["path"] = oct_dir
    acf = os.path.normpath(os.path.join(sn_dir, "..", "..", "appmanifest_264710.acf"))
    st = read_acf(acf) if os.path.isfile(acf) else None
    r["buildId"] = str(ci_get(st or {}, "buildid", "")) or None
    try:
        with open(os.path.join(os.path.dirname(oct_dir), "index.txt"), encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
        r["index"] = len(lines) >= 1 + len(index_lines) and all(
            lines[i + 1].strip() == index_lines[i] for i in range(len(index_lines)))
    except OSError:
        r["index"] = False
    if not os.path.isdir(oct_dir):
        r["field"], r["found"] = "folder", "missing " + oct_dir
        return r
    names, sizes, rows = [], {}, []
    for e in os.scandir(oct_dir):
        if not e.is_file():
            continue
        m = _BATCH.match(e.name)
        if not m:
            continue
        names.append(e.name)
        sizes[e.name] = e.stat().st_size
        rows.append(int(m.group(2)))
    names.sort()
    h = FNV_OFFSET
    for n in names:
        h = fnv1a64(("%s\t%d\n" % (n, sizes[n])).encode("utf-8"), h)
    r.update(files=len(names), bytes=sum(sizes.values()), rows=(min(rows), max(rows)) if rows else None, hash=h)
    return r

def terrain_matches(r: dict, files: int, total: int, rows, h: int, build_id: str):
    """(ok, field, found, expected) in SnSourceManifest.Matches order."""
    if r.get("field") == "folder":
        return False, "folder", r.get("found"), "CompiledOctreesCache"
    checks = (("index", r["index"], True), ("build", r["buildId"], build_id), ("files", r["files"], files),
              ("bytes", r["bytes"], total), ("rows", tuple(r["rows"]) if r["rows"] else None, tuple(rows)),
              ("manifestHash", r["hash"], h))
    for field, found, expected in checks:
        if found != expected:
            if field == "manifestHash":
                return False, field, "0x%016x" % (found or 0), "0x%016x" % expected
            return False, field, found, expected
    return True, None, None, None

def sn_host_slots(sn_dir: str) -> list:
    """Every slot folder with a readable gameinfo.json: {slot, session, gameMode, changeSet, corrupted, gameTime}."""
    out = []
    base = os.path.join(sn_dir, "SNAppData", "SavedGames")
    if not os.path.isdir(base):
        return out
    for e in sorted(os.scandir(base), key=lambda x: x.name):
        if not e.is_dir():
            continue
        gi = os.path.join(e.path, "gameinfo.json")
        try:
            with open(gi, encoding="utf-8-sig") as f:
                doc = json.load(f)
        except (OSError, ValueError):
            continue
        out.append({"slot": e.name, "session": str(doc.get("session") or ""), "gameMode": doc.get("gameMode"),
                    "changeSet": str(doc.get("changeSet") or ""), "corrupted": bool(doc.get("corrupted")),
                    "gameTime": doc.get("gameTime")})
    return out

def choose_host_slot(slots, changeset: str, freedom: int, session_wanted: str = None, slot_wanted: str = None):
    """(slot dict or None, reason): the Freedom slot the SN plugin will load, chosen like HostSaveRules.SelectSlot
 (session id; a slot name only when several slots share it). Preference without a wish: the slot with the
 current changeset, then the most played one; ties by slot name."""
    ok = [s for s in slots if s["gameMode"] == freedom and not s["corrupted"] and s["session"]]
    if slot_wanted:
        hit = [s for s in slots if s["slot"].lower() == slot_wanted.lower()]
        if not hit:
            return None, "slot %s not found" % slot_wanted
        s = hit[0]
        if s not in ok:
            return None, "slot %s is not a usable Freedom save (gameMode %s, corrupted %s)" % (
                s["slot"], s["gameMode"], s["corrupted"])
        return s, "named"
    if session_wanted:
        hit = [s for s in ok if s["session"].lower() == session_wanted.lower()]
        return (hit[0], "session") if hit else (None, "no Freedom slot has session %s" % session_wanted)
    if not ok:
        return None, "no Freedom save (gameMode %d) among %d slot(s)" % (freedom, len(slots))
    ok.sort(key=lambda s: (s["changeSet"] != changeset, -(s["gameTime"] or 0), s["slot"]))
    return ok[0], "best of %d Freedom slot(s)" % len(ok)

# ----------------------------------------------------------------------------------------------------------------
# Steam user (SE saves folder name)
# ----------------------------------------------------------------------------------------------------------------

def active_steam_user_from_registry():
    """SteamID64 of the user logged into the running Steam client (ActiveProcess\\ActiveUser), or None."""
    v = _reg_value(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam\ActiveProcess", "ActiveUser") if winreg else None
    try:
        v = int(v)
    except (TypeError, ValueError):
        return None
    return str(STEAMID64_BASE + v) if v > 0 else None

def steam_user_ids(root: str) -> list:
    """SteamID64s from config/loginusers.vdf, the MostRecent one first."""
    doc = read_vdf(os.path.join(root, "config", "loginusers.vdf")) if root else None
    users = ci_get(doc or {}, "users") or {}
    ids = [(k, v) for k, v in users.items() if k.isdigit() and isinstance(v, dict)]
    ids.sort(key=lambda kv: (str(ci_get(kv[1], "MostRecent", "0")) != "1",
                             -int(ci_get(kv[1], "Timestamp", "0") or 0) if str(ci_get(kv[1], "Timestamp", "0")).isdigit()
                             else 0))
    return [k for k, _v in ids]

def choose_steam_user(saves_root: str, active: str = None, login_ids=None):
    """(SteamID64 or None, how): the running client's user, else loginusers' MostRecent, else the only numeric folder
 under %APPDATA%\\SpaceEngineers\\Saves."""
    if active:
        return active, "Steam client (ActiveUser)"
    if login_ids:
        return login_ids[0], "loginusers.vdf (MostRecent)"
    try:
        nums = [e.name for e in os.scandir(saves_root) if e.is_dir() and e.name.isdigit()]
    except OSError:
        nums = []
    if len(nums) == 1:
        return nums[0], "the only folder in " + saves_root
    return None, ("%d numeric folders in %s and no Steam user found" % (len(nums), saves_root))
