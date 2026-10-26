"""Fake machines for the S9 installer tests: Steam with two libraries, both games with their version
files, Workshop items, a Subnautica host save, a small terrain listing, Pulsar and BepInEx zips served from a local
folder through file:// URLs, and a release folder built from fake plugins plus the repository's template world."""
from __future__ import annotations

import hashlib
import json
import os
import pathlib
import shutil
import zipfile

import de_pins as P
import de_steam as D

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
TEMPLATE = os.path.join(REPO, "tools", "release", "world-template", "DeepEngineers")
HOSTSAVE = os.path.join(REPO, "tools", "release", "hostsave", "slot")
FAKE_USER = "76561197960265729"          # account 1: a placeholder SteamID64, not a person

def write(path: str, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    mode = "wb" if isinstance(data, bytes) else "w"
    with open(path, mode, **({} if isinstance(data, bytes) else {"encoding": "utf-8", "newline": "\n"})) as f:
        f.write(data)
    return path

def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

def sha_file(path: str) -> str:
    with open(path, "rb") as f:
        return sha(f.read())

def snapshot(root: str) -> dict:
    out = {}
    for base, _d, files in os.walk(root):
        for n in files:
            full = os.path.join(base, n)
            out[os.path.relpath(full, root).replace(os.sep, "/")] = sha_file(full)
    return out

def acf(app_id, installdir, build, auto_update=1, flags=4) -> str:
    return ('"AppState"\n{\n\t"appid"\t\t"%s"\n\t"StateFlags"\t\t"%d"\n\t"installdir"\t\t"%s"\n\t"buildid"\t\t"%s"\n'
            '\t"AutoUpdateBehavior"\t\t"%d"\n}\n' % (app_id, flags, installdir, build, auto_update))

class FakeMachine:
    def __init__(self, root: str, *, se_build=P.SE_BUILD_ID, sn_build=P.SN_BUILD_ID, changeset=P.SN_CHANGESET,
                 water_version=P.WATER_MOD_VERSION, workshop=True, host=True, auto_update=1):
        self.root = root
        self.steam = os.path.join(root, "Steam")
        self.lib2 = os.path.join(root, "D", "SteamLibrary")
        self.appdata = os.path.join(root, "Users", "player", "AppData", "Roaming")
        self.localappdata = os.path.join(root, "Users", "player", "AppData", "Local")
        self.env = {"APPDATA": self.appdata, "LOCALAPPDATA": self.localappdata, "DEEPENG_STEAM_ROOT": self.steam}
        os.makedirs(self.appdata)
        os.makedirs(self.localappdata)
        lib2_vdf = self.lib2.replace("\\", "\\\\")
        steam_vdf = self.steam.replace("\\", "\\\\")
        write(os.path.join(self.steam, "steamapps", "libraryfolders.vdf"),
              '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"%s"\n\t\t"apps"\n\t\t{\n\t\t\t"244850"\t\t"1"\n\t\t}\n\t}\n'
              '\t"1"\n\t{\n\t\t"path"\t\t"%s"\n\t\t"apps"\n\t\t{\n\t\t\t"264710"\t\t"1"\n\t\t}\n\t}\n}\n' % (
                  steam_vdf, lib2_vdf))
        write(os.path.join(self.steam, "config", "loginusers.vdf"),
              '"users"\n{\n\t"%s"\n\t{\n\t\t"AccountName"\t\t"player"\n\t\t"MostRecent"\t\t"1"\n'
              '\t\t"Timestamp"\t\t"1790000000"\n\t}\n}\n' % FAKE_USER)
        # Space Engineers in the Steam folder's own library
        self.se = os.path.join(self.steam, "steamapps", "common", "SpaceEngineers")
        write(os.path.join(self.se, "Bin64", "SpaceEngineers.exe"), b"MZ fake se")
        write(os.path.join(self.steam, "steamapps", "appmanifest_244850.acf"),
              acf(P.SE_APP_ID, "SpaceEngineers", se_build, auto_update))
        if workshop:
            write(os.path.join(self.steam, "steamapps", "workshop", "content", P.SE_APP_ID, P.WATER_MOD_ID,
                               *P.WATER_MOD_VERSION_FILE),
                  'namespace Jakaria { public static class WaterData { public const string Version = "%s"; } }\n' %
                  water_version)
            write(os.path.join(self.steam, "steamapps", "workshop", "content", P.SE_APP_ID, P.TEXT_HUD_API_ID,
                               "metadata.mod"), "<ModMetadata />\n")
        # Subnautica in the second library
        self.sn = os.path.join(self.lib2, "steamapps", "common", "Subnautica")
        write(os.path.join(self.sn, "Subnautica.exe"), b"MZ fake subnautica")
        write(os.path.join(self.sn, *P.SN_CHANGESET_FILE), changeset + "\n")
        write(os.path.join(self.lib2, "steamapps", "appmanifest_264710.acf"),
              acf(P.SN_APP_ID, "Subnautica", sn_build, auto_update))
        oct_dir = os.path.join(self.sn, *P.SN_OCTREE_DIR)
        write(os.path.join(os.path.dirname(oct_dir), "index.txt"), "7\n" + "\n".join(P.SN_TERRAIN_INDEX_LINES) + "\n")
        for name, size in (("compiled-batch-1-7-1.optoctrees", 10), ("compiled-batch-2-19-3.optoctrees", 20),
                           ("compiled-batch-0-10-0.optoctrees", 5)):
            write(os.path.join(oct_dir, name), b"o" * size)
        write(os.path.join(oct_dir, "readme.txt"), "not a batch")
        r = D.terrain_source(self.sn, P.SN_OCTREE_DIR, P.SN_TERRAIN_INDEX_LINES, P.SN_BUILD_ID)
        self.terrain = {"files": r["files"], "bytes": r["bytes"], "rows": r["rows"], "hash": r["hash"],
                        "index": P.SN_TERRAIN_INDEX_LINES}
        if host:
            write(os.path.join(self.sn, "SNAppData", "SavedGames", "slot0003", "gameinfo.json"),
                  json.dumps({"version": 2, "gameTime": 40, "changeSet": int(P.SN_CHANGESET), "session":
                              "11111111-2222-4333-8444-555555555555", "gameMode": 1, "corrupted": False}))
            write(os.path.join(self.sn, "SNAppData", "SavedGames", "slot0001", "gameinfo.json"),
                  json.dumps({"version": 2, "gameTime": 900, "changeSet": int(P.SN_CHANGESET), "session":
                              "99999999-2222-4333-8444-555555555555", "gameMode": 0, "corrupted": False}))
        os.makedirs(os.path.join(self.appdata, "SpaceEngineers", "Saves", FAKE_USER))
        self.server = os.path.join(root, "server")
        self.specs = self._zips()

    def _zips(self) -> dict:
        os.makedirs(self.server)
        pz = os.path.join(self.server, "Pulsar-test.zip")
        legacy = b"MZ fake pulsar legacy"
        with zipfile.ZipFile(pz, "w") as z:
            z.writestr("Legacy.exe", legacy)
            z.writestr("Legacy.exe.config", "<configuration />")
            z.writestr("Libraries/Legacy/0Harmony.dll", b"harmony")
            z.writestr("Libraries/", b"")
        bz = os.path.join(self.server, "BepInEx-test.zip")
        self.pack_files = {"winhttp.dll": b"MZ doorstop", "doorstop_config.ini": "[General]\nenabled=true\n",
                           "steam_appid.txt": "264710", "BepInEx/core/BepInEx.dll": b"MZ bepinex core",
                           "BepInEx/core/0Harmony.dll": b"MZ harmonyx", "BepInEx/config/BepInEx.cfg": "[Chainloader]\n",
                           "BepInEx/plugins/.tobey.bepinex.pack/Tobey.FileTree.dll": b"MZ filetree"}
        with zipfile.ZipFile(bz, "w") as z:
            for n, d in self.pack_files.items():
                z.writestr(n, d)
            z.writestr("BepInEx/plugins/", b"")

        def b(x):
            return x if isinstance(x, bytes) else x.encode("utf-8")
        return {
            "pulsar": dict(P.PULSAR, file="Pulsar-test.zip", url=pathlib.Path(pz).as_uri(), sha256=sha_file(pz),
                           size=os.path.getsize(pz), marker_sha256=sha(legacy), page="(test)"),
            "bepinex": dict(P.BEPINEX_PACK, file="BepInEx-test.zip", url=pathlib.Path(bz).as_uri(), sha256=sha_file(bz),
                            size=os.path.getsize(bz), page="(test)",
                            markers={"winhttp.dll": sha(b(self.pack_files["winhttp.dll"])),
                                     "BepInEx/core/BepInEx.dll": sha(b(self.pack_files["BepInEx/core/BepInEx.dll"]))}),
        }

def make_release(root: str) -> str:
    rel = os.path.join(root, "DeepEngineers-test")
    write(os.path.join(rel, "plugins", "plugin.dll"), b"MZ se plugin " + os.urandom(64))
    write(os.path.join(rel, "plugins", "DeepEngineers.Wire.dll"), b"MZ wire " + os.urandom(64))
    write(os.path.join(rel, "plugins", "DeepEngineers.SN.dll"), b"MZ sn plugin " + os.urandom(64))
    write(os.path.join(rel, "worldmod", "DeepEngineers.World", "metadata.mod"), "<ModMetadata />\n")
    write(os.path.join(rel, "worldmod", "DeepEngineers.World", "Data", "WaterConfig.xml"), "<WaterConfig />\n")
    write(os.path.join(rel, "worldmod", "DeepEngineers.World", "Data", "PlanetDataFiles", "DE_4546B", "up.png"),
          b"\x89PNG fake")
    shutil.copytree(TEMPLATE, os.path.join(rel, "world-template", "DeepEngineers"))
    shutil.copytree(HOSTSAVE, os.path.join(rel, "hostsave", "slot"))
    files = []
    for path, h in sorted(snapshot(rel).items()):
        files.append({"path": path, "sha256": h, "size": os.path.getsize(os.path.join(rel, *path.split("/")))})
    write(os.path.join(rel, "release.json"), json.dumps({"product": "DeepEngineers", "version": "test",
                                                         "flavour": "test", "files": files}, indent=1))
    return rel

def ctx(machine: FakeMachine, release: str, **kw):
    import de_install as I
    args = dict(env=machine.env, specs=machine.specs, check_processes=False, run_preflight=False, use_registry=False,
                terrain=machine.terrain)
    args.update(kw)
    return I.Ctx(release, **args)
