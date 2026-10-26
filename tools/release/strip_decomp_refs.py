"""Copy the DeepEngineers source tree without the decompiled-source citations in its comments.
Python 3.10 standard library only; run with ``python -I``.

 python -I tools/release/strip_decomp_refs.py <repo> <empty output folder> [--docs] [--keep-recon] [--dry-run]

What is removed, in comments only (C# ``//``, ``///`` and ``/* */``; Python ``#`` comments and triple-quoted strings;
XML ``<!-- -->`` in.sbc/.xml/.csproj/.props files, the world mod included):
 * file:line citations of decompiled sources: ``MySession.cs:4355``, ``<c>MyCockpit.cs:561</c>``,
 ``se/Sandbox.Game/.../MyPlanets.cs`` (paths of the repository itself are kept);
 * "decomp" phrases: ``VERIFIED decomp`` -> ``VERIFIED``, ``(decomp …)`` / ``(VERIFIED in the decompiled build)``
 parentheticals, ``read in the decomp``, ``VERIFIED line 188``;
 * the parentheses and punctuation left empty by the above.
Code is never touched: every C# file's code tokens (everything outside comments) and every Python file's tokens
outside comments and triple-quoted strings are compared before and after, and a file whose code would change is
copied unchanged and reported (exit 1). Python results must still parse.

Not copied.git,.vs, bin, obj, TestResults, __pycache__, third_party (loader binaries), tests/out, and docs/recon
(decompilation recon notes; --keep-recon copies them). --docs also strips the same citations from.md and.txt files
(as whole text). The internal-behaviour descriptions themselves stay: this tool removes citations, not knowledge; the
project decides what else to keep private (ROADMAP S9 options).

Exit: 0 copied and stripped; 1 at least one file kept unchanged because stripping would have touched code; 2 usage
error (output folder not empty, repository not found).
"""
from __future__ import annotations

import argparse
import ast
import os
import re
import shutil
import sys
from pathlib import Path

SKIP_DIRS = {".git", ".vs", ".idea", "bin", "obj", "TestResults", "__pycache__", ".pytest_cache", "third_party"}
SKIP_REL = {"tests/out"}
RECON_REL = "docs/recon"
OUR_PATH = re.compile(r"(?:^|[/\\])(?:src|tests|tools|docs)[/\\]|DeepEngineers", re.I)

# ------------------------------------------------------------------------------------------------- citation rules

_CS_REF = r"[\w.`/\\-]*\.cs(?::\d+(?:[ \t]*[-–/,][ \t]*\d+)*)?"     # a.cs path, optionally:line[-line, line]
_LINE_REF = re.compile(r"(?<![\w/\\.])(" + _CS_REF + r")")
_C_ELEMENT = re.compile(r"<c>[ \t]*(" + _CS_REF + r")[ \t]*</c>")
_PAREN = re.compile(r"[ \t]*\(((?:[^()]|\([^()]*\)){0,600})\)")     # one nesting level; may span lines (a block)
_DECOMP_WORD = re.compile(r"\bdecomp(?:ile|iled|ilation|iling)?\b", re.I)
_PHRASES = [
    (re.compile(r"\bVERIFIED[ \t]+(?:by[ \t]+|in[ \t]+(?:the[ \t]+)?|from[ \t]+(?:the[ \t]+)?)?decomp(?:ile|iled|ilation|iling)?\b"
                r"(?:[ \t]+(?:SE|Subnautica|Pulsar|BepInEx|Unity|build|sources?|code)(?:[ \t]+[\d.]+)?)*"
                r"(?:[ \t]+(?:sources?|build|code))?[ \t]*:?", re.I), "VERIFIED"),
    (re.compile(r"\bVERIFIED[ \t]+line[ \t]+\d+\b"), "VERIFIED"),
    (re.compile(r"\b(?:read|checked|looked[ \t]+up)[ \t]+in[ \t]+the[ \t]+decomp(?:iled)?\b"
                r"(?:[ \t]+(?:SE|Subnautica|build|sources?)(?:[ \t]+[\d.]+)?)*(?:[ \t]+(?:sources?|build))?", re.I), "read"),
    (re.compile(r"\b(?:the[ \t]+)?decomp(?:ile|iled)?[ \t]+root\b[^)\n;]*", re.I), ""),
    # bare line numbers continuing a removed citation: "(1014,:479)", ",:157-164"
    (re.compile(r"(?<![\w\])>])[ \t,]*:\d+(?:[ \t]*[-–/][ \t]*\d+)?(?:[ \t]*,[ \t]*:\d+(?:[ \t]*[-–/][ \t]*\d+)?)*"), ""),
]
_ENTITY = re.compile(r"&(#?\w{1,8});")

def _is_game_ref(ref: str) -> bool:
    return not OUR_PATH.search(ref)

def strip_comment(text: str) -> tuple[str, int]:
    """Strips the citations from comment text (one comment, a block of line comments joined with newlines, or a
 whole document). Newlines are kept, so the caller can split the result back into the same lines; only lines
 that changed are tidied. Returns (text, number of removals)."""
    n = 0

    def drop_paren(m):
        nonlocal n
        inner = m.group(1)
        if _DECOMP_WORD.search(inner) or any(_is_game_ref(r) for r in re.findall(r"\b\w[\w./\\-]*\.cs:\d+", inner)):
            n += 1
            return "\n" * m.group(0).count("\n")
        return m.group(0)

    t = _PAREN.sub(drop_paren, text)

    def drop_c(m):
        nonlocal n
        if _is_game_ref(m.group(1)) and (":" in m.group(1) or "/" in m.group(1)):
            n += 1
            return ""
        return m.group(0)

    t = _C_ELEMENT.sub(drop_c, t)

    def drop_ref(m):
        nonlocal n
        ref = m.group(1)
        if _is_game_ref(ref) and (re.search(r"\.cs:\d", ref) or "/" in ref or "\\" in ref):
            n += 1
            return ""
        return m.group(0)

    t = _LINE_REF.sub(drop_ref, t)
    for rx, rep in _PHRASES:
        t, k = rx.subn(rep, t)
        n += k
    if n:
        before, after = text.split("\n"), t.split("\n")
        if len(before) == len(after):
            t = "\n".join(_tidy(a, re.match(r"[ \t]*", b).group()) if a != b else a for a, b in zip(after, before))
    return t, n

def _tidy(line: str, lead_was: str = "") -> str:
    """Punctuation left behind on a changed line (XML/HTML entities such as &gt; are protected)."""
    keep = []

    def protect(m):
        keep.append(m.group(0))
        return "\x00%d\x01" % (len(keep) - 1)

    t = _ENTITY.sub(protect, line)
    lead = lead_was or re.match(r"[ \t]*", t).group()          # the line's original indentation
    body = t.lstrip(" \t")
    body = re.sub(r"\([ \t]*[,;:]?[ \t]*\)", "", body)                 # empty parentheses
    body = re.sub(r"\([ \t]*[,;:][ \t]*", "(", body)
    body = re.sub(r"[ \t]*[,;:][ \t]*\)", ")", body)
    body = re.sub(r"[ \t]{2,}", " ", body)
    body = re.sub(r"[ \t]+([,;)]|\.(?=\s|$))", r"\1", body)        # ";" "," " )" "." left by a removal
    body = re.sub(r",[ \t]*,", ",", body)
    body = re.sub(r"VERIFIED[ \t]*,[ \t]*VERIFIED", "VERIFIED", body)
    body = re.sub(r"^(?:[,;:]|\.(?=[ \t]|$|<))[ \t]*", "", body)        # a line that now starts with punctuation
    t = (lead + body).rstrip()
    return re.sub("\x00(\\d+)\x01", lambda m: keep[int(m.group(1))], t)

# ------------------------------------------------------------------------------------------------- segmenters

_CS_SEG = re.compile(r"""
    (?P<lc>//[^\n]*)
  | (?P<bc>/\*.*?(?:\*/|\Z))
  | (?P<vstr>\$?@\$?"(?:[^"]|"")*")
  | (?P<str>\$?"(?:[^"\\\n]|\\.)*")
  | (?P<chr>'(?:[^'\\\n]|\\.){1,10}')
""", re.S | re.X)

_PY_SEG = re.compile(r"""
    (?P<lc>\#[^\n]*)
  | (?P<tstr>(?:[rRbBuUfF]{0,2})(?:\"\"\"(?:[^\\]|\\.)*?\"\"\"|'''(?:[^\\]|\\.)*?'''))
  | (?P<str>(?:[rRbBuUfF]{0,2})(?:"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'))
""", re.S | re.X)

_LC_PREFIX = {".cs": re.compile(r"//+[ \t]?"), ".py": re.compile(r"#+[ \t]?")}

def _strip_block(lines, ext):
    """A run of consecutive line comments: the marker prefixes stay, the bodies are stripped as one text."""
    rx = _LC_PREFIX[ext]
    prefixes, bodies = [], []
    for c in lines:
        p = rx.match(c).group()
        prefixes.append(p)
        bodies.append(c[len(p):])
    new, k = strip_comment("\n".join(bodies))
    if not k:
        return lines, 0
    nb = new.split("\n")
    if len(nb) != len(bodies):
        return lines, 0
    # a comment line emptied by the stripping becomes None (dropped with its line break by the caller)
    return [None if (not b.strip() and ob.strip()) else (p + b).rstrip() if b.strip() else p.rstrip()
            for p, b, ob in zip(prefixes, nb, bodies)], k

def strip_source(text: str, ext: str) -> tuple[str, int]:
    """Strips the citations in the comments of one C# or Python file (strings and code untouched). Consecutive line
 comments (only a newline and indentation between them) are handled as one block, so a parenthetical citation that
 spans lines goes as a whole."""
    rx = _PY_SEG if ext == ".py" else _CS_SEG
    segs = []                                   # (start, end, kind)
    for m in rx.finditer(text):
        segs.append((m.start(), m.end(), m.lastgroup))
    out, pos, total, i = [], 0, 0, 0
    while i < len(segs):
        s, e, kind = segs[i]
        out.append(text[pos:s])
        if kind == "lc":
            j = i
            while j + 1 < len(segs) and segs[j + 1][2] == "lc" and re.fullmatch(r"[ \t]*\n[ \t]*", text[segs[j][1]:segs[j + 1][0]]):
                j += 1
            block = [text[a:b] for a, b, _ in segs[i:j + 1]]
            seps = [text[segs[x][1]:segs[x + 1][0]] for x in range(i, j)]
            new, k = _strip_block(block, ext)
            total += k
            kept = [(c, seps[x] if x < len(seps) else None) for x, c in enumerate(new) if c is not None]
            if not kept:                        # the whole block was citations: keep its first line's marker
                kept = [(_LC_PREFIX[ext].match(block[0]).group().rstrip(), None)]
            for x, (c, sep) in enumerate(kept):
                out.append(c)
                if x < len(kept) - 1:
                    out.append(sep if sep is not None else seps[-1])
            pos = segs[j][1]
            i = j + 1
            continue
        seg = text[s:e]
        if kind == "bc" or (kind == "tstr" and ext == ".py"):
            q = (3 + len(re.match(r"[rRbBuUfF]{0,2}", seg).group())) if kind == "tstr" else 2
            body, k = strip_comment(seg[q:-q if kind == "bc" else -3] if len(seg) >= 2 * q else "")
            if k:
                seg = seg[:q] + body + (seg[-2:] if kind == "bc" else seg[-3:])
                total += k
        out.append(seg)
        pos = e
        i += 1
    out.append(text[pos:])
    return "".join(out), total

XML_EXT = {".sbc", ".xml", ".csproj", ".props", ".targets", ".config", ".resx"}

# ------------------------------------------------------------------------------------------------ the project pass
#. In comments (and whole.md/.txt files with --docs) every
# sentence that names or quotes the project, the journal or the free-play sessions is dropped; code is never touched. Sentences end at.; ! ? or the end of the line.
_OWNER_WORDS = re.compile(r"\b(?:owner|owner's|owners|modlog|free[ -]?play|bslinger\w*|the user said|he said)\b|\bROOT\\", re.I)
_SENT = re.compile(r"[^.;!?\n]*(?:[.;!?]+|$)")

def _owner_line(line: str) -> tuple[str, int]:
    """One comment line without its marker: the sentences with project words removed; (text, removals)."""
    if not _OWNER_WORDS.search(line):
        return line, 0
    kept, n = [], 0
    for m in _SENT.finditer(line):
        s = m.group()
        if not s:
            continue
        if _OWNER_WORDS.search(s):
            n += 1
            continue
        kept.append(s)
    out = "".join(kept)
    out = re.sub(r"[ \t]{2,}", " ", out).strip()
    # an orphan opener/closer left by a removed sentence
    out = re.sub(r"^[\s,;:)\]]+|[\s,;:(\[]+$", "", out)
    return out, n

_LINE_MARK = re.compile(r"^(?P<lead>[ \t]*)(?P<mark>///?|#+|\*|<!--)?(?P<sp>[ \t]*)(?P<body>.*?)(?P<tail>[ \t]*(?:\*/|-->)?)$")

def _owner_segment(seg: str) -> tuple[str, int]:
    """A comment segment (line comments block, a block comment, a docstring, an XML comment, or a whole.md): the project pass
 line by line; a line left empty inside a block of line comments is dropped."""
    lines = seg.split("\n")
    out, total = [], 0
    for ln in lines:
        m = _LINE_MARK.match(ln)
        body = m.group("body")
        new, n = _owner_line(body)
        total += n
        if n and not new.strip():
            # the whole line was about the project: keep only a bare marker when it carried one and a block depends on it
            mark = m.group("mark") or ""
            if mark in ("///", "//", "#", "##", "###"):
                continue
            out.append(m.group("lead") + mark + m.group("tail"))
            continue
        out.append(m.group("lead") + (m.group("mark") or "") + (m.group("sp") if m.group("mark") else "") + new + m.group("tail") if n else ln)
    return "\n".join(out), total

def strip_owner(text: str, ext: str) -> tuple[str, int]:
    """The project pass over the comments of a.cs/.py file, the comments of an XML file, or a whole.md/.txt."""
    if ext in (".md", ".txt"):
        return _owner_segment(text)
    if ext in XML_EXT:
        total = 0

        def one(m):
            nonlocal total
            body, n = _owner_segment(m.group("xc")[4:-3])
            total += n
            return "<!--" + body + "-->"
        return _XML_SEG.sub(one, text), total
    rx = _PY_SEG if ext == ".py" else _CS_SEG
    out, pos, total = [], 0, 0
    for m in rx.finditer(text):
        out.append(text[pos:m.start()])
        seg = text[m.start():m.end()]
        kind = m.lastgroup
        if kind in ("lc", "bc") or (kind == "tstr" and ext == ".py"):
            new, n = _owner_segment(seg)
            total += n
            seg = new
        out.append(seg)
        pos = m.end()
    out.append(text[pos:])
    return "".join(out), total
_XML_SEG = re.compile(r"(?P<xc><!--.*?-->)", re.S)

def strip_xml(text: str) -> tuple[str, int]:
    """XML comments <!--... --> (the world mod's.sbc, the project files)."""
    total = 0

    def one(m):
        nonlocal total
        body, k = strip_comment(m.group()[4:-3])
        total += k
        return "<!--" + body + "-->" if k else m.group()

    return _XML_SEG.sub(one, text), total

def code_tokens(text: str, ext: str):
    """The tokens a compiler sees, minus comments (and, for Python, minus triple-quoted strings): the invariant."""
    if ext in XML_EXT:
        return _XML_SEG.sub(" ", text).split()
    rx = _PY_SEG if ext == ".py" else _CS_SEG
    parts, pos = [], 0
    for m in rx.finditer(text):
        parts.append(text[pos:m.start()])
        if m.lastgroup in ("str", "vstr", "chr"):
            parts.append(" " + m.group() + " ")
        else:
            parts.append(" ")
        pos = m.end()
    parts.append(text[pos:])
    return "".join(parts).split()

# ------------------------------------------------------------------------------------------------- tree copy

def wanted(rel: str, keep_recon: bool) -> bool:
    if any(rel == s or rel.startswith(s + "/") for s in SKIP_REL):
        return False
    if not keep_recon and (rel == RECON_REL or rel.startswith(RECON_REL + "/")):
        return False
    return True

def run(argv=None, out=print) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("repo")
    ap.add_argument("output")
    ap.add_argument("--docs", action="store_true", help="also strip .md and .txt files")
    ap.add_argument("--keep-recon", action="store_true", help="also copy docs/recon")
    ap.add_argument("--dry-run", action="store_true", help="report only, write nothing")
    ap.add_argument("--keep-owner", action="store_true", help="keep the comments that name or quote the owner (default: removed)")
    a = ap.parse_args(argv)
    repo, dst = Path(a.repo).resolve(), Path(a.output).resolve()
    if not (repo / "DeepEngineers.sln").is_file() and not (repo / "src").is_dir():
        out("not a DeepEngineers source tree: %s" % repo)
        return 2
    if dst == repo or repo in dst.parents:
        out("the output must be outside the repository: %s" % dst)
        return 2
    if dst.exists() and any(dst.iterdir()) and not a.dry_run:
        out("output folder not empty: %s" % dst)
        return 2
    files = stripped_files = removed = owner_removed = 0
    kept_unchanged = []
    for dirpath, dirnames, filenames in os.walk(repo):
        d = Path(dirpath)
        reld = d.relative_to(repo).as_posix()
        dirnames[:] = sorted(x for x in dirnames if x not in SKIP_DIRS and
                             wanted((reld + "/" + x).lstrip("./") if reld != "." else x, a.keep_recon))
        for f in sorted(filenames):
            src = d / f
            rel = src.relative_to(repo).as_posix()
            if not wanted(rel, a.keep_recon):
                continue
            files += 1
            target = dst / rel
            ext = src.suffix.lower()
            data = None
            if ext in (".cs", ".py") or ext in XML_EXT or (a.docs and ext in (".md", ".txt")):
                raw = src.read_bytes()
                bom = raw.startswith(b"\xef\xbb\xbf")
                try:
                    text = raw[3 if bom else 0:].decode("utf-8")
                except UnicodeDecodeError:
                    text = None
                if text is not None:
                    if ext in (".md", ".txt"):
                        new, k = strip_comment(text)
                    elif ext in XML_EXT:
                        new, k = strip_xml(text)
                    else:
                        new, k = strip_source(text, ext)
                    if not a.keep_owner:
                        new, k2 = strip_owner(new, ext)
                        k += k2
                        owner_removed += k2
                    if k:
                        ok = ext in (".md", ".txt") or code_tokens(text, ext) == code_tokens(new, ext)
                        if ok and ext == ".py":
                            try:
                                ast.parse(new)
                            except SyntaxError:
                                ok = False
                        if ok:
                            data = (b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8")
                            stripped_files += 1
                            removed += k
                        else:
                            kept_unchanged.append(rel)
            if a.dry_run:
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            if data is None:
                shutil.copy2(src, target)
            else:
                target.write_bytes(data)
                shutil.copystat(src, target)
    out("strip_decomp_refs: %d files %s, %d with citations stripped (%d removals, %d of them owner sentences)%s" % (
        files, "examined" if a.dry_run else "copied", stripped_files, removed, owner_removed,
        "" if a.keep_recon else "; docs/recon left out"))
    for rel in kept_unchanged:
        out("  KEPT UNCHANGED (stripping would have touched code): %s" % rel)
    return 1 if kept_unchanged else 0

if __name__ == "__main__":
    sys.exit(run())
