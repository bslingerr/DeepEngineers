"""Path resolution of launcher.py. Python 3.10 stdlib.

Two modes, decided by where launcher.py lives:
 * dev (the repository, src/DeepEngineers.Launcher/): every path is exactly the S0-S7 value, i.e. this PC's
 defaults with the DEEPENG_SE_BIN64 / DEEPENG_SUBNAUTICA / DEEPENG_PULSAR overrides (Directory.Build.props,
 build.ps1); backups in ~/. manifests under docs/install.
 * release (a release folder: <root>/launcher/launcher.py with <root>/release.json, written by
 tools/release/make_release.py): no path of the development PC is used. The game folders come from the
 environment overrides, else the installer's %LOCALAPPDATA%\\DeepEngineers\\install\\paths.json, else Steam itself
 (registry + libraryfolders.vdf, de_steam.py); Pulsar is the installer's copy (%LOCALAPPDATA%\\DeepEngineers\\Pulsar
 unless the installer recorded an existing one); backups live in %LOCALAPPDATA%\\DeepEngineers\\backups (Play.cmd's
 zip step, `launcher.py backup`); "the build" that preflight compares the deployed DLLs with is <root>/plugins.

apply_release_env puts what the plugins read from the environment into the launcher's own environment (the games
inherit it, launcher.py start_games copies os.environ): DEEPENG_SUBNAUTICA (the SE terrain gate's Subnautica folder,
src/DeepEngineers.Terrain/SnSourceManifest.cs, whose fallback is the development PC's Steam path) and the Subnautica
host save chosen by the installer (DEEPENGINEERS_SN_HOST_SESSION / DEEPENGINEERS_SN_HOST_SLOT, HostSaveRules.S1Switches;
the plugin's built-in default session is the development PC's slot). Values already set in the environment win.
"""
from __future__ import annotations

import json
import os

RELEASE_MARKER = "release.json"
PATHS_FILE = "paths.json"
MANIFEST_FILE = "install-manifest.json"

# the S0-S7 defaults of this PC (dev mode only; never used by a release folder)
DEV_SE_BIN64 = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64"
DEV_SUBNAUTICA = r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"

def release_root(here: str):
    root = os.path.normpath(os.path.join(here, ".."))
    return root if os.path.isfile(os.path.join(root, RELEASE_MARKER)) else None

def local_data(env=None) -> str:
    env = os.environ if env is None else env
    return os.path.join(env.get("LOCALAPPDATA") or os.path.expanduser("~"), "DeepEngineers")

def install_dir(env=None) -> str:
    return os.path.join(local_data(env), "install")

def load_json(path: str) -> dict:
    try:
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        return doc if isinstance(doc, dict) else {}
    except (OSError, ValueError):
        return {}

def steam_games(env=None) -> dict:
    """{"seBin64", "subnautica", "seLibrary"} from Steam (None where not found). Read-only."""
    try:
        import de_steam as D
        import de_pins as P
    except ImportError:
        return {"seBin64": None, "subnautica": None, "seLibrary": None}
    root = D.steam_root(env)
    libs = D.library_folders(root) if root else []
    se = D.find_app(libs, P.SE_APP_ID)
    sn = D.find_app(libs, P.SN_APP_ID)
    return {"seBin64": os.path.join(se.path, "Bin64") if se and se.path else None,
            "subnautica": sn.path if sn else None,
            "seLibrary": se.library if se else None}

def resolve(here: str, env=None) -> dict:
    env = os.environ if env is None else env
    rel = release_root(here)
    repo = os.path.normpath(os.path.join(here, "..", ".."))
    local = local_data(env)
    if rel is None:
        return {
            "mode": "dev", "releaseRoot": None, "repo": repo, "local": local,
            "seBin64": env.get("DEEPENG_SE_BIN64") or DEV_SE_BIN64,
            "subnautica": env.get("DEEPENG_SUBNAUTICA") or DEV_SUBNAUTICA,
            "pulsar": env.get("DEEPENG_PULSAR") or os.path.join(repo, "third_party", "pulsar", "extracted"),
            "backups": os.path.join(os.path.expanduser("~"), ".universal-modder", "backups"),
            "plugins": None,
            "registryBackupCopy": os.path.join(repo, "docs", "install", "subnautica-screenmanager-backup.json"),
            "manifest": os.path.join(repo, "docs", "install", "deepengineers-files.txt"),
            "worldModSrc": os.path.join(repo, "src", "DeepEngineers.WorldMod"),
            "backupHint": "um backup create %s",
            "backupScope": "full",
            "snHostSession": None, "snHostSlot": None, "pathsFile": None, "source": "dev defaults",
        }
    inst = os.path.join(local, "install")
    pj_path = os.path.join(inst, PATHS_FILE)
    pj = load_json(pj_path)
    se_bin64, subnautica = pj.get("seBin64"), pj.get("subnautica")
    source = "paths.json" if pj else "Steam"
    if not (se_bin64 and subnautica) or not (os.path.isdir(se_bin64) and os.path.isdir(subnautica)):
        found = steam_games(env)
        se_bin64 = se_bin64 if se_bin64 and os.path.isdir(se_bin64) else found["seBin64"]
        subnautica = subnautica if subnautica and os.path.isdir(subnautica) else found["subnautica"]
        source = "Steam" if not pj else "paths.json + Steam"
    return {
        "mode": "release", "releaseRoot": rel, "repo": None, "local": local,
        "seBin64": env.get("DEEPENG_SE_BIN64") or se_bin64 or "",
        "subnautica": env.get("DEEPENG_SUBNAUTICA") or subnautica or "",
        "pulsar": env.get("DEEPENG_PULSAR") or pj.get("pulsar") or os.path.join(local, "Pulsar"),
        "backups": os.path.join(local, "backups"),
        "plugins": os.path.join(rel, "plugins"),
        "registryBackupCopy": os.path.join(inst, "subnautica-screenmanager-backup.json"),
        "manifest": os.path.join(inst, "deepengineers-files.txt"),
        "worldModSrc": os.path.join(rel, "worldmod", "DeepEngineers.World"),
        "backupHint": "Play.cmd makes it: launcher.py backup --sets %s",
        "backupScope": "de",
        "snHostSession": pj.get("snHostSession"), "snHostSlot": pj.get("snHostSlot"),
        "pathsFile": pj_path if pj else None, "source": source,
    }

def apply_release_env(paths: dict, env=None) -> dict:
    """Release mode only: the plugins' environment inputs (see the module text). Returns what was set."""
    env = os.environ if env is None else env
    done = {}
    if paths.get("mode") != "release":
        return done
    for key, value in (("DEEPENG_SUBNAUTICA", paths.get("subnautica")),
                       ("DEEPENGINEERS_SN_HOST_SESSION", paths.get("snHostSession")),
                       ("DEEPENGINEERS_SN_HOST_SLOT", paths.get("snHostSlot"))):
        if value and not env.get(key):
            env[key] = value
            done[key] = value
    return done
