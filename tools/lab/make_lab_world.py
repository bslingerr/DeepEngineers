"""Creates the "DE Lab" Space Engineers world from an existing save, by script (no game UI).

 python -I tools/lab/make_lab_world.py --source "Earth Planet 19-44" [--name "DE Lab"] [--force]
 [--saves-root DIR]

- Refuses while Space Engineers runs (Legacy.exe or SpaceEngineers.exe): the save folders are live then, and SE
 rewrites the world files when it saves.
- Copies <Saves>/<steamid>/<source> to <Saves>/<steamid>/<name> (never touches the source; the copy's Backup folder is
 left out).
- Sets SessionName = name and adds the local world mod "DeepEngineers.World" (folder name under
 %APPDATA%\\SpaceEngineers\\Mods, PublishedFileId 0) to <Mods> if absent, keeping the Workshop mods already listed
 (Water Mod 2200451495, Text HUD API 758597413) and the game mode, in BOTH world files:
 - Sandbox.sbc (the checkpoint);
 - Sandbox_config.sbc (the world configuration), when the copy has one. SE loads the checkpoint and then, when this
 file exists, overrides the checkpoint's Settings, Mods and SessionName with its own

 Editing Sandbox.sbc alone would load the copy without the world mod.
- Prints what it changed in each file and checks the result by reading both files back.

The planet DE_4546B is then spawned from inside SE with the Spawn Menu (Shift+F10, docs/worldmod/DE_LAB_WORLD.md §4)
The S1 SE plugin has no spawn command.
Stdlib only.
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET  # read-only checks (check_world); never used to write

MOD_NAME = "DeepEngineers.World"
SE_PROCESSES = ("legacy.exe", "spaceengineers.exe")
XSI = "http://www.w3.org/2001/XMLSchema-instance"
XSD = "http://www.w3.org/2001/XMLSchema"

def saves_root(explicit=None):
    if explicit:
        return explicit
    base = os.path.join(os.environ["APPDATA"], "SpaceEngineers", "Saves")
    ids = [d for d in os.listdir(base) if d.isdigit()]
    if len(ids) != 1:
        sys.exit("expected one Steam id folder under %s, found %r" % (base, ids))
    return os.path.join(base, ids[0])

def se_running():
    """Names of running SE processes (tasklist, read-only); None when the list cannot be read."""
    try:
        out = subprocess.run(["tasklist", "/fo", "csv", "/nh"], capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    found = []
    for line in out.splitlines():
        name = line.split(",")[0].strip().strip('"').lower()
        if name in SE_PROCESSES:
            found.append(name)
    return found

MOD_ITEM_XML = ('    <ModItem FriendlyName="%s">\n      <Name>%s</Name>\n      <PublishedFileId>0</PublishedFileId>\n'
                '      <IsDependency>false</IsDependency>\n    </ModItem>\n' % (MOD_NAME, MOD_NAME))

def set_world(path, name, required):
    """SessionName and the local mod in one world file (checkpoint or world configuration). Returns a change list.

 Text edits only. Real run: a copy rewritten by ElementTree failed to load ("Namespace prefix 'xsd'
 is not defined"): the files use xsd: inside attribute VALUES (xsi:type="xsd:string"), so the serializer dropped
 the "unused" xmlns:xsd declaration. Every other byte, the declaration, the namespaces and the line endings stay.
 """
    if not os.path.exists(path):
        if required:
            sys.exit("missing: " + path)
        return ["absent (nothing to change)"]
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        text = f.read()
    changes = []
    m = re.search(r"<SessionName>([^<]*)</SessionName>", text)
    if m is None:
        sys.exit("SessionName missing in " + path)
    if m.group(1) != name:
        changes.append("SessionName %r -> %r" % (m.group(1), name))
        text = text[:m.start(1)] + name + text[m.end(1):]
    nl = "\r\n" if "\r\n" in text else "\n"
    item = MOD_ITEM_XML.replace("\n", nl)
    if ("<Name>%s</Name>" % MOD_NAME) in text:
        changes.append("local mod already listed")
    elif re.search(r"<Mods\s*/>", text):
        text = re.sub(r"<Mods\s*/>", "<Mods>" + nl + item + "  </Mods>", text, count=1)
        changes.append("<Mods> created with %s" % MOD_NAME)
    elif "</Mods>" in text:
        text = text.replace("</Mods>", item + "  </Mods>", 1)
        changes.append("local mod %s added" % MOD_NAME)
    else:
        sys.exit("<Mods> not found in " + path)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    return changes or ["already as wanted"]

def check_world(path, name):
    if not os.path.exists(path):
        return True
    root = ET.parse(path).getroot()
    mods = [m.findtext("Name") or "" for m in root.findall("Mods/ModItem")]
    return root.findtext("SessionName") == name and MOD_NAME in mods

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", required=True, help="existing save folder name (e.g. 'Earth Planet 2026-10-11 19-44')")
    ap.add_argument("--name", default="DE Lab")
    ap.add_argument("--force", action="store_true", help="replace an existing '<name>' folder")
    ap.add_argument("--saves-root", default=None, help="the <Saves>/<steamid> folder (default: found under %%APPDATA%%)")
    a = ap.parse_args()

    running = se_running()
    if running is None:
        sys.exit("cannot list processes: refusing (Space Engineers must not run while its saves are edited)")
    if running:
        sys.exit("Space Engineers is running (%s): close it first (its save folders are live)" % ", ".join(running))

    root = saves_root(a.saves_root)
    src = os.path.join(root, a.source)
    dst = os.path.join(root, a.name)
    if not os.path.isdir(src):
        sys.exit("source save not found: " + src)
    if os.path.isdir(dst):
        if not a.force:
            sys.exit("destination exists (use --force to replace): " + dst)
        shutil.rmtree(dst)
    shutil.copytree(src, dst, ignore=shutil.ignore_patterns("Backup"))
    print("copied", src, "->", dst)

    for fname, required in (("Sandbox.sbc", True), ("Sandbox_config.sbc", False)):
        path = os.path.join(dst, fname)
        print("%s: %s" % (fname, "; ".join(set_world(path, a.name, required))))
    ok = all(check_world(os.path.join(dst, f), a.name) for f in ("Sandbox.sbc", "Sandbox_config.sbc"))
    print("read back: SessionName and %s %s in every world file" % (MOD_NAME, "present" if ok else "NOT present"))
    mods_dir = os.path.join(os.environ.get("APPDATA", ""), "SpaceEngineers", "Mods", MOD_NAME)
    print("world mod folder present:", os.path.isdir(mods_dir), mods_dir)
    print("next (docs/worldmod/DE_LAB_WORLD.md): check that no planet of the copied save lies within 200 km of "
          "C = (-1000000, 250000, 1500000); load '%s' from Load Game (-continue loads the last world played); spawn "
          "DE_4546B with the Spawn Menu (section 4), then the owner-approved save edit to the integer centre (section 5)."
          % a.name)
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
