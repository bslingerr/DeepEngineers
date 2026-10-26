"""S7 world settings: oxygen and airtightness of the lab world, and the S7 throwaway copies (ROADMAP S7 "project actions" 1-2).

The game is read, never written by the plugins (fact B): the lab world's EnableOxygen / EnableOxygenPressurization are the
project's. This tool is for the orchestrator, with Space Engineers closed and the project's approval, --dry-run first:

 python -I tools/lab/set_world_settings.py --world "DE Lab" --oxygen on --pressurization on [--dry-run]
 python -I tools/lab/set_world_settings.py --copy "DE Lab NoPress" --oxygen on --pressurization off [--force] [--dry-run]
 (S7.N1b: the same room on a world without airtightness: "DE.Interior OFF pressurization disabled" once)
 python -I tools/lab/set_world_settings.py --copy "DE Lab S7Save" --oxygen on --pressurization on --point-last-session
 python -I tools/lab/set_world_settings.py --copy "DE Lab SunWeather" --sun-base X,Y,Z [--dry-run]
 (S7.M6: only the sector weather's BaseSunDirection moved off the environment's rotation plane, the environment
 definition stock: SunSync's bind check |base·axis| < 1e-3 fails and refuses ("DE.Time REFUSE axis"); X,Y,Z = a
 direction with a component along the axis that the DE.Time bind line logs, e.g. the axis itself)
 python -I tools/lab/set_world_settings.py --check "DE Lab" (read-only report)
 python -I tools/lab/set_world_settings.py --selftest (no file touched)
Options: --from SOURCE (default "DE Lab"; the copy's source), --saves-root <APPDATA>/SpaceEngineers/Saves/<steamid>,
--autosave-off (copies only), --point-last-session / --restore-last-session (LastSession.sbl, as make_survival_copy).

Rules: refuses while Space Engineers runs (--dry-run, --check and --selftest only read); a copy carries DE_THROWAWAY.txt
and --force replaces only a marked copy; --sun-base is refused on "DE Lab" itself; edits are text edits of the XML
elements (never re-serialised): <EnableOxygen>, <EnableOxygenPressurization> inside <Settings> (asserted, set, or added),
<BaseSunDirection x= y= z= /> inside the MyObjectBuilder_SectorWeatherComponent session component (Sandbox.sbc only;
MySectorWeatherComponent.Init reads it when it is not zero, decomp MySectorWeatherComponent.cs:260-268). Every change is
printed. Stdlib only.
"""
import argparse
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import make_survival_copy as msc   # noqa: E402 (se_running, saves_root_for, read, point_last_session, MARKER, LAB)

LAB = msc.LAB
MARKER = msc.MARKER
WORLD_FILES = ("Sandbox.sbc", "Sandbox_config.sbc")
BACKUP_SUFFIX = ".bak-s7"
WEATHER_OPEN = '<MyObjectBuilder_SessionComponent xsi:type="MyObjectBuilder_SectorWeatherComponent">'

def _onoff(v):
    return "true" if v else "false"

def set_bool_setting(text, tag, value, changes):
    """<tag>true|false</tag>: set, or added right after <Settings> when absent."""
    want = _onoff(value)
    m = re.search(r"<%s>([^<]*)</%s>" % (tag, tag), text)
    if m is None:
        s = re.search(r"<Settings>", text)
        if s is None:
            raise ValueError("no <Settings> element: cannot add <%s>" % tag)
        changes.append("%s added = %s" % (tag, want))
        return text[:s.end()] + "\n    <%s>%s</%s>" % (tag, want, tag) + text[s.end():]
    if m.group(1).strip().lower() != want:
        changes.append("%s %s -> %s" % (tag, m.group(1), want))
        return text[:m.start(1)] + want + text[m.end(1):]
    return text

def set_session_name(text, name, changes):
    m = re.search(r"<SessionName>([^<]*)</SessionName>", text)
    if m is not None and m.group(1) != name:
        changes.append("SessionName %r -> %r" % (m.group(1), name))
        return text[:m.start(1)] + name + text[m.end(1):]
    return text

def set_autosave_off(text, changes):
    m = re.search(r"<AutoSaveInMinutes>([^<]*)</AutoSaveInMinutes>", text)
    if m is None:
        s = re.search(r"<Settings>", text)
        if s is None:
            return text
        changes.append("AutoSaveInMinutes added = 0")
        return text[:s.end()] + "\n    <AutoSaveInMinutes>0</AutoSaveInMinutes>" + text[s.end():]
    if m.group(1).strip() != "0":
        changes.append("AutoSaveInMinutes %s -> 0" % m.group(1))
        return text[:m.start(1)] + "0" + text[m.end(1):]
    return text

def _fmt(v):
    return repr(float(v))

def set_sun_base(text, xyz, changes):
    """BaseSunDirection of the sector weather session component (Sandbox.sbc). The component must exist (a world saved
 by SE 1.200+ has it); the element is replaced or added inside it."""
    i = text.find(WEATHER_OPEN)
    if i < 0:
        raise ValueError("no MyObjectBuilder_SectorWeatherComponent session component in the checkpoint")
    j = text.find("</MyObjectBuilder_SessionComponent>", i)
    if j < 0:
        raise ValueError("unterminated weather component")
    block = text[i:j]
    new_el = '<BaseSunDirection x="%s" y="%s" z="%s" />' % tuple(_fmt(v) for v in xyz)
    m = re.search(r"<BaseSunDirection\b[^>]*/>|<BaseSunDirection\b[^>]*>.*?</BaseSunDirection>", block, re.S)
    if m is not None:
        if m.group(0) == new_el:
            return text
        changes.append("BaseSunDirection %s -> %s" % (m.group(0), new_el))
        block = block[:m.start()] + new_el + block[m.end():]
    else:
        changes.append("BaseSunDirection added = %s" % new_el)
        block = block[:len(WEATHER_OPEN)] + "\n        " + new_el + block[len(WEATHER_OPEN):]
    return text[:i] + block + text[j:]

def read_settings(text):
    out = {}
    for tag in ("GameMode", "EnableOxygen", "EnableOxygenPressurization", "SessionName", "AutoSaveInMinutes"):
        m = re.search(r"<%s>([^<]*)</%s>" % (tag, tag), text)
        out[tag] = m.group(1) if m else None
    i = text.find(WEATHER_OPEN)
    if i >= 0:
        j = text.find("</MyObjectBuilder_SessionComponent>", i)
        m = re.search(r"<BaseSunDirection\b[^>]*/>", text[i:j])
        out["BaseSunDirection"] = m.group(0) if m else None
    return out

def edit_text(text, fname, name=None, oxygen=None, press=None, sun_base=None, autosave_off=False):
    """(new text, changes) of one world file."""
    changes = []
    if oxygen is not None:
        text = set_bool_setting(text, "EnableOxygen", oxygen, changes)
    if press is not None:
        text = set_bool_setting(text, "EnableOxygenPressurization", press, changes)
    if name is not None:
        text = set_session_name(text, name, changes)
    if autosave_off:
        text = set_autosave_off(text, changes)
    if sun_base is not None and fname == "Sandbox.sbc":
        text = set_sun_base(text, sun_base, changes)
    return text, changes

def edit_world(folder, dry, backup, **kw):
    for fname in WORLD_FILES:
        p = os.path.join(folder, fname)
        if not os.path.exists(p):
            print("%s: absent" % fname)
            continue
        new, changes = edit_text(msc.read(p), fname, **kw)
        print("%s: %s" % (fname, "; ".join(changes) if changes else "already as wanted"))
        if not changes or dry:
            continue
        if backup and not os.path.exists(p + BACKUP_SUFFIX):
            shutil.copy2(p, p + BACKUP_SUFFIX)
            print("  backed up once as %s" % (fname + BACKUP_SUFFIX))
        with open(p, "w", encoding="utf-8", newline="") as f:
            f.write(new)

def check(root, name):
    folder = os.path.join(root, name)
    print("world:", folder, "(exists)" if os.path.isdir(folder) else "(MISSING)")
    if not os.path.isdir(folder):
        return 1
    for fname in WORLD_FILES:
        p = os.path.join(folder, fname)
        if os.path.exists(p):
            s = read_settings(msc.read(p))
            print("  %-19s %s" % (fname, "; ".join("%s %s" % (k, v if v is not None else "-") for k, v in s.items())))
    print("  marker %s: %s" % (MARKER, "present" if os.path.exists(os.path.join(folder, MARKER)) else "absent"))
    for fname in WORLD_FILES:
        if os.path.exists(os.path.join(folder, fname + BACKUP_SUFFIX)):
            print("  backup %s present" % (fname + BACKUP_SUFFIX))
    return 0

def _xyz(text):
    try:
        v = [float(t) for t in text.split(",")]
    except ValueError:
        raise argparse.ArgumentTypeError("expected X,Y,Z, got %r" % text)
    if len(v) != 3 or sum(c * c for c in v) < 1e-12:
        raise argparse.ArgumentTypeError("expected a non-zero X,Y,Z, got %r" % text)
    n = sum(c * c for c in v) ** 0.5
    return [c / n for c in v]

def _bool(text):
    t = text.strip().lower()
    if t in ("on", "true", "1", "yes"):
        return True
    if t in ("off", "false", "0", "no"):
        return False
    raise argparse.ArgumentTypeError("on|off, got %r" % text)

# ----------------------------------------------------------------------------------------------------------------
# selftest (in memory)
# ----------------------------------------------------------------------------------------------------------------

_SAMPLE = """<?xml version="1.0"?>
<MyObjectBuilder_Checkpoint xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <SessionName>DE Lab</SessionName>
  <Settings>
    <GameMode>Creative</GameMode>
    <EnableOxygen>false</EnableOxygen>
    <AutoSaveInMinutes>5</AutoSaveInMinutes>
  </Settings>
  <SessionComponents>
    <MyObjectBuilder_SessionComponent xsi:type="MyObjectBuilder_SectorWeatherComponent">
      <BaseSunDirection x="0" y="0" z="0" />
      <SunDirectionNormalized x="0.3" y="0.6" z="-0.7" />
    </MyObjectBuilder_SessionComponent>
  </SessionComponents>
</MyObjectBuilder_Checkpoint>
"""

def selftest():
    fails = []

    def ck(cond, what):
        if not cond:
            fails.append(what)
    t, ch = edit_text(_SAMPLE, "Sandbox.sbc", oxygen=True, press=True)
    s = read_settings(t)
    ck(s["EnableOxygen"] == "true" and s["EnableOxygenPressurization"] == "true", "oxygen and pressurisation on: %s" % s)
    ck(any("EnableOxygen false -> true" in c for c in ch) and any("EnableOxygenPressurization added" in c for c in ch), "changes listed: %s" % ch)
    t2, ch2 = edit_text(t, "Sandbox.sbc", oxygen=True, press=True)
    ck(t2 == t and not ch2, "idempotent")
    t3, _ = edit_text(t, "Sandbox_config.sbc", press=False, name="DE Lab NoPress", autosave_off=True)
    s3 = read_settings(t3)
    ck(s3["EnableOxygenPressurization"] == "false" and s3["SessionName"] == "DE Lab NoPress" and s3["AutoSaveInMinutes"] == "0", "NoPress copy: %s" % s3)
    t4, ch4 = edit_text(_SAMPLE, "Sandbox.sbc", sun_base=[0.0, 1.0, 0.0])
    ck('x="0.0" y="1.0" z="0.0"' in read_settings(t4)["BaseSunDirection"], "sun base set: %s" % read_settings(t4)["BaseSunDirection"])
    ck("SunDirectionNormalized" in t4 and '<SunDirectionNormalized x="0.3"' in t4, "the saved sun direction untouched")
    t5, ch5 = edit_text(_SAMPLE, "Sandbox_config.sbc", sun_base=[0.0, 1.0, 0.0])
    ck(t5 == _SAMPLE and not ch5, "the config file has no weather component: untouched")
    no_el = _SAMPLE.replace('      <BaseSunDirection x="0" y="0" z="0" />\n', "")
    t6, ch6 = edit_text(no_el, "Sandbox.sbc", sun_base=[1.0, 0.0, 0.0])
    ck(any("BaseSunDirection added" in c for c in ch6) and 'x="1.0"' in t6, "sun base added")
    try:
        edit_text(_SAMPLE.replace(WEATHER_OPEN, "<X>"), "Sandbox.sbc", sun_base=[1.0, 0.0, 0.0])
        ck(False, "no weather component must refuse")
    except ValueError:
        pass
    try:
        edit_text("<Checkpoint/>", "Sandbox.sbc", oxygen=True)
        ck(False, "no <Settings> must refuse")
    except ValueError:
        pass
    ck(_xyz("0,2,0") == [0.0, 1.0, 0.0], "normalised")
    ck(_bool("ON") is True and _bool("off") is False, "on/off")
    for f in fails:
        print("SELFTEST FAIL: " + f)
    print("set_world_settings selftest " + ("PASS" if not fails else "FAIL (%d)" % len(fails)))
    return 0 if not fails else 1

def main(argv=None):
    ap = argparse.ArgumentParser(description="S7 world settings: oxygen/airtightness of DE Lab and the S7 throwaway copies")
    ap.add_argument("--world", default=None, help="edit this world in place (DE Lab: --oxygen/--pressurization only)")
    ap.add_argument("--copy", default=None, metavar="NAME", help="make a throwaway copy NAME of --from and edit it")
    ap.add_argument("--from", dest="source", default=LAB)
    ap.add_argument("--oxygen", type=_bool, default=None, metavar="on|off")
    ap.add_argument("--pressurization", type=_bool, default=None, metavar="on|off")
    ap.add_argument("--sun-base", type=_xyz, default=None, metavar="X,Y,Z", help="S7.M6: BaseSunDirection of the copy")
    ap.add_argument("--autosave-off", action="store_true", help="copies only: AutoSaveInMinutes 0")
    ap.add_argument("--force", action="store_true", help="replace an existing copy that carries the marker")
    ap.add_argument("--point-last-session", action="store_true", help="LastSession.sbl -> the edited world")
    ap.add_argument("--restore-last-session", action="store_true", help="LastSession.sbl -> --from")
    ap.add_argument("--saves-root", default=None)
    ap.add_argument("--check", default=None, metavar="NAME")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    root = a.saves_root or msc.saves_root_for(a.source if not a.world else a.world)
    if a.check:
        return check(root, a.check)
    if not a.dry_run:
        running = msc.se_running()
        if running is None or running:
            sys.exit("Space Engineers is running or the process list is unreadable: refusing (worlds are edited with SE closed)")
    if a.restore_last_session:
        msc.point_last_session(root, os.path.join(root, a.source), a.source, a.dry_run)
        print("dry run: nothing written" if a.dry_run else "done")
        return 0
    if bool(a.world) == bool(a.copy):
        sys.exit("exactly one of --world NAME (in place) or --copy NAME (a throwaway copy of --from)")
    if a.oxygen is None and a.pressurization is None and a.sun_base is None and not a.autosave_off:
        sys.exit("nothing to change (--oxygen, --pressurization, --sun-base, --autosave-off)")
    if a.world:
        folder = os.path.join(root, a.world)
        if not os.path.isdir(folder):
            sys.exit("world not found: " + folder)
        if a.sun_base is not None or a.autosave_off:
            if a.world.strip().lower() == LAB.lower() or not os.path.exists(os.path.join(folder, MARKER)):
                sys.exit("--sun-base and --autosave-off only on a marked throwaway copy (never on %s or an unmarked world)" % LAB)
        print("editing %s in place%s" % (folder, " (dry run)" if a.dry_run else ""))
        edit_world(folder, a.dry_run, backup=True, oxygen=a.oxygen, press=a.pressurization, sun_base=a.sun_base, autosave_off=a.autosave_off)
    else:
        name = a.copy
        if name.strip().lower() in (a.source.strip().lower(), LAB.lower()):
            sys.exit("the copy must not be named like the source or the lab world")
        src, dst = os.path.join(root, a.source), os.path.join(root, name)
        if not os.path.isdir(src):
            sys.exit("source not found: " + src)
        if os.path.isdir(dst):
            if not a.force:
                sys.exit("destination exists (use --force to replace a marked copy): " + dst)
            if not os.path.exists(os.path.join(dst, MARKER)):
                sys.exit("refusing --force: %s carries no %s marker" % (dst, MARKER))
        print("copy %s -> %s (without Backup)%s" % (src, dst, " [replacing the marked copy]" if os.path.isdir(dst) else ""))
        if not a.dry_run:
            if os.path.isdir(dst):
                shutil.rmtree(dst)
            shutil.copytree(src, dst, ignore=shutil.ignore_patterns("Backup"))
            with open(os.path.join(dst, MARKER), "w", encoding="utf-8") as f:
                f.write("throwaway copy made by tools/lab/set_world_settings.py from %s (S7); --force may replace it\n" % a.source)
        edit_world(dst if not a.dry_run else src, a.dry_run, backup=False, name=name, oxygen=a.oxygen, press=a.pressurization,
                   sun_base=a.sun_base, autosave_off=a.autosave_off)
        folder = dst
    if a.point_last_session:
        msc.point_last_session(root, folder, os.path.basename(folder), a.dry_run)
    print("dry run: nothing written" if a.dry_run else "done: " + folder)
    return 0

if __name__ == "__main__":
    sys.exit(main())
