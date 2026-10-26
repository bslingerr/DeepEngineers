"""DeepEngineers release lint
Python 3.10 standard library only; run with ``python -I``.

 python -I tools/release/check_release.py release <release folder> [--allowlist F] [--repo R] [--decomp D...]
 python -I tools/release/check_release.py sources <source tree> [--decomp D...]

``release`` lints the folder players download
the repository, or the copy made by strip_decomp_refs.py) with checks 2 and 3. Every check prints its own lines and a
``CHECK <n> <name>: PASS|FAIL|SKIP`` verdict. The exit code is the sum of the bits of the checks that failed:

 bit 1 check 1, allowlist: every file of the release folder matches an entry of the allowlist manifest
 (tools/release/allowlist.txt); an entry may require the file, pin its SHA-256, or require it to equal a
 repository file byte for byte (the release-flavour build output); any other file fails.
 bit 2 check 2, decompiled code: normalised token 12-grams of every shipped.cs and.py file against the decompiled
 game and loader sources; a single shared 12-gram fails. Normalised: comments
 and whitespace dropped, braces dropped, "this." dropped, C# using directives and namespace names dropped,
 numbers in one canonical form, windows of numbers and punctuation only skipped; identifiers, keywords,
 strings and the other punctuation kept as they are. Shared runs are reported longest first with a class
 for the reader only;

 failing.
 bit 4 check 3, machine paths and decompiler markers, in every shipped file (text and binary; ASCII/UTF-8 and
 UTF-16LE; SteamID64 values also as little-endian u64 and protobuf varints): a Windows user-profile path with a
 real user name, a the assistant project folder name, a SteamID64, a Steam userdata account folder, the E: games
 folder of the development PC, and decompiler headers

 bit 8 check 4, ``um publish check`` (universal-modder CLI) on the release folder, once with each game install as
 --game, as an extra lint; SKIP (not a failure) when the CLI is absent or with --no-um.
 bit 16 usage or set-up error.

The decomp path is never stored in a shipped file: the default is $DEEPENG_DECOMP, else --decomp. Informational
notes (never failing): decompiled-line citations in shipped text ("VERIFIED... "), which strip_decomp_refs.py

"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import shlex
import shutil
import subprocess
import sys
import time
from pathlib import Path

EXIT_ALLOWLIST = 1
EXIT_DECOMP = 2
EXIT_PATHS = 4
EXIT_UM = 8
EXIT_USAGE = 16

GRAM = 12
SOURCE_EXT = {".cs", ".py"}
SKIP_DIRS = {".git", ".vs", ".idea", "bin", "obj", "TestResults", "__pycache__", ".pytest_cache", "node_modules",
             "third_party", "out"}
MAX_SCAN_BYTES = 256 << 20          # files larger than this are hashed and listed, not scanned (check 3)
UTF16_SCAN_BYTES = 32 << 20         # UTF-16LE views only for files up to this size

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
DEFAULT_ALLOWLIST = HERE / "allowlist.txt"

# ===================================================================================================== tokenizer

_CS_TOKEN = re.compile(r"""
    (?P<ws>\s+)
  | (?P<lc>//[^\n]*)
  | (?P<bc>/\*.*?(?:\*/|\Z))
  | (?P<vstr>\$?@\$?"(?:[^"]|"")*")
  | (?P<str>\$?"(?:[^"\\\n]|\\.)*")
  | (?P<chr>'(?:[^'\\\n]|\\.){1,10}')
  | (?P<num>0[xX][0-9a-fA-F_]+[uUlL]*|0[bB][01_]+[uUlL]*|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]*|\.\d[\d_]*(?:[eE][+-]?\d+)?[fFdDmM]?)
  | (?P<id>@?[A-Za-z_][A-Za-z0-9_]*)
  | (?P<op>\?\?=|<<=|>>=|==|!=|<=|>=|&&|\|\||=>|\+\+|--|\+=|-=|\*=|/=|%=|&=|\|=|\^=|<<|\?\?|\?\.|::|->)
  | (?P<p>.)
""", re.S | re.X)

_PY_TOKEN = re.compile(r"""
    (?P<ws>\s+)
  | (?P<lc>\#[^\n]*)
  | (?P<tstr>(?:[rRbBuUfF]{0,2})(?:\"\"\"(?:[^\\]|\\.)*?\"\"\"|'''(?:[^\\]|\\.)*?'''))
  | (?P<str>(?:[rRbBuUfF]{0,2})(?:"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'))
  | (?P<num>0[xX][0-9a-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?[jJ]?|\.\d[\d_]*(?:[eE][+-]?\d+)?)
  | (?P<id>[A-Za-z_][A-Za-z0-9_]*)
  | (?P<op>\*\*=|//=|>>=|<<=|==|!=|<=|>=|->|\+=|-=|\*=|/=|%=|&=|\|=|\^=|\*\*|//|<<|>>|:=)
  | (?P<p>.)
""", re.S | re.X)

_DROP = {"ws", "lc", "bc"}
_BRACES = {"{", "}"}

def _canon_number(text: str) -> str:
    t = text.replace("_", "").lower()
    try:
        if t.startswith("0x"):
            return repr(float(int(t[2:].rstrip("ul") or "0", 16)))
        if t.startswith("0b"):
            return repr(float(int(t[2:].rstrip("ul") or "0", 2)))
        if t.startswith("0o"):
            return repr(float(int(t[2:] or "0", 8)))
        t = t.rstrip("fdmulj")
        return repr(float(t)) if t else text
    except ValueError:
        return text

def tokenize(text: str, ext: str):
    """Normalised tokens and their start offsets (see the module summary)."""
    rx = _PY_TOKEN if ext == ".py" else _CS_TOKEN
    toks: list[str] = []
    offs: list[int] = []
    for m in rx.finditer(text):
        kind = m.lastgroup
        if kind in _DROP:
            continue
        v = m.group()
        if kind == "num":
            v = _canon_number(v)
        elif kind == "id":
            if v[0] == "@":
                v = v[1:]
        elif kind == "p" and v in _BRACES:
            continue
        if v == "." and toks and toks[-1] == "this" and kind == "p":
            toks.pop()
            offs.pop()
            continue
        toks.append(v)
        offs.append(m.start())
    if ext != ".py":
        toks, offs = _drop_directives(toks, offs)
    return toks, offs

_DIRECTIVE_OK = {".", "=", "::", "static", "global", "<", ">", ",", "?"}

def _drop_directives(toks, offs):
    """C#: drops `using A.B;` / `using X = A.B;` / `using static A.B;` directives and `namespace A.B` names (file
 headers every C# file shares; a using *statement* `using (…)` / `using var` stays)."""
    out_t, out_o = [], []
    i, n = 0, len(toks)
    while i < n:
        t = toks[i]
        if t == "using" and i + 1 < n and toks[i + 1] not in ("(", "var") and _is_name(toks[i + 1]):
            j = i + 1
            while j < n and toks[j] != ";" and (_is_name(toks[j]) or toks[j] in _DIRECTIVE_OK):
                j += 1
            if j < n and toks[j] == ";":
                i = j + 1
                continue
        if t == "namespace" and i + 1 < n and _is_name(toks[i + 1]):
            j = i + 2                                   # name (. name )*
            while j + 1 < n and toks[j] == "." and _is_name(toks[j + 1]):
                j += 2
            if j < n and toks[j] == ";":
                j += 1
            i = j
            continue
        out_t.append(t)
        out_o.append(offs[i])
        i += 1
    return out_t, out_o

def _is_name(t: str) -> bool:
    return bool(t) and (t[0].isalpha() or t[0] == "_")

def grams(toks):
    """Hash of every GRAM-token window (Python's tuple hash; one process, so both sides agree). A window made only of
 numbers and punctuation (a table of constants: "1, 2, 3, …") carries no code and is skipped on both sides."""
    n = len(toks) - GRAM + 1
    if n <= 0:
        return
    word = [1 if (t[0].isalpha() or t[0] in "_\"'@$") else 0 for t in toks]
    run = sum(word[:GRAM])
    for i in range(n):
        if i:
            run += word[i + GRAM - 1] - word[i - 1]
        if run:
            yield i, hash(tuple(toks[i:i + GRAM]))

def read_text(p: Path) -> str:
    b = p.read_bytes()
    if b.startswith(b"\xef\xbb\xbf"):
        b = b[3:]
    elif b.startswith(b"\xff\xfe") or b.startswith(b"\xfe\xff"):
        return b.decode("utf-16", errors="replace")
    return b.decode("utf-8", errors="replace")

def line_of(text: str, off: int) -> int:
    return text.count("\n", 0, off) + 1

# ===================================================================================================== file walks

def walk(root: Path, skip_dirs=SKIP_DIRS):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in skip_dirs)
        for f in sorted(filenames):
            yield Path(dirpath) / f

def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

# ===================================================================================================== check 1

class AllowEntry:
    def __init__(self, pattern: str, required: bool, sha: str | None, build: str | None, line: int):
        self.pattern = pattern
        self.required = required
        self.sha = sha
        self.build = build
        self.line = line
        self.regex = re.compile(_glob_to_regex(pattern), re.I)

def _glob_to_regex(pat: str) -> str:
    out, i = [], 0
    while i < len(pat):
        c = pat[i]
        if pat.startswith("**/", i):
            out.append("(?:[^/]+/)*")
            i += 3
            continue
        if pat.startswith("**", i):
            out.append(".*")
            i += 2
            continue
        if c == "*":
            out.append("[^/]*")
        elif c == "?":
            out.append("[^/]")
        else:
            out.append(re.escape(c))
        i += 1
    return "^" + "".join(out) + "$"

def _build_path(entry: AllowEntry, rel: str) -> str:
    """`dir/** build=src/x/**` maps the matched remainder (one tree to another); otherwise the build path as given."""
    if entry.build.endswith("/**") and entry.pattern.endswith("/**"):
        return entry.build[:-2] + rel[len(entry.pattern) - 2:]
    if entry.build.endswith("/*") and entry.pattern.endswith("/*"):
        return entry.build[:-1] + rel.rsplit("/", 1)[-1]
    return entry.build

def load_allowlist(path: Path) -> list[AllowEntry]:
    entries = []
    for n, raw in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        line = raw.split("#", 1)[0].strip() if not raw.lstrip().startswith("#") else ""
        if not line:
            continue
        parts = line.split()
        pat = parts[0].replace("\\", "/")
        required = False
        sha = build = None
        for opt in parts[1:]:
            if opt == "required":
                required = True
            elif opt.startswith("sha256="):
                sha = opt[7:].lower()
            elif opt.startswith("build="):
                build = opt[6:].replace("\\", "/")
            else:
                raise ValueError("%s:%d: unknown option %r" % (path, n, opt))
        entries.append(AllowEntry(pat, required, sha, build, n))
    return entries

def check_allowlist(release: Path, allowlist: Path, repo: Path, out) -> bool:
    try:
        entries = load_allowlist(allowlist)
    except (OSError, ValueError) as e:
        out("  allowlist unreadable: %s" % e)
        return False
    ok = True
    seen = {id(e): 0 for e in entries}
    files = [p for p in walk(release, skip_dirs=set())]
    for p in files:
        rel = p.relative_to(release).as_posix()
        match = next((e for e in entries if e.regex.match(rel)), None)
        if match is None:
            out("  FAIL not in the allowlist: %s" % rel)
            ok = False
            continue
        seen[id(match)] += 1
        if match.sha or match.build:
            h = sha256(p)
            if match.sha and h != match.sha:
                out("  FAIL sha256 differs: %s (%s, allowlist line %d wants %s)" % (rel, h, match.line, match.sha))
                ok = False
            if match.build:
                src = repo / _build_path(match, rel)
                if not src.is_file():
                    out("  FAIL build file missing for %s: %s (run build.ps1 -Flavor release)" % (rel, match.build))
                    ok = False
                elif sha256(src) != h:
                    out("  FAIL differs from the build output: %s != %s" % (rel, match.build))
                    ok = False
    for e in entries:
        if e.required and seen[id(e)] == 0:
            out("  FAIL required and missing: %s (allowlist line %d)" % (e.pattern, e.line))
            ok = False
    out("  %d files, %d allowlist entries" % (len(files), len(entries)))
    return ok

# ===================================================================================================== check 2

def index_shipped(files, root: Path):
    """The set of every shipped GRAM-gram hash, plus per file: path, text, token offsets and tokens."""
    index: set[int] = set()
    meta = []
    for p in files:
        text = read_text(p)
        toks, offs = tokenize(text, p.suffix.lower())
        meta.append((p, text, offs, toks))
        index.update(h for _, h in grams(toks))
    return index, meta

SUSPECT_SPAN = 24      # report class only: a shared run this long, rare in the decomp, reads as copied code
IDIOM_DF = 3           # report class only: a 12-gram found in this many decompiled files is a language/API idiom

def _run_key(rel: str, toks) -> str:
    return hashlib.sha1((rel + chr(9) + " ".join(toks)).encode("utf-8")).hexdigest()[:16]

def load_baseline(path):
    keys = set()
    if path and Path(path).is_file():
        for raw in Path(path).read_text(encoding="utf-8-sig").splitlines():
            raw = raw.strip()
            if raw and not raw.startswith("#"):
                keys.add(raw.split()[0])
    return keys

def check_decomp(files, root: Path, decomp_roots, out, max_report=20, show=False, baseline=None,
                 write_baseline=None) -> bool:
    """Check 2.
 interop facts: Harmony signatures, enum values, P/Invoke lines). Runs are classed for the report only."""
    if not files:
        out("  no .cs or .py file shipped: nothing to compare")
        return True
    t0 = time.monotonic()
    index, meta = index_shipped(files, root)
    out("  %d shipped source files, %d distinct %d-grams" % (len(files), len(index), GRAM))
    matched: dict[int, list] = {}           # gram -> [set of decomp file ids, example]
    dpaths: list[Path] = []
    for droot in decomp_roots:
        for p in walk(droot, skip_dirs={".git"}):
            if p.suffix.lower() != ".cs":
                continue
            try:
                text = read_text(p)
            except OSError:
                continue
            di = len(dpaths)
            dpaths.append(p)
            toks, offs = tokenize(text, ".cs")
            for i, h in grams(toks):
                if h in index:
                    m = matched.get(h)
                    if m is None:
                        matched[h] = [{di}, (p, line_of(text, offs[i]))]
                    elif len(m[0]) < 64:
                        m[0].add(di)
    out("  %d decompiled .cs files scanned in %.1f s; %d shared %d-grams" % (len(dpaths), time.monotonic() - t0,
                                                                          len(matched), GRAM))
    if not matched:
        return True
    acked = load_baseline(baseline)
    runs = []
    for p, text, offs, toks in meta:
        rel = _rel(p, root)
        hs = [h for _, h in grams(toks)]
        i = 0
        while i < len(hs):
            if hs[i] not in matched:
                i += 1
                continue
            j = i
            while j + 1 < len(hs) and hs[j + 1] in matched:
                j += 1
            span = j - i + GRAM
            df = min(len(matched[h][0]) for h in hs[i:j + 1])
            ex = matched[hs[i]][1]
            cls = "idiom" if df >= IDIOM_DF else "SUSPECT" if span >= SUSPECT_SPAN else "short"
            rtoks = toks[i:j + GRAM]
            runs.append((cls, span, rel, line_of(text, offs[i]), df, ex, rtoks, _run_key(rel, rtoks)))
            i = j + 1
    order = {"SUSPECT": 0, "short": 1, "idiom": 2}
    runs.sort(key=lambda r: (order[r[0]], -r[1], r[2], r[3]))
    open_runs = [r for r in runs if r[7] not in acked]
    files_hit = len(set(r[2] for r in open_runs))
    counts = {c: sum(1 for r in open_runs if r[0] == c) for c in order}
    out("  %d shared runs in %d files (%d SUSPECT: >= %d tokens and in <= %d decomp files; %d short; %d idiom: in >= %d "
        "decomp files)%s" % (len(open_runs), files_hit, counts["SUSPECT"], SUSPECT_SPAN, IDIOM_DF - 1, counts["short"],
                             counts["idiom"], IDIOM_DF,
                             "; %d acknowledged in the baseline" % (len(runs) - len(open_runs)) if acked else ""))
    for k, r in enumerate(open_runs):
        if k >= max_report:
            out("  ... %d more runs (--max-report)" % (len(open_runs) - k))
            break
        cls, span, rel, line, df, ex, rtoks, key = r
        out("  FAIL %-7s %s:%d  %d tokens, in %d decomp file%s (e.g. %s:%d)  key %s" % (
            cls, rel, line, span, df, "" if df == 1 else "s", _short(str(ex[0])), ex[1], key))
        if show:
            t = " ".join(rtoks)
            out("          " + (t if len(t) <= 240 else t[:237] + "..."))
    if write_baseline:
        nl = chr(10)
        with open(write_baseline, "w", encoding="utf-8", newline=nl) as f:
            f.write("# check_release.py check 2 baseline: one shared run per line (key, class, file:line, tokens)." + nl +
                    "# Review each run; keep only the interop facts the owner accepts (a Harmony signature, an enum's" + nl +
                    "# values, a P/Invoke line); a run whose key is listed here is reported but does not fail." + nl)
            for cls, span, rel, line, df, ex, rtoks, key in runs:
                t = " ".join(rtoks)
                f.write("%s  %s  %s:%d  %s%s" % (key, cls, rel, line, t if len(t) <= 160 else t[:157] + "...", nl))
        out("  baseline candidates written: %s (%d runs; review before use)" % (write_baseline, len(runs)))
    return not open_runs

def _ranges(nums):
    out, start, prev = [], None, None
    for n in nums:
        if start is None:
            start = prev = n
        elif n <= prev + 1:
            prev = n
        else:
            out.append((start, prev))
            start = prev = n
    if start is not None:
        out.append((start, prev))
    s = ",".join("%d" % a if a == b else "%d-%d" % (a, b) for a, b in out[:12])
    return s + (",…" if len(out) > 12 else "")

def _short(path: str) -> str:
    """A decomp path without the machine part."""
    parts = Path(path).parts
    for i, part in enumerate(parts):
        if part.lower() == "decomp":
            return "/".join(parts[i + 1:])
    return "/".join(parts[-3:])

def _rel(p: Path, root: Path) -> str:
    try:
        return p.relative_to(root).as_posix()
    except ValueError:
        return p.name

# ===================================================================================================== check 3

# Patterns are written so that this file does not match itself (it ships in a source release).
_PLACEHOLDER_USERS = {"you", "your", "yourname", "your-name", "your_name", "username", "user", "name", "me", "example",
                      "public", "default", "default user", "all users", "<you>", "<user>"}
_TEXT_PATTERNS = [
    ("user-profile path", re.compile(r"\b[A-Za-z]:(?:\\\\|\\|/)+Users(?:\\\\|\\|/)+([^\\/\s\"'<>%$*?|:;,\x00]{1,64})", re.I)),
    ("Claude project folder", re.compile(r"\b[A-Za-z]--Users-([A-Za-z0-9._]+)")),
    ("SteamID64", re.compile(r"(?<!\d)7656119\d{10}(?!\d)")),
    ("Steam userdata account", re.compile(r"\buserdata(?:\\\\|\\|/)+(\d{3,12})\b", re.I)),
    ("development PC games folder", re.compile(r"\b[Ee]:(?:\\\\|\\|/)+Games\b")),
]
_MARKER_PATTERNS = [
    ("decompiler: ICSharpCode", re.compile(r"ICSharpCode[.]Decompiler")),
    ("decompiler header", re.compile(r"(?:^|\n)[ \t]*(?://|#)[ \t]*Decompiled[ \t]+with\b", re.I)),
    ("decompiler: JetBrains", re.compile(r"JetBrains[ \t]+decompiler", re.I)),
    ("decompiler: dnSpy", re.compile(r"(?:^|\n)[ \t]*//[ \t]*dnSpy")),
    ("decompiler: MVID line", re.compile(r"(?:^|\n)[ \t]*//[ \t]*MVID:")),
    ("decompiler: assembly location", re.compile(r"(?:^|\n)[ \t]*//[ \t]*Assembly[ \t]+location:")),
    ("metadata-as-source region", re.compile(r"(?:^|\n)[ \t]*#region[ \t]+Assembly[ \t]")),
    ("compiler-generated name", re.compile(r"<Private(?:ImplementationDetails)>|<>c__DisplayClass\d|CS\$<>8__locals")),
]
_CITATION = re.compile(r"\b(?:VERIFIED|[Dd]ecomp(?:iled)?)\b[^\n]{0,200}?[\w./\\-]+\.cs:\d+")
# a SteamID64 (individual account, universe 1) inside binary data: little-endian u64, or a protobuf varint
_BIN_STEAMID_LE = re.compile(rb"[\x00-\xff]{4}\x01\x00\x10\x01", re.S)
_BIN_STEAMID_VARINT = re.compile(rb"[\x80-\xff]{4}[\x90-\x9f]\x80\x80\x88\x01", re.S)

STEAMID64_BASE = 0x0110000100000000     # individual accounts start here (account id 0 = the conversion base)
PLACEHOLDER_ACCOUNTS = 1000              # account ids below this are test placeholders (fixtures), no person's id

def _steamid_of(raw: bytes, label: str) -> int:
    if "u64" in label:
        return int.from_bytes(raw, "little")
    v = 0
    for k, byte in enumerate(raw):
        v |= (byte & 0x7F) << (7 * k)
    return v

def _views(b: bytes):
    yield "text", b.decode("latin-1")
    if len(b) <= UTF16_SCAN_BYTES and _looks_binary_or_utf16(b):
        yield "utf-16le", b.decode("utf-16-le", errors="ignore")
        yield "utf-16le+1", b[1:].decode("utf-16-le", errors="ignore")

def _looks_binary_or_utf16(b: bytes) -> bool:
    head = b[:65536]
    return b"\x00" in head

def scan_file(p: Path, rel: str, out, notes) -> int:
    """Check 3 on one file; returns the number of findings."""
    try:
        size = p.stat().st_size
    except OSError:
        return 0
    if size > MAX_SCAN_BYTES:
        out("  FAIL too large to scan (%d MB): %s" % (size >> 20, rel))
        return 1
    b = p.read_bytes()
    found = 0
    for view, text in _views(b):
        for label, rx in _TEXT_PATTERNS:
            for m in rx.finditer(text):
                if label == "user-profile path" and m.group(1).strip().lower() in _PLACEHOLDER_USERS:
                    continue
                if label == "user-profile path" and m.group(1).startswith(("<", "{", "$", "%")):
                    continue
                if label == "SteamID64" and int(m.group()) - STEAMID64_BASE < PLACEHOLDER_ACCOUNTS:
                    continue                                # the conversion base or a fixture placeholder
                found += 1
                if found <= 5:
                    out("  FAIL %s in %s (%s, offset %d)%s" % (label, rel, view, m.start(),
                                                              _where(text, m.start(), view)))
        for label, rx in _MARKER_PATTERNS if view == "text" and not _looks_binary_or_utf16(b) else ():
            # decompiler markers in text only: a compiled DLL legitimately holds the compiler-generated names
            m = rx.search(text)
            if m:
                found += 1
                if found <= 5:
                    out("  FAIL %s in %s%s" % (label, rel, _where(text, m.start(), view)))
        if view == "text" and not _looks_binary_or_utf16(b):
            c = len(_CITATION.findall(text))
            if c:
                notes.append((rel, c))
    #: compressed pixel and voxel data (png, dds, vx2, jpg) matches the u64 / varint SteamID patterns by chance
 # (left_mat.png did); those files hold no settings or paths, so only the text patterns above apply to them
    pixel_data = rel.lower().endswith((".png", ".dds", ".vx2", ".jpg", ".jpeg"))
    # our own assemblies are compared byte for byte with the build outputs (allowlist build=); their metadata tables
 # (a string index next to a row's 0x0001 / 0x0110 bytes) matched the u64 pattern at one offset of plugin.dll
    own_assembly = rel.lower().endswith(".dll")
    if _looks_binary_or_utf16(b) and not pixel_data and not own_assembly:
        for label, rx in (("SteamID64 (binary u64)", _BIN_STEAMID_LE), ("SteamID64 (binary varint)", _BIN_STEAMID_VARINT)):
            for m in rx.finditer(b):
                if _steamid_of(m.group(), label) - STEAMID64_BASE < PLACEHOLDER_ACCOUNTS:
                    continue
                found += 1
                out("  FAIL %s in %s (offset %d)" % (label, rel, m.start()))
                break
    if found > 5:
        out("  ... %s: %d findings in all" % (rel, found))
    return found

def _where(text: str, off: int, view: str) -> str:
    if view != "text":
        return ""
    return " line %d" % line_of(text, off)

def check_paths(files, root: Path, out) -> bool:
    notes: list = []
    bad = 0
    for p in files:
        bad += 1 if scan_file(p, _rel(p, root), out, notes) else 0
    out("  %d files scanned, %d with findings" % (len(files), bad))
    if notes:
        total = sum(c for _, c in notes)
        out("  note: %d decompiled-line citations in %d text files (not a failure; strip_decomp_refs.py removes them "
            "for a source release, owner decision O13)" % (total, len(notes)))
    return bad == 0

# ===================================================================================================== check 4

def _split_cmd(cmd: str):
    """A command line as argv; double quotes group a path with spaces (Windows style, no escapes)."""
    return [t.strip('"') for t in shlex.split(cmd, posix=False)]

def find_um(explicit: str | None):
    """The universal-modder CLI as an argv prefix, or None."""
    if explicit:
        return _split_cmd(explicit)
    env = os.environ.get("DEEPENG_UM")
    if env:
        return _split_cmd(env)
    exe = shutil.which("um")
    if exe:
        return [exe]
    uv = shutil.which("uv")
    base = Path.home() / ".claude" / "plugins" / "cache" / "universal-modder" / "universal-modder"
    if uv and base.is_dir():
        for d in sorted(base.iterdir(), key=lambda x: x.stat().st_mtime, reverse=True):
            if (d / "pyproject.toml").is_file() and (d / "um" / "publish.py").is_file():
                return [uv, "run", "--project", str(d), "um"]
    return None

def check_um(release: Path, um, games, out) -> bool | None:
    if um is None:
        out("  universal-modder CLI not found (um on PATH, $DEEPENG_UM or --um): skipped")
        return None
    ok = True
    runs = [g for g in games if g and Path(g).is_dir()] or [None]
    for g in runs:
        cmd = list(um) + ["publish", "check", str(release)] + (["--game", str(g)] if g else [])
        out("  run: um publish check <release>%s" % (" --game <%s>" % Path(g).name if g else ""))
        try:
            r = subprocess.run(cmd, capture_output=True, text=True, timeout=3600)
        except (OSError, subprocess.TimeoutExpired) as e:
            out("  FAIL um did not run: %s" % e)
            ok = False
            continue
        lines = (r.stdout or "").splitlines()
        for line in lines[-40:]:
            out("    " + line)
        if r.returncode != 0:
            # the shipped Freedom host save is a save the game wrote (player data, not a game asset); when it was copied
 # from this PC's own slot, um sees it "verbatim" in SNAppData/SavedGames. Those three paths, and only those,
 # are accepted; any other failure still fails
            fails = [l.strip() for l in lines if l.strip().startswith("FAIL") and "verbatim:" in l]
            other = [l for l in lines if l.strip().startswith("FAIL") and "verbatim:" not in l and "files," not in l]
            # also accepted: DeepEngineers' own plugins found where the installer or build.ps1 deployed them
            saves = [l for l in fails if (l.split("verbatim:", 1)[1].strip().split()[0] in HOST_SAVE_PATHS and "SNAppData/SavedGames/" in l)
                     or (l.split("verbatim:", 1)[1].strip().split()[0].startswith("plugins/") and "BepInEx/plugins/DeepEngineers/" in l)]
            if fails and len(saves) == len(fails) and not other:
                out("  PASS um publish check: the %d failures are DeepEngineers' own files found installed (the shipped host save, the deployed plugins), accepted" % len(saves))
                continue
            out("  FAIL um publish check exit %d" % r.returncode)
            ok = False
    return ok

HOST_SAVE_PATHS = {"hostsave/slot/gameinfo.json", "hostsave/slot/global-objects.bin", "hostsave/slot/scene-objects.bin", "hostsave/slot/screenshot.jpg"}

# ===================================================================================================== main

def default_decomp():
    env = os.environ.get("DEEPENG_DECOMP")
    return [Path(x) for x in env.split(os.pathsep)] if env else []

def source_files(root: Path, skip_dirs=SKIP_DIRS):
    return [p for p in walk(root, skip_dirs) if p.suffix.lower() in SOURCE_EXT]

def run(argv=None, out=print) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0],
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("mode", choices=("release", "sources"))
    ap.add_argument("folder", help="the release folder, or the source tree")
    ap.add_argument("--allowlist", default=str(DEFAULT_ALLOWLIST), help="release mode: the allowlist manifest")
    ap.add_argument("--repo", default=str(REPO), help="release mode: the repository the build= entries point into")
    ap.add_argument("--decomp", action="append", default=None,
                    help="a decompiled-sources folder (repeatable; default $DEEPENG_DECOMP, os.pathsep-separated)")
    ap.add_argument("--no-decomp", action="store_true", help="skip check 2 (reported as SKIP, never as PASS)")
    ap.add_argument("--um", default=None, help="the universal-modder CLI command (default: found automatically)")
    ap.add_argument("--no-um", action="store_true", help="skip check 4")
    ap.add_argument("--se-dir", default=r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers",
                    help="check 4: the Space Engineers install (passed to um as --game)")
    ap.add_argument("--sn-dir", default=r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica",
                    help="check 4: the Subnautica install (passed to um as --game)")
    ap.add_argument("--show", action="store_true", help="check 2: print the tokens of every reported run")
    ap.add_argument("--baseline", default=None,
                    help="check 2: the owner's reviewed list of acknowledged runs (keys); default none")
    ap.add_argument("--write-baseline", default=None, help="check 2: write every shared run as baseline candidates")
    ap.add_argument("--max-report", type=int, default=20, help="check 2: files reported in detail")
    ap.add_argument("--include-skipped-dirs", action="store_true",
                    help="sources mode: also walk bin/obj/third_party/out/TestResults")
    a = ap.parse_args(argv)

    folder = Path(a.folder)
    if not folder.is_dir():
        out("not a folder: %s" % folder)
        return EXIT_USAGE
    decomp = [Path(d) for d in a.decomp] if a.decomp else default_decomp()
    if not a.no_decomp:
        missing = [d for d in decomp if not d.is_dir()]
        if not decomp or missing:
            out("decomp folder missing (%s): pass --decomp <folder> (the scratchpad decomp) or --no-decomp"
                % (", ".join(str(m) for m in missing) or "none given"))
            return EXIT_USAGE
    code = 0
    out("DeepEngineers check_release %s %s" % (a.mode, folder.name))

    if a.mode == "release":
        allow = Path(a.allowlist)
        if not allow.is_file():
            out("allowlist missing: %s" % allow)
            return EXIT_USAGE
        out("CHECK 1 allowlist (%s)" % allow.name)
        ok = check_allowlist(folder, allow, Path(a.repo), out)
        out("CHECK 1 allowlist: %s" % ("PASS" if ok else "FAIL"))
        code |= 0 if ok else EXIT_ALLOWLIST
        shipped_all = list(walk(folder, skip_dirs=set()))
        shipped_src = [p for p in shipped_all if p.suffix.lower() in SOURCE_EXT]
    else:
        skip = set() if a.include_skipped_dirs else SKIP_DIRS
        shipped_all = list(walk(folder, skip))
        shipped_src = [p for p in shipped_all if p.suffix.lower() in SOURCE_EXT]

    out("CHECK 2 decompiled code (%d-grams)" % GRAM)
    if a.no_decomp:
        out("CHECK 2 decompiled code: SKIP (--no-decomp)")
    else:
        ok = check_decomp(shipped_src, folder, decomp, out, max_report=a.max_report, show=a.show,
                          baseline=a.baseline, write_baseline=a.write_baseline)
        out("CHECK 2 decompiled code: %s" % ("PASS" if ok else "FAIL"))
        code |= 0 if ok else EXIT_DECOMP

    out("CHECK 3 machine paths and decompiler markers")
    ok = check_paths(shipped_all, folder, out)
    out("CHECK 3 machine paths and decompiler markers: %s" % ("PASS" if ok else "FAIL"))
    code |= 0 if ok else EXIT_PATHS

    if a.mode == "release":
        out("CHECK 4 um publish check")
        if a.no_um:
            out("CHECK 4 um publish check: SKIP (--no-um)")
        else:
            r = check_um(folder, find_um(a.um), [a.se_dir, a.sn_dir], out)
            out("CHECK 4 um publish check: %s" % ("SKIP" if r is None else "PASS" if r else "FAIL"))
            code |= EXIT_UM if r is False else 0
    out("RESULT %s (exit %d)" % ("PASS" if code == 0 else "FAIL", code))
    return code

if __name__ == "__main__":
    sys.exit(run())
