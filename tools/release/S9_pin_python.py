"""S9 one-click: fills the SHA-256 pin of the embeddable interpreter (de_pins.PYTHON_EMBED) from a copy of
the zip downloaded from python.org. python.org publishes only the size and MD5 of that file; this script refuses a zip
whose size or MD5 differ from those published values, then prints (or with --write stores) its SHA-256.

 python -I tools/release/S9_pin_python.py <python-3.10.11-embed-amd64.zip> [--write]

Downloads nothing. --write changes only the `"sha256": ""` line of PYTHON_EMBED in src/DeepEngineers.Launcher/de_pins.py.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PINS = os.path.normpath(os.path.join(HERE, "..", "..", "src", "DeepEngineers.Launcher", "de_pins.py"))
sys.path.insert(0, os.path.dirname(PINS))
import de_pins as P  # noqa: E402

def digests(path: str):
    md5, sha = hashlib.md5(), hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            md5.update(b)
            sha.update(b)
    return md5.hexdigest(), sha.hexdigest()

def check(path: str, spec=None) -> tuple:
    """(sha256, None) or (None, why)."""
    spec = spec or P.PYTHON_EMBED
    if not os.path.isfile(path):
        return None, "no file %s" % path
    if os.path.getsize(path) != spec["size"]:
        return None, "size %d, python.org publishes %d" % (os.path.getsize(path), spec["size"])
    md5, sha = digests(path)
    if md5 != spec["md5"]:
        return None, "MD5 %s, python.org publishes %s" % (md5, spec["md5"])
    try:
        with zipfile.ZipFile(path) as z:
            names = {n.lower() for n in z.namelist()}
    except zipfile.BadZipFile as e:
        return None, "not a zip (%s)" % e
    tag = "python%s%s" % tuple(spec["version"].split(".")[:2])
    for need in ("python.exe", tag + ".dll", tag + ".zip", tag + "._pth"):
        if need not in names:
            return None, "%s missing in the zip" % need
    return sha, None

def write_pin(sha: str, pins_path: str = PINS) -> bool:
    with open(pins_path, encoding="utf-8") as f:
        text = f.read()
    block = re.search(r"PYTHON_EMBED = \{.*?\n\}", text, re.S)
    if not block:
        return False
    new_block, n = re.subn(r'"sha256": "[0-9a-f]*"', '"sha256": "%s"' % sha, block.group(0), count=1)
    if n != 1:
        return False
    with open(pins_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text[:block.start()] + new_block + text[block.end():])
    return True

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="pin the embeddable Python's SHA-256 (S9)")
    ap.add_argument("zip")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args(argv)
    sha, why = check(a.zip)
    if sha is None:
        print("FAIL %s: %s (download it again from %s)" % (a.zip, why, P.PYTHON_EMBED["url"]))
        return 1
    print("PASS %s: size and MD5 equal python.org's; sha256 %s" % (P.PYTHON_EMBED["file"], sha))
    if a.write:
        if not write_pin(sha):
            print("FAIL could not find the PYTHON_EMBED sha256 line in %s" % PINS)
            return 1
        print("PASS written into %s" % PINS)
    return 0

if __name__ == "__main__":
    sys.exit(main())
