"""DeepEngineers updater. Python 3.10 stdlib only; no game is touched.

Start asks GitHub for the latest release of the DeepEngineers repository, compares it with the installed release.json
version, and, on the player's yes (or --auto), downloads the release zip, checks every file against the release's own
SHA256SUMS.txt, unpacks it over the release folder (plugins, worldmod, world-template, launcher, docs, release.json) and
re-runs the installer (a repair: plugins, world mod, the world copies, the manifest). The running launcher then exits with
code 3 so Play.cmd starts the new launcher.

 python -I launcher.py update --check # prints the installed and latest versions; exit 0 up to date, 10 newer
 python -I launcher.py update [--yes|--auto] # asks (or not) and applies; exit 3 = updated, run again

is reported as "update check unavailable" and never stops a launch; a token in %LOCALAPPDATA%\DeepEngineers\github-token.txt
(one line) is sent when present. DEEPENGINEERS_UPDATE=off skips everything. No download is applied unless the zip's
SHA-256 equals the one in the release's SHA256SUMS.txt and the zip's release.json names a newer version.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile

REPO_OWNER, REPO_NAME = "bslingerr", "DeepEngineers"
API_LATEST = "https://api.github.com/repos/%s/%s/releases/latest" % (REPO_OWNER, REPO_NAME)
USER_AGENT = "DeepEngineers-updater"
ZIP_RE = re.compile(r"^DeepEngineers-(?P<version>[0-9][^/\\]*?)\.zip$", re.I)
SUMS_NAME = "SHA256SUMS.txt"
TIMEOUT_S = 15
EXIT_UPDATED = 3
EXIT_NEWER = 10
# the folders and files the zip replaces in the release folder (what make_release.py writes); nothing else is touched
REPLACED_DIRS = ("plugins", "worldmod", "world-template", "launcher")
REPLACED_FILES = ("Install.cmd", "install.ps1", "Play.cmd", "play.ps1", "Uninstall.cmd", "README.md", "NOTICE.md",
                  "UNINSTALL.md", "release.json", SUMS_NAME)

def version_tuple(v: str) -> tuple:
    """'1.2.3' -> (1, 2, 3); a leading 'v' and a suffix after '-' are dropped; unknown ->."""
    if not v:
        return ()
    v = v.strip().lstrip("vV").split("-", 1)[0]
    out = []
    for part in v.split("."):
        if not part.isdigit():
            break
        out.append(int(part))
    return tuple(out)

def newer(latest: str, installed: str) -> bool:
    a, b = version_tuple(latest), version_tuple(installed)
    return bool(a) and (not b or a > b)

def read_token(local: str) -> str | None:
    p = os.path.join(local, "github-token.txt")
    try:
        with open(p, encoding="utf-8") as f:
            t = f.read().strip()
        return t or None
    except OSError:
        return None

def installed_version(release_root: str) -> str | None:
    try:
        with open(os.path.join(release_root, "release.json"), encoding="utf-8") as f:
            return (json.load(f).get("version") or "").strip() or None
    except (OSError, ValueError):
        return None

def fetch(url: str, token: str | None, dst: str | None = None, timeout: float = TIMEOUT_S):
    """GET url; JSON (dst None) or the bytes written to dst. Raises urllib.error.HTTPError / URLError / OSError."""
    headers = {"User-Agent": USER_AGENT, "Accept": "application/vnd.github+json" if dst is None else "application/octet-stream"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        if dst is None:
            return json.loads(r.read().decode("utf-8"))
        with open(dst, "wb") as f:
            shutil.copyfileobj(r, f)
        return dst

def latest_release(token: str | None, opener=fetch) -> dict:
    """{'status': 'ok'|'unavailable'|'error', 'version', 'zipUrl', 'zipName', 'sumsUrl', 'detail'}."""
    try:
        doc = opener(API_LATEST, token)
    except urllib.error.HTTPError as e:
        why = "the repository is private or has no release yet" if e.code in (403, 404) else "HTTP %d" % e.code
        return {"status": "unavailable", "detail": "%s (%s)" % (why, API_LATEST)}
    except (urllib.error.URLError, OSError, ValueError) as e:
        return {"status": "unavailable", "detail": "no answer from GitHub (%s)" % e}
    tag = str(doc.get("tag_name") or doc.get("name") or "").strip()
    zip_url = zip_name = sums_url = None
    for asset in doc.get("assets") or []:
        name = str(asset.get("name") or "")
        if ZIP_RE.match(name) and not zip_url:
            zip_url, zip_name = asset.get("browser_download_url") or asset.get("url"), name
        elif name == SUMS_NAME:
            sums_url = asset.get("browser_download_url") or asset.get("url")
    if not tag:
        return {"status": "error", "detail": "the latest release has no tag"}
    if not zip_url or not sums_url:
        return {"status": "error", "version": tag, "detail": "the release %s lacks the zip or %s" % (tag, SUMS_NAME)}
    m = ZIP_RE.match(zip_name)
    return {"status": "ok", "version": (m.group("version") if m else tag), "tag": tag, "zipUrl": zip_url, "zipName": zip_name,
            "sumsUrl": sums_url, "detail": ""}

def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def parse_sums(text: str) -> dict:
    """sha256sum format: '<hex> <name>' per line -> {name: hex}; a leading '*' on the name is dropped."""
    out = {}
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split(None, 1)
        if len(parts) != 2 or len(parts[0]) != 64:
            continue
        out[parts[1].strip().lstrip("*")] = parts[0].lower()
    return out

def verify_zip(zip_path: str, sums_text: str, zip_name: str) -> tuple:
    """(ok, detail): the zip's SHA-256 equals the SHA256SUMS entry for its name (or the only entry when one)."""
    sums = parse_sums(sums_text)
    want = sums.get(zip_name) or sums.get(os.path.basename(zip_path))
    if want is None and len(sums) == 1:
        want = next(iter(sums.values()))
    if want is None:
        return False, "%s has no entry for %s" % (SUMS_NAME, zip_name)
    got = sha256_file(zip_path)
    if got != want:
        return False, "SHA-256 mismatch: %s expected %s, got %s (the download is discarded)" % (zip_name, want[:16], got[:16])
    return True, "SHA-256 verified (%s)" % got[:16]

def zip_release_version(zip_path: str) -> tuple:
    """(top folder inside the zip, the version its release.json names); ('', None) when absent."""
    with zipfile.ZipFile(zip_path) as z:
        names = z.namelist()
        tops = {n.split("/", 1)[0] for n in names if "/" in n}
        top = next(iter(tops)) if len(tops) == 1 else ""
        rj = (top + "/" if top else "") + "release.json"
        if rj not in names:
            return top, None
        try:
            return top, (json.loads(z.read(rj).decode("utf-8")).get("version") or "").strip() or None
        except ValueError:
            return top, None

def apply_zip(zip_path: str, release_root: str, log=print) -> str:
    """Unpacks the release zip over release_root: the replaced folders are swapped whole (old -> *.old, removed after),
 the replaced files overwritten; other files in the folder are left. Returns the new version."""
    top, version = zip_release_version(zip_path)
    if not version:
        raise ValueError("the zip has no release.json version")
    staging = tempfile.mkdtemp(prefix="de-update-", dir=os.path.dirname(os.path.abspath(zip_path)))
    try:
        with zipfile.ZipFile(zip_path) as z:
            for info in z.infolist():
                name = info.filename
                if name.startswith("/") or ".." in name.replace("\\", "/").split("/"):
                    raise ValueError("unsafe path in the zip: %r" % name)
            z.extractall(staging)
        src_root = os.path.join(staging, top) if top else staging
        for d in REPLACED_DIRS:
            s, t = os.path.join(src_root, d), os.path.join(release_root, d)
            if not os.path.isdir(s):
                continue
            old = t + ".old"
            if os.path.isdir(old):
                shutil.rmtree(old)
            if os.path.isdir(t):
                os.rename(t, old)
            shutil.copytree(s, t)
            if os.path.isdir(old):
                shutil.rmtree(old, ignore_errors=True)
            log("update   replaced %s/" % d)
        for n in REPLACED_FILES:
            s = os.path.join(src_root, n)
            if os.path.isfile(s):
                shutil.copy2(s, os.path.join(release_root, n))
        log("update   replaced the root files, release.json now %s" % version)
    finally:
        shutil.rmtree(staging, ignore_errors=True)
    return version

def repair_install(release_root: str, python_exe: str, log=print) -> int:
    """de_install.py install (the repair path) with the new launcher; returns its exit code."""
    installer = os.path.join(release_root, "launcher", "de_install.py")
    cmd = [python_exe, "-I", "-B", installer, "--release-root", release_root, "install", "--no-preflight"]
    log("update   repairing the install: " + " ".join(cmd[3:]))
    return subprocess.call(cmd)

def run_update(release_root: str, local: str, check_only=False, yes=False, auto=False, ask=input, log=print,
               opener=fetch, python_exe=None) -> int:
    """The verb. Exit codes: 0 nothing to do or unavailable, EXIT_NEWER (check only, newer available),
 EXIT_UPDATED (applied: start the launcher again), 1 a failure that leaves the install as it was."""
    if (os.environ.get("DEEPENGINEERS_UPDATE") or "").strip().lower() == "off":
        log("update   off (DEEPENGINEERS_UPDATE=off)")
        return 0
    installed = installed_version(release_root) or "?"
    token = read_token(local)
    info = latest_release(token, opener)
    if info["status"] != "ok":
        log("update   check unavailable: %s; DeepEngineers %s stays as it is" % (info["detail"], installed))
        return 0
    latest = info["version"]
    if not newer(latest, installed):
        log("update   DeepEngineers %s is the latest release" % installed)
        return 0
    log("update   DeepEngineers %s is available (installed %s): %s" % (latest, installed, info["zipName"]))
    if check_only:
        return EXIT_NEWER
    if not (yes or auto):
        try:
            answer = ask("Download and install it now? [Y/n] ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            answer = "n"
        if answer not in ("", "y", "yes", "o", "oui"):
            log("update   skipped; Play continues with %s" % installed)
            return 0
    downloads = os.path.join(local, "updates")
    os.makedirs(downloads, exist_ok=True)
    zip_path = os.path.join(downloads, info["zipName"])
    sums_path = os.path.join(downloads, SUMS_NAME)
    try:
        log("update   downloading %s" % info["zipUrl"])
        opener(info["zipUrl"], token, zip_path)
        opener(info["sumsUrl"], token, sums_path)
    except (urllib.error.URLError, OSError) as e:
        log("update   download failed (%s): nothing changed" % e)
        return 1
    with open(sums_path, encoding="utf-8", errors="replace") as f:
        ok, detail = verify_zip(zip_path, f.read(), info["zipName"])
    if not ok:
        log("update   " + detail)
        try:
            os.remove(zip_path)
        except OSError:
            pass
        return 1
    log("update   " + detail)
    top, zip_version = zip_release_version(zip_path)
    if not zip_version or not newer(zip_version, installed):
        log("update   the zip's release.json says %s, not newer than %s: nothing changed" % (zip_version, installed))
        return 1
    try:
        version = apply_zip(zip_path, release_root, log)
    except (OSError, ValueError, zipfile.BadZipFile) as e:
        log("update   apply failed (%s): run Install.cmd from the new zip by hand" % e)
        return 1
    code = repair_install(release_root, python_exe or sys.executable, log)
    if code != 0:
        log("update   the repair reported exit %d: run Install.cmd once, then Play" % code)
        return 1
    log("update   DeepEngineers %s installed: starting again" % version)
    return EXIT_UPDATED

def add_parser(sub):
    p = sub.add_parser("update", help="check GitHub for a newer DeepEngineers release and install it (release mode)")
    p.add_argument("--check", action="store_true", help="only report; exit 10 when a newer release exists")
    p.add_argument("--yes", action="store_true", help="install without asking")
    p.add_argument("--auto", action="store_true", help="the same as --yes (Play.cmd --auto-update)")
    return p
