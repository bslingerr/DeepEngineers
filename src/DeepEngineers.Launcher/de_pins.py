"""DeepEngineers version pins: one place for every pinned game build, Workshop item and
third-party download that the installer (de_install.py), the release launcher (launcher_paths.py) and the release
build pass (tools/release/make_release.py) check. Python 3.10 stdlib, no imports.

Where each value comes from:
 * SE_BUILD_ID 24675677: CONTRACT section 0 ("SE 1.210.014 (Steam build 24675677)"), appmanifest_244850.acf "buildid".
 The SE plugin's own gate is src/DeepEngineers.SE/VersionGate.cs (ExpectedGameVersionConst 1210014, read in game from
 MyPerGameSettings.BasicGameInfo.GameVersion). SpaceEngineers.exe carries no version resource (1.0.0.0), so the
 Steam build id is the only offline reading.
 * SN_CHANGESET "83031": what the SN plugin logs as the game build (src/DeepEngineers.SN/HelloInfo.cs:
 SNUtils.GetPlasticChangeSetOfBuild reads Subnautica_Data/StreamingAssets/SNUnmanagedData/plastic_status.ignore).
 * SN_BUILD_ID 20241558: the terrain gate's pin (src/DeepEngineers.Wire/SiteV1.cs SnManifestBuildId, read by
 src/DeepEngineers.Terrain/SnSourceManifest.cs from <Subnautica>/../../appmanifest_264710.acf). A different build
 puts the SE side into the terrain fail-safe (saving blocked), so the installer stops on it too.
 * SN_TERRAIN_*: the rest of SnSourceManifest's pinned listing (CompiledOctreesCache: 5,416 batch files,
 1,203,085,204 bytes, rows 7..19, FNV-1a 64 of "<name>\\t<size>\\n" in ordinal order, index.txt lines 2-5).
 * WATER_MOD_VERSION "3.23": src/DeepEngineers.SE/WaterModClient.cs PinnedVersion (launcher.py WATER_MOD_PINNED).
 * PULSAR / BEPINEX_PACK: the SHA-256 and sizes are those of the zips the development PC installed from; the Subnautica install's winhttp.dll and BepInEx/core/BepInEx.dll equal the pack's.
 The URLs follow GitHub's release-asset scheme for the tags named in docs/recon (HYPOTHESIS until the first
 install on a second machine downloads them: a 404 makes the installer print the manual route, never a different
 file; the SHA-256 is always checked).
tests/release/test_pins.py re-reads the C# sources and fails when a pin here drifts from the plugins' own pins.
"""

PRODUCT = "DeepEngineers"

# --- Space Engineers -------------------------------------------------------------------------------------------
SE_APP_ID = "244850"
SE_BUILD_ID = "24675677"
SE_GAME_VERSION = 1210014           # the plugin's in-game pin (VersionGate)
SE_GAME_VERSION_TEXT = "1.210.014"

# --- Subnautica ------------------------------------------------------------------------------------------------
SN_APP_ID = "264710"
SN_BUILD_ID = "20241558"
SN_CHANGESET = "83031"
SN_CHANGESET_FILE = ("Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "plastic_status.ignore")
SN_OCTREE_DIR = ("Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "Build18", "CompiledOctreesCache")
SN_TERRAIN_FILES = 5416
SN_TERRAIN_BYTES = 1203085204
SN_TERRAIN_ROWS = (7, 19)
SN_TERRAIN_HASH = 0x2CC277CE91763285
SN_TERRAIN_INDEX_LINES = ("4096 3200 4096", "128 100 128", "32", "5 5 5")
SN_HOST_GAME_MODE_FREEDOM = 1       # gameinfo.json "gameMode" (HostSaveRules.LegacyGameMode.Freedom)

# --- Workshop (Space Engineers) --------------------------------------------------------------------------------
WATER_MOD_ID = "2200451495"
TEXT_HUD_API_ID = "758597413"
WATER_MOD_VERSION = "3.23"
WORKSHOP_ITEMS = (
    (WATER_MOD_ID, "Water Mod"),
    (TEXT_HUD_API_ID, "Text HUD API"),
)
WATER_MOD_VERSION_FILE = ("Data", "Scripts", "Water", "Water", "WaterData.cs")

def workshop_url(item_id: str) -> str:
    return "https://steamcommunity.com/sharedfiles/filedetails/?id=%s" % item_id

# --- Pulsar (SE plugin loader, portable) -----------------------------------------------------------------------
PULSAR = {
    "name": "Pulsar",
    "version": "2.4.3",
    "file": "Pulsar-v2.4.3-win-x64.zip",
    "url": "https://github.com/SpaceGT/Pulsar/releases/download/v2.4.3/Pulsar-v2.4.3-win-x64.zip",
    "page": "https://github.com/SpaceGT/Pulsar/releases/tag/v2.4.3",
    "sha256": "14b4eec93ee4d2c223a712944646f657d372c16beca4406843e4198f2e93ff8b",
    "size": 31380992,
    "licence": "MIT",
    # Legacy.exe inside the zip: an existing Pulsar folder with this file is the pinned release
    "marker": "Legacy.exe",
    "marker_sha256": "8808c7bf6e11a88e16d363f89483602ed7afb005793448064cc5cc72fbc41ad2",
}

# --- BepInEx for Subnautica (Tobey's pack: BepInEx 5.4.23.5 x64 + Doorstop, preconfigured for Subnautica) ----------
BEPINEX_PACK = {
    "name": "Tobey's BepInEx Pack for Subnautica",
    "version": "5.4.23-pack.3.1.1",
    "bepinex": "5.4.23.5",
    "file": "Tobey.s.BepInEx.Pack.for.Subnautica.zip",
    "url": "https://github.com/toebeann/BepInEx.Subnautica/releases/download/v5.4.23-pack.3.1.1/"
           "Tobey.s.BepInEx.Pack.for.Subnautica.zip",
    "page": "https://github.com/toebeann/BepInEx.Subnautica/releases/tag/v5.4.23-pack.3.1.1",
    "sha256": "21a1fd6ec22253a6cf05ccf1f6407f6055c95971a7dda7545b33481069a4dd3e",
    "size": 3835534,
    "licence": "BepInEx: LGPL-2.1; the pack's other parts: their own licence files",
    # files that identify an installed copy of this pack (relative to the Subnautica folder)
    "markers": {
        "winhttp.dll": "8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c",
        "BepInEx/core/BepInEx.dll": "19eb836818955e4f86818306aaf2baaee989be566d7da697f835b429ab585149",
    },
}

DOWNLOADS = (PULSAR, BEPINEX_PACK)

# --- The interpreter the installer brings ----------------------------------------------
# CPython's Windows embeddable package: 3.10.11 is the last 3.10 with Windows binaries and the version every launcher
# and installer module was written and tested on (the development PC runs 3.10.11). install.ps1 downloads it into
# %LOCALAPPDATA%\DeepEngineers\python (Play.cmd and the shortcuts use it), so the player's PC needs no Python.
# python.org publishes only MD5 and size for this file; "sha256" is filled by `python -I tools/release/S9_pin_python.py <the downloaded zip> --write`, which
# refuses a file whose size or MD5 differ from python.org's. While it is empty, install.ps1 never downloads an
# interpreter and falls back to a Python 3.10+ already on the PC.
PYTHON_EMBED = {
    "name": "Python embeddable package (64-bit)",
    "version": "3.10.11",
    "file": "python-3.10.11-embed-amd64.zip",
    "url": "https://www.python.org/ftp/python/3.10.11/python-3.10.11-embed-amd64.zip",
    "page": "https://www.python.org/downloads/release/python-31011/",
    "size": 8629277,
    "md5": "f1c0538b060e03cbb697ab3581cb73bc",
    "sha256": "608619f8619075629c9c69f361352a0da6ed7e62f83a0e19c63e0ea32eb7629d",
    "licence": "PSF License (LICENSE.txt inside the zip; downloaded from python.org, not redistributed)",
}

def as_dict() -> dict:
    """Every pin as plain data (release.json, `launcher.py paths --json`)."""
    return {
        "se": {"appId": SE_APP_ID, "buildId": SE_BUILD_ID, "gameVersion": SE_GAME_VERSION_TEXT},
        "subnautica": {"appId": SN_APP_ID, "buildId": SN_BUILD_ID, "changeset": SN_CHANGESET,
                       "terrainFiles": SN_TERRAIN_FILES, "terrainBytes": SN_TERRAIN_BYTES,
                       "terrainHash": "0x%016x" % SN_TERRAIN_HASH},
        "workshop": {"waterMod": {"id": WATER_MOD_ID, "version": WATER_MOD_VERSION},
                     "textHudApi": {"id": TEXT_HUD_API_ID}},
        "pulsar": {k: PULSAR[k] for k in ("version", "url", "sha256", "size")},
        "bepinexPack": {k: BEPINEX_PACK[k] for k in ("version", "bepinex", "url", "sha256", "size")},
        # install.ps1 reads this entry from release.json before any Python exists (S9 one-click)
        "python": {k: PYTHON_EMBED[k] for k in ("version", "file", "url", "page", "sha256", "size")},
    }
