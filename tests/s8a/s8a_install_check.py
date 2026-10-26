"""S8a.N6.install (ROADMAP "### S8a" oracle; CONTRACT §3.3 "The store": nothing is ever written into either game's install):
a read-only listing of both installs taken before and after a session, and their comparison. Stdlib only; nothing is
opened for writing under either install, nothing is hashed by content (a listing of 5,000+ files must stay cheap):
every file's size and modification time, per root, plus a listing hash of the Subnautica octree cache (the folder the
S4 source manifest measures: files and bytes).

 python -I tests/s8a/s8a_install_check.py list --out FILE [--subnautica DIR] [--se DIR]
 python -I tests/s8a/s8a_install_check.py compare BEFORE AFTER [--json OUT]
 python -I tests/s8a/s8a_install_check.py --selftest

Roots: the Subnautica install without its SNAppData folder (the Steam save slots live there: the host save is S1.N4's
business, not this listing's), SE's Bin64 and Content. The launcher (launcher_s8a.py, every session) writes
s8a-install-before.txt and s8a-install-after.txt into the session folder and records the comparison; the analyzer's
S8a.N6.install row reads it. Rows: a changed file (size or mtime), an added file, a removed file, and the octree listing
hash before/after.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
import tempfile

FORMAT = 1
SUBNAUTICA_DEFAULT = os.environ.get("DEEPENG_SUBNAUTICA") or r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"
SE_DEFAULT = os.environ.get("DEEPENG_SE") or (os.path.dirname(os.environ["DEEPENG_SE_BIN64"]) if os.environ.get("DEEPENG_SE_BIN64")
                                               else r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers")
OCTREES_REL = "Subnautica_Data/StreamingAssets/SNUnmanagedData/Build18/CompiledOctreesCache"
SN_EXCLUDE_TOP = ("SNAppData",)
# BepInEx rewrites these at every launch: listed as "info" entries, reported, never judged; BepInEx/plugins,
# BepInEx/core, BepInEx/patchers and everything else under the install stay watched
SN_INFO_PREFIXES = ("BepInEx/LogOutput.log", "BepInEx/cache/", "BepInEx/config/")
MAX_LISTED = 50          # diff entries kept per kind in the comparison

def info_kind(kind, rel):
    """The listing kind an entry belongs to: '<kind>-info' for the loader's own volatile files, else the root's kind."""
    if kind == "subnautica" and any(rel.startswith(p) for p in SN_INFO_PREFIXES):
        return kind + "-info"
    return kind

def fnv1a64(data: bytes) -> int:
    h = 0xcbf29ce484222325
    for b in data:
        h = ((h ^ b) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def roots_of(subnautica=None, se=None):
    sn = subnautica or SUBNAUTICA_DEFAULT
    se = se or SE_DEFAULT
    return [("subnautica", sn, SN_EXCLUDE_TOP), ("se-bin64", os.path.join(se, "Bin64"), ()), ("se-content", os.path.join(se, "Content"), ())]

def walk(root, exclude_top=()):
    """(rel, size, mtime_ns) of every regular file under root (symlinks not followed); os.scandir (one stat per entry)."""
    out = []
    stack = [root]
    ex = set(x.lower() for x in exclude_top)
    while stack:
        d = stack.pop()
        try:
            it = os.scandir(d)
        except OSError:
            continue
        with it:
            for e in it:
                try:
                    if e.is_dir(follow_symlinks=False):
                        if d == root and e.name.lower() in ex:
                            continue
                        stack.append(e.path)
                    elif e.is_file(follow_symlinks=False):
                        st = e.stat(follow_symlinks=False)
                        out.append((os.path.relpath(e.path, root).replace(os.sep, "/"), st.st_size, st.st_mtime_ns))
                except OSError:
                    continue
    out.sort()
    return out

def octree_hash(entries):
    """FNV-1a 64 over 'rel<TAB>size<LF>' of the octree cache files (the evidence the S4 source manifest measures: files, bytes)."""
    lines = ["%s\t%d\n" % (rel, size) for rel, size, _m in entries if rel.startswith(OCTREES_REL + "/")]
    return fnv1a64("".join(lines).encode("utf-8")) if lines else None

def listing(roots=None):
    roots = roots or roots_of()
    L = {"format": FORMAT, "time": datetime.datetime.utcnow().isoformat(timespec="seconds") + "Z", "roots": [], "entries": {}}
    for kind, path, exclude in roots:
        exists = os.path.isdir(path)
        ent = walk(path, exclude) if exists else []
        info = {"kind": kind, "path": path, "exists": exists, "files": len(ent), "bytes": sum(e[1] for e in ent)}
        if kind == "subnautica":
            h = octree_hash(ent)
            info["octreeHash"] = "%016x" % h if h is not None else None
            info["octreeFiles"] = sum(1 for e in ent if e[0].startswith(OCTREES_REL + "/"))
        L["roots"].append(info)
        L["entries"][kind] = {}
        for e in ent:
            L["entries"].setdefault(info_kind(kind, e[0]), {})[e[0]] = [e[1], e[2]]
    return L

def write_listing(path, L):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# s8a-install-listing %d\n# time %s\n" % (L["format"], L["time"]))
        for r in L["roots"]:
            f.write("# root %s %s exists=%d files=%d bytes=%d octreeHash=%s octreeFiles=%s\n" % (
                r["kind"], r["path"], 1 if r["exists"] else 0, r["files"], r["bytes"], r.get("octreeHash") or "-", r.get("octreeFiles", "-")))
        for kind in sorted(L["entries"]):
            for rel in sorted(L["entries"][kind]):
                size, m = L["entries"][kind][rel]
                f.write("%s\t%s\t%d\t%d\n" % (kind, rel, size, m))

def read_listing(path):
    L = {"format": None, "time": None, "roots": [], "entries": {}}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("# s8a-install-listing "):
                L["format"] = int(line.split()[2])
            elif line.startswith("# time "):
                L["time"] = line[7:]
            elif line.startswith("# root "):
                parts = line[7:].split(" ")
                kv = {}
                head = []
                for p in parts:
                    if "=" in p and p.split("=", 1)[0] in ("exists", "files", "bytes", "octreeHash", "octreeFiles"):
                        k, v = p.split("=", 1)
                        kv[k] = v
                    else:
                        head.append(p)
                kind = head[0]
                path_ = " ".join(head[1:])
                L["roots"].append({"kind": kind, "path": path_, "exists": kv.get("exists") == "1", "files": int(kv.get("files", 0)),
                                   "bytes": int(kv.get("bytes", 0)), "octreeHash": None if kv.get("octreeHash") in (None, "-") else kv["octreeHash"],
                                   "octreeFiles": None if kv.get("octreeFiles") in (None, "-") else int(kv["octreeFiles"])})
            elif line and not line.startswith("#"):
                kind, rel, size, m = line.split("\t")
                L["entries"].setdefault(kind, {})[rel] = [int(size), int(m)]
    return L

def compare(before, after):
    """{ok, changed, added, removed, info, counts, roots: {kind: {before, after}}, octreeHashBefore, octreeHashAfter}; a file
 is changed when its size or mtime differs; ok = nothing changed, added or removed and the octree hash equal. The
 loader's own volatile files ('<kind>-info' entries: BepInEx log, cache, config) are reported under `info`, never judged."""
    changed, added, removed, info = [], [], [], []
    kinds = sorted(set(before["entries"]) | set(after["entries"]))
    for kind in kinds:
        b = before["entries"].get(kind, {})
        a = after["entries"].get(kind, {})
        judged = not kind.endswith("-info")
        for rel in sorted(set(b) | set(a)):
            if rel in b and rel in a:
                if b[rel] != a[rel]:
                    (changed if judged else info).append({"kind": kind, "file": rel, "before": b[rel], "after": a[rel], "what": "changed"})
            elif rel in b:
                (removed if judged else info).append({"kind": kind, "file": rel, "what": "removed"})
            else:
                (added if judged else info).append({"kind": kind, "file": rel, "what": "added"})
    hb = next((r.get("octreeHash") for r in before["roots"] if r["kind"] == "subnautica"), None)
    ha = next((r.get("octreeHash") for r in after["roots"] if r["kind"] == "subnautica"), None)
    missing = [r["kind"] for r in before["roots"] + after["roots"] if not r.get("exists")]
    res = {"ok": not changed and not added and not removed and hb == ha and not missing,
           "counts": {"changed": len(changed), "added": len(added), "removed": len(removed), "info": len(info),
                      "filesBefore": sum(len(v) for v in before["entries"].values()), "filesAfter": sum(len(v) for v in after["entries"].values())},
           "changed": changed[:MAX_LISTED], "added": added[:MAX_LISTED], "removed": removed[:MAX_LISTED], "info": info[:MAX_LISTED],
           "roots": {r["kind"]: {"before": r, "after": next((x for x in after["roots"] if x["kind"] == r["kind"]), None)} for r in before["roots"]},
           "octreeHashBefore": hb, "octreeHashAfter": ha, "rootsMissing": sorted(set(missing))}
    return res

def list_to_file(out, subnautica=None, se=None):
    L = listing(roots_of(subnautica, se))
    write_listing(out, L)
    return L

def selftest() -> int:
    ok = True
    tmp = tempfile.mkdtemp(prefix="s8a-install-")
    sn = os.path.join(tmp, "Subnautica")
    se = os.path.join(tmp, "SpaceEngineers")
    oct_dir = os.path.join(sn, *OCTREES_REL.split("/"))
    os.makedirs(oct_dir)
    os.makedirs(os.path.join(sn, "SNAppData", "SavedGames", "slot0000"))
    os.makedirs(os.path.join(se, "Bin64"))
    os.makedirs(os.path.join(se, "Content", "Data"))
    for rel in ("BepInEx/LogOutput.log", "BepInEx/cache/harmony_interop_cache.dat", "BepInEx/config/BepInEx.cfg", "BepInEx/plugins/DeepEngineers/DeepEngineers.SN.dll", "BepInEx/core/BepInEx.dll"):
        p = os.path.join(sn, *rel.split("/"))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(b"x")
    for name, data in (("compiled-batch-12-18-12.optoctrees", b"\x04\x00\x00\x00" + b"a" * 100), ("compiled-batch-7-17-10.optoctrees", b"\x04\x00\x00\x00" + b"b" * 50)):
        with open(os.path.join(oct_dir, name), "wb") as f:
            f.write(data)
    with open(os.path.join(sn, "Subnautica.exe"), "wb") as f:
        f.write(b"exe")
    with open(os.path.join(sn, "SNAppData", "SavedGames", "slot0000", "gameinfo.json"), "w") as f:
        f.write("{}")
    with open(os.path.join(se, "Bin64", "SpaceEngineers.exe"), "wb") as f:
        f.write(b"se")
    with open(os.path.join(se, "Content", "Data", "x.sbc"), "wb") as f:
        f.write(b"<x/>")
    roots = roots_of(sn, se)
    p1 = os.path.join(tmp, "before.txt")
    L1 = listing(roots)
    write_listing(p1, L1)
    R1 = read_listing(p1)
    good = R1["entries"] == L1["entries"] and [r["octreeHash"] for r in R1["roots"]][0] == L1["roots"][0]["octreeHash"] and R1["format"] == FORMAT
    print("  listing round trip (text file): %s" % ("PASS" if good else "FAIL"))
    ok &= good
    good = "SNAppData/SavedGames/slot0000/gameinfo.json" not in L1["entries"]["subnautica"] and "Subnautica.exe" in L1["entries"]["subnautica"]
    print("  SNAppData excluded, the install listed: %s" % ("PASS" if good else "FAIL"))
    ok &= good
    c = compare(R1, listing(roots))
    good = c["ok"] and c["counts"]["changed"] == 0
    print("  unchanged installs compare equal: %s" % ("PASS" if good else "FAIL"))
    ok &= good
    # a rewritten batch file (same size, new mtime), a new file in Bin64, a removed Content file
    bp = os.path.join(oct_dir, "compiled-batch-12-18-12.optoctrees")
    st = os.stat(bp)
    os.utime(bp, ns=(st.st_atime_ns, st.st_mtime_ns + 5_000_000_000))
    with open(os.path.join(se, "Bin64", "DE_new.dll"), "wb") as f:
        f.write(b"new")
    os.remove(os.path.join(se, "Content", "Data", "x.sbc"))
    c = compare(R1, listing(roots))
    good = (not c["ok"] and c["counts"] == dict(c["counts"], changed=1, added=1, removed=1) and c["changed"][0]["file"].endswith("12-18-12.optoctrees")
            and c["added"][0]["kind"] == "se-bin64" and c["removed"][0]["kind"] == "se-content" and c["octreeHashBefore"] == c["octreeHashAfter"])
    print("  a touched batch, an added DLL and a removed data file are reported: %s" % ("PASS" if good else "FAIL"))
    ok &= good
    with open(bp, "ab") as f:
        f.write(b"!")
    c2 = compare(R1, listing(roots))
    good = c2["octreeHashBefore"] != c2["octreeHashAfter"] and c2["counts"]["changed"] == 1
    print("  a grown batch file changes the octree listing hash: %s" % ("PASS" if good else "FAIL"))
    ok &= good
    missing = compare(R1, listing([("subnautica", os.path.join(tmp, "nope"), ()), roots[1], roots[2]]))
    good = not missing["ok"] and "subnautica" in missing["rootsMissing"]
    print("  a missing root never passes: %s" % ("PASS" if good else "FAIL"))
    ok &= good
    #  BepInEx's own log, cache and config change at every launch: INFO, never a FAIL; plugins and core stay watched
    R2 = listing(roots)
    for rel in ("BepInEx/LogOutput.log", "BepInEx/cache/harmony_interop_cache.dat", "BepInEx/config/BepInEx.cfg"):
        with open(os.path.join(sn, *rel.split("/")), "ab") as f:
            f.write(b"!")
    with open(os.path.join(sn, "BepInEx", "LogOutput.log.1"), "wb") as f:
        f.write(b"rotated")
    c3 = compare(R2, listing(roots))
    good = c3["ok"] and c3["counts"]["info"] == 4 and c3["counts"]["changed"] == 0 and c3["counts"]["added"] == 0 and \
        "BepInEx/plugins/DeepEngineers/DeepEngineers.SN.dll" in R2["entries"]["subnautica"] and "BepInEx/LogOutput.log" in R2["entries"]["subnautica-info"]
    with open(os.path.join(sn, "BepInEx", "plugins", "DeepEngineers", "DeepEngineers.SN.dll"), "ab") as f:
        f.write(b"!")
    c4 = compare(R2, listing(roots))
    good &= not c4["ok"] and c4["counts"]["changed"] == 1 and c4["changed"][0]["file"].endswith("DeepEngineers.SN.dll")
    print("  BepInEx log/cache/config are INFO (%d), a plugin change still fails: %s" % (c3["counts"]["info"], "PASS" if good else "FAIL"))
    ok &= good
    print("s8a_install_check selftest %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd")
    p = sub.add_parser("list", help="write the listing of both installs (read-only)")
    p.add_argument("--out", required=True)
    p.add_argument("--subnautica", default=None)
    p.add_argument("--se", default=None)
    p = sub.add_parser("compare", help="compare two listings")
    p.add_argument("before")
    p.add_argument("after")
    p.add_argument("--json", default=None)
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.cmd == "list":
        L = list_to_file(a.out, a.subnautica, a.se)
        for r in L["roots"]:
            print("  %-11s %s: %s%d files, %d bytes%s" % (r["kind"], r["path"], "" if r["exists"] else "MISSING ", r["files"], r["bytes"],
                                                        " octreeHash=%s" % r.get("octreeHash") if r.get("octreeHash") else ""))
        print("listing written: %s" % a.out)
        return 0
    if a.cmd == "compare":
        c = compare(read_listing(a.before), read_listing(a.after))
        if a.json:
            with open(a.json, "w", encoding="utf-8") as f:
                json.dump(c, f, indent=1)
        print("S8a install check: %s (changed %d, added %d, removed %d, info %d; octree hash %s -> %s)" % (
            "PASS" if c["ok"] else "FAIL", c["counts"]["changed"], c["counts"]["added"], c["counts"]["removed"], c["counts"]["info"], c["octreeHashBefore"], c["octreeHashAfter"]))
        for k in ("changed", "added", "removed", "info"):
            for e in c[k]:
                print("  %s %s %s%s" % (k, e["kind"], e["file"], " (%s)" % e.get("what") if k == "info" else ""))
        return 0 if c["ok"] else 1
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
