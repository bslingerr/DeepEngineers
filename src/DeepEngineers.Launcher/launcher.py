"""DeepEngineers launcher (Python 3.10 stdlib): preflight, deploy, manifest, run, s1, s2, s2b, s3, s4, s5a, s5, tp1,
s6, s7a, s7, stop, restore-registry.

CONTRACT §7 "Launcher" table and ARCHITECTURE §5.14; ROADMAP S0 "Launcher" work item, S1 "Launcher" and §5.7.

 python -I src/DeepEngineers.Launcher/launcher.py preflight
 python -I src/DeepEngineers.Launcher/launcher.py deploy [--dry-run] # copies build.ps1's build output
 python -I src/DeepEngineers.Launcher/launcher.py manifest # records what is deployed
 python -I src/DeepEngineers.Launcher/launcher.py run [--ready-phase ReadyIdle|Menu] [--continue] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s1 [--quick] [--parts wm2,hold,wm3,wm4,yaw,g1e5] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s1 --analyze tests/out/s1/<stamp> # re-run the oracles only
 python -I src/DeepEngineers.Launcher/launcher.py s2 [--parts n9,n8,td1,n2,n7,n7b,n3,n4,n11,n6,ta1,n5] [--owner] [--quick]
 python -I src/DeepEngineers.Launcher/launcher.py s2 --analyze tests/out/s2/<stamp> [--baseline SE_ALONE_DIR]
 python -I src/DeepEngineers.Launcher/launcher.py s2b [--parts n1,n2,n3,n4] [--quick] [--mutants S2b.M1,...] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s2b --analyze tests/out/s2b/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py s5a [--parts fins,o2,arms,choke] [--quick] [--mutants S5a.M1,...] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s5a --analyze tests/out/s5a/<stamp> [--sn-log SN_LOG]
 python -I src/DeepEngineers.Launcher/launcher.py s3 [--mode S3a|S3b] [--parts...] [--mutant-group NAME] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s3 --list-mutant-groups | --analyze tests/out/s3/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py s4 --mode S4se|S4both [--parts...|all] [--mutants S4.M2,...]
 [--mutant-group NAME] [--world COPY --owner] [--quick] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s4 --list-mutant-groups | --selftest | --analyze tests/out/s4/<stamp>
 [--sn-log FILE] [--baseline DIR] [--no-plugin-log FILE]
 python -I src/DeepEngineers.Launcher/launcher.py s5 --mode S5a|S5b [--parts g1e4,n1,n2,n3,n4,n5,n6,n7,record,tp1,
 teardown] [--synth 500,2000,5000 | --replay NAME] [--mutants
 S5.M1,...] [--mutant-group NAME] [--ships 500=BP,...] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s5 --list-mutant-groups | --analyze tests/out/s5/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py tp1 --state base-se|base-sn|space|track-hover|track-sail|prime|
 underwater[,...] [--ccx1] [--seconds 300] [--baseline DIR]
 python -I src/DeepEngineers.Launcher/launcher.py s6 --mode S6a|S6se|S6b [--parts...] [--mutant-group NAME]
 [--world COPY] [--owner] [--quick] [--dry-run] (launcher_s6.py)
 python -I src/DeepEngineers.Launcher/launcher.py s6 --dry-run | --list-mutant-groups | --analyze tests/out/s6/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py s7a [--parts n1,n2,n3,n4,n5,teardown] [--owner] [--mutant S7a.Mx]
 [--deck BLUEPRINT] [--dev-phases] [--dry-run] (launcher_s7a.py)
 python -I src/DeepEngineers.Launcher/launcher.py s7a --list-mutant-groups | --analyze tests/out/s7a/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py tp1 --state... --picture site|waterline (S7a.T-P1r)
 python -I src/DeepEngineers.Launcher/launcher.py s7 [--parts n1,n1b,n1c,n2,n3,n4,n5,n6,n7,n8,n9,n10,n11,wm5,teardown]
 [--owner] [--mutant S7.Mx] [--world NAME] [--room BLUEPRINT]
 [--ship BLUEPRINT] [--quick] [--dry-run] (launcher_s7.py)
 python -I src/DeepEngineers.Launcher/launcher.py s7 --census [--batches all|FILE] [--dry-run] | --list-mutant-groups |
 --selftest | --analyze tests/out/s7/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py stop [--kill]
 python -I src/DeepEngineers.Launcher/launcher.py restore-registry --yes # Screenmanager keys from the backup
 python -I src/DeepEngineers.Launcher/launcher.py paths [--json] # S9: resolved folders (launcher_release.py)
 python -I src/DeepEngineers.Launcher/launcher.py backup [--sets...] [--scope de|full] [--keep N] # S9: zip step
In a release folder (<root>/launcher/ next to <root>/release.json; tools/release/make_release.py) the folders come
from the installer's paths.json or Steam (launcher_paths.py) and only the player verbs are offered.

Rules it keeps: Subnautica first, started directly with `-monitor 1`, `DEEPENGINEERS_BRIDGE=1` in its environment
and the named object `<section>_launcher` held until the ready phase (S0: Menu; from S1: ReadyIdle, the default),
180 s; the section is only ever opened (never created) and the handle closed after the ready phase; SE (`Legacy.exe
-noUpdate -noSplash -noPrompt -bin64 <Bin64>`) started as a child, then AllowSetForegroundWindow(sePid); mixed
elevation refused; closing is CloseMainWindow then waiting on the PID; a kill only by exact PID after 45 s, only with
--kill and the project's typed confirmation; never a Steam start of SE; no automatic relaunch.

Ready wait (S1 semantics): open-only, read-only polling every 0.25 s for `magic` valid, `snPid` equal to the started
process and `snPhase` >= the ready phase (ReadyIdle, WarmingUp or Active from S1; Quitting never
counts); Booting, Menu and LoadingSave keep waiting; a phase that falls back to None after the bridge started (the
plugin withdrew), or the process exiting, ends the wait at once. The time to the ready phase is logged
(`sn_ready`, CONTRACT O11). On timeout SE is not started unless --se-anyway (S0 real-run rule).

`--continue` appends `-continue` to Legacy.exe's arguments. It is a Pulsar option, not an SE one (VERIFIED: Pulsar
2.4.3's Pulsar.Shared.dll declares "-continue" with the description "Continue the last game automatically."; SE
1.210.014's MySandboxGame.ProcessCommandLine knows only +connect_lobby / +launch_scenario*; and the S0 real runs
loaded the last world with it). `s1` passes it by default (--no-continue to stop that).

`s1` runs one scripted S1 session (ROADMAP S1 oracle): preflight -> run (Subnautica, ReadyIdle, SE with -continue) ->
SE in world with the hello acknowledged -> the C11 test commands of tests/s1/check_wm.py `steps` in order
(de.teleport WM-2, hold, de.capture; the 60 s hold; de.path WM-3; de.teleport + de.look x 3 at the three WM-4
points; 50 de.look yaw+30; de.teleport + de.pixel (SE, then SN with SE's pointSn; Subnautica answers "pending" and
logs DE.SnPixel) at the 9 G1-E5 cases, at SE's configured FOV) -> SE closed with CloseMainWindow, Subnautica quits by
itself -> SE/SN JSONL, the game logs and the captures copied into tests/out/s1/<stamp>/ -> tests/s1/check_wm.py,
latency.py and fmod_events.py -> the S1 oracle table (s1-results.json, s1-table.txt). With --sn-cmd/--se-cmd it runs
against the Python stand-ins (their stdout is the JSONL; the SE stand-in is closed by TerminateProcess, never a
game). S1.N4 (host slot untouched): SHA-256 of every file of the host slot before Subnautica starts and after it
exits (read-only); when Subnautica has not exited, the launcher waits up to 60 s more, then hashes anyway and the
row is MISSING unless a file changed (FAIL): the table cannot pass without S1.N4.

`s2` runs one scripted S2 session (ROADMAP S2 oracle) with the s1 machinery (preflight, run, the in-world wait,
the C11 harness, close, collect): the parts of S2Runner (`--dry-run` lists them) through the plugins' S2 commands
(SE: de.display {toggle | seeThrough on|off | margins | readback | probe}, de.view, de.audio, de.zorder; SN: de.calib,
de.s2; the forced see-through adds to the view machine's request, so T-D1 toggles in Prime;
the motions de.teleport and de.motion {kind: line, fromSn, toSn, speedMps | kind: bob, x, z, centreY, amplitudeM,
hz, seconds} - de.motion is a proposal to SE (not S1's de.path, which ignores unknown keys), with a teleport fallback
that the session's warnings name), while read-only threads sample the
section (10 Hz "sample" events tagged with the part: C0 heartbeats/phases/hwnds, C5, C7, C1 tick), time every
waterline crossing (C1 qpcCapture of C5 crossingTick -> first C7 presentedSeTick >= it, ~1 kHz) and record the
foreground every 100 ms from before SE starts (S2.N10). Tools: tools/display/walker.py after every toggle,
tools/display/notepad_between.py (S2.N5), tools/display/ui_automation.py (S2.N9), tools/capture/composed_video.py
(td1 and N0 videos; never in stand-in runs or with --no-video), tools/capture/desktop_shot.ps1 (menu shots),
tools/measure/measure.ps1 (N3/N4/N11), tools/audio/peaks.py (T-A1). WaterClientSettings.xml is hashed before and
after (read-only).s
click; the launcher prints the instruction, waits for its effect on the section (C5 overlays/flags, C0 sePhase) and
records it. Output tests/out/s2/<stamp>/ (s2-session.jsonl, logs, videos, CSVs, peaks, s2-table.txt,
s2-results.json); the table joins tests/s2/analyze_td1.py, analyze_n2.py, analyze_n7b.py, analyze_perf.py, analyze_ta1.py and the
launcher's own rows (N5, N6, N9, N10, OTHER). Stand-in dry run: --sn-cmd/--se-cmd with tests/standins/fake_sn_host.py
--s2 --s2-window and fake_se_producer.py --s2 --s2-window (off-screen windows), --skip-preflight --quick.

`s2b` runs one scripted S2b swimming session
in SE ALONE, with the `s2 --se-alone` machinery: no Subnautica process; the passive stand-in host
(tests/standins/fake_sn_host.py --passive) creates the section so SE links and the C11 harness works; SE is
Legacy.exe -continue (the last world, DE Lab); --mutants puts DEEPENGINEERS_MUTANTS in SE's environment only (S2b.M1
numbers x1.5, M2 jetpack left on, M3 no hysteresis, M4 O2 rule off). No human is at the keyboard, so the character is
driven through C11 `de.swim` (SE plugin S2bCommands / SwimScript) with its scripted-input source:
 de.swim {"teleportChar": [x, y, z], "yawDeg": 0} the local character's origin (feet) to SN metres, upright, facing
 SN north; velocity zeroed; a spectator camera is handed back to the character; the swim model is re-armed
 (a teleport while swimming re-enters without an exit line)
 de.swim {"script": [{"label", "seconds", "forward", "strafe", "vertical", "sprint", "jetpack"},...], "label"}
 steps run back to back from SE's next tick (round(seconds x 60) ticks each); inputs in [-1, 1]; "jetpack"
 true/false is a one-shot request at the step start (the swim rule may refuse it); no look keys: the first
 step looks level at yaw 0 (the teleport's heading). The launcher also records "move" [strafe, vertical,
 forward] in the session log (SE ignores it). Every script ends with a release step
 de.swim {"dump": true} the controller's state (state, eyeY, eyeSn, vSet, vSe, enters, exits, policy, script, tick,
 suit {helmet, h2Level, jetpackPowered, o2Bottles, h2Bottles, survival},...)
 de.swim {"o2": "dump"} the oxygen rule (rule, tankSeconds, level, amountL, capacityL, multiplier,...)
 de.swim {"model": "on", "rows": "on",...} the model on (SE starts with DEEPENGINEERS_SWIM=standby: DE Lab loads at
 -3,000 m, where the model would breathe Subnautica's crush class before the harness can refill) and a
 "swim" row every tick the model is evaluated (the plugin's default is every tick only while scripted)
Session order: the dump check; the model on + rows on + teleport to the refill spot in one command;
the preconditions (precheck: helmet closed, jetpack powered with >= 25 % hydrogen, no O2 bottle carried; a failure
refuses the parts: without hydrogen SE refuses the jetpack and N2/N3/S2b.M2 cannot be judged, an open helmet drowns
the engineer, a bottle refills the suit below 50 % and breaks N4); a refill; the suit preparation (teleport above the
water, a jetpack-off request, fall back in: a known jetpackWas=off for the M3 surface hold and the restore checks);
then the parts. After each script the launcher polls the dump until "script" is null (SE's simulation may run slower
than the wall clock). The parts (--dry-run prints them with their scripts):
 n1 at -50 m, two scripts, a surface refill before each (Subnautica's diver holds 45 s of O2 and the whole
 sequence would empty it): forward 10 s, release 5 s, strafe 5 s, release 5 s | up 3 s, down 3 s, release 3 s,
 backward 5 s, release 4 s, sprint forward 5 s, release 5 s
 n2 from -20 m: a jetpack request with the head under water (refused; S2b.M2 leaves it on), vertical up held 20 s
 (the surface clamp), de.swim dumps at +0.5, +1.5, +5, +10, +15, +20 s; n2j at the surface: up 2 s, a jetpack
 request with the head out (granted: SE's jetpack takes over, mode Fly, the swim latch stays on below +0.8 m),
 jetpack off and back to the surface float
 n3 4 cycles (2 with --quick) across the surface: swim up 2.5 s into the clamp, jetpack on + up 1.5 s (out of the
 water: SE's jetpack lifts the eye past +0.8 m, one exit with reason jetpack), jetpack off and fall 3 s (back in:
 one entry); a dump near the end of every phase. de.swim has no kinematic oscillation and a teleport keeps the
 swim latch (no transition), so the crossing is the script's own (the ROADMAP's "jump out" case: jetpack + up)
 n4 a refill, 60 s at -30 m (30 s with --quick) with o2=dump every 5 s, then up to the surface and 20 s there (dumps
 every 5 s); the dive ends early when this or the next dump (by the last slope) is at or below --n4-min-o2 (8 %),
 so the character never suffocates
A refill = teleportChar to -3 m, swim up into the surface clamp until o2=dump reads >= 95 % (Subnautica: 30 units/s with
the eye above -1 m); one runs first in every session (the DE Lab character waits on SE's floor at -3,000 m, where
Subnautica's crush class uses 2 units/s). SpaceEngineers.cfg and WaterClientSettings.xml are hashed before and after (read-only).
Output tests/out/s2b/<stamp>/ (s2b-session.jsonl, se-*.jsonl, the SE game log, Pulsar info.log, the passive host's
log sn-passive-host.txt) and the S2B ORACLE TABLE of tests/s2b/analyze_swim.py (s2b-table.txt, s2b-results.json).
A stand-in dry run cannot exercise the swim controller (it needs SE's physics); the analyser's --selftest carries the
oracle. --se-cmd with tests/standins/fake_se_producer.py runs the plumbing only (de.swim unknown -> rows MISSING).

`s3` runs one scripted S3 "First block" session (ROADMAP S3 oracle C1-C14, G1-E2, N1-N3). S3a (default): Subnautica
and, in SE's place, the stand-in tests/standins/fake_se_producer.py --s3a --s2 started through the --se-cmd path (the
procedural 2.5 m cube with SE's real armor maps read in place, one PartAdd, a static pose from wire_ref; the --s2 view
machine gives Subnautica its Full recipe underwater); S3b: both games (Legacy.exe -continue, DE Lab). After ReadyIdle and
the in-world/hello wait, the parts run through the C11 harness (`--dry-run` lists them): place (deep grandReef spot:
Subnautica's layer-30 seabed probe, C7 frame times at P1 without and with the block, de.s3 count baseline, SE de.s3 place
with the block centre at the seabed, C7 mirror wait, Subnautica's own 600-frame measure with the mirror visible/hidden),
geometry (Subnautica's shear test at 1.0284 and 1.0345), views (Subnautica's lab run P1, P2, P4 with the reference
Seamoth), sun (de.time {t} 0.5 / 0.0, a lab run P1 each), n2 (SE GPS-marker pixels, Subnautica's S1 camera trace, the
0.5 Hz +-30 deg yaw sine through de.motion kind yaw), n3 (corner pixels: SE de.s3 pixel, Subnautica's S1 de.pixel), n1
(S3a: the glyph block), plate (S3b: a 3 x 3 wall with SE's colour export), shallow (the Safe Shallows anchor: lab run P3,
P3+1s with Subnautica's CPU time, a P3 burst), teardown (de.s3 teardown + count). Captures: Subnautica's lab runs (its
plan poses, S1 + S3 sidecars) under %LOCALAPPDATA%\\DeepEngineers\\captures\\s3\\<stamp>-<spot>[-<tag>]; where a part
needs the SE camera or no lab run exists (a stand-in), the harness flow: SE camera at the pose, 90 frames presenting its
SE tick, Subnautica's de.capture into captures\\s3\\<stamp> (SE's C10 relay as the fallback). The Subnautica-side S3
actions are one table (S3Runner.SN_ACTIONS: de.s3 {action: status | probe | shear | frametime | visible | placement |
count | teardown}, de.s3 {lab...}, de.time {t}, de.trace {on}) so their names change in one place; an action
Subnautica refuses as unknown leaves its rows MISSING. Seeded failures: --mutants or --mutant-group (S3_MUTANT_GROUPS, --list-mutant-groups): Subnautica's mutants
through DEEPENGINEERS_MUTANT, S3.M4 at the producer. Output tests/out/s3/<stamp>/ (s3-session.jsonl, the SE/SN logs,
LogOutput.log, Player.log, captures/) and the S3 ORACLE TABLE of tests/s3/analyze_s3.py (s3-table.txt,
s3-results.json, s3-image-manifest.json). Games are never piped and only closed by PID (CloseMainWindow; the stand-ins by
TerminateProcess).

`s4` runs one scripted S4 "Planet + terrain" session (ROADMAP S4 oracle; tests/s4/analyze_s4.py). S4se (default): SE
alone with the passive stand-in host (as s2b), Legacy.exe -continue (DE Lab, or with --world the throwaway copy that
LastSession.sbl points at); parts gate, tiles, n3, hash, at1, bake, drop, walk, n1, perf (+ at0b) through the SE
plugin's `de.terrain {action...}`; the async actions are awaited by their s4.* rows, read back from the plugin's JSONL
while the session runs (S4SeTail, read-only) and stored per part in s4-session.jsonl. S4both: both games (the S3b
recipe); parts gate, tiles, at2 (SE rays, then Subnautica's `de.s4` layer-30 probe at the SE columns with the SE hit as
the camera-hold hint), wm6 (the flattest spot near (0, -18, 0): SE ray + drop, the mirrored cube, Subnautica's hit), n1,
capture (the cube from 3 m). Throwaway-copy parts: n2 (every
edit path), g2e2save / g2e3b, g2e2noplugin, g2e5 (a copy without the world mod, alone), m4 / m10 (with S4.M4 / S4.M10).
Seeded failures: --mutants or --mutant-group (S4_MUTANT_GROUPS, --list-mutant-groups); S4.M1a-d are offline only.
--dry-run prints the plan with the source manifest measured now, the reference heights at the spots and the copy checks.
Reference heights: tools/worldmap/worldmap_ref.py OctreeColumns (the user's octrees, read in place). Output
tests/out/s4/<stamp>/ (s4-session.jsonl, se-*.jsonl, sn-*.jsonl, the game logs, captures/, s4-results.json,
s4-table.txt).

The layout and the Win32 helpers come from tests/wire/layout_py.py and section_py.py (looked up next to this file
first, then in the repository), so the launcher shares the one hand-written Python layout.
"""
from __future__ import annotations

import argparse
import collections
import datetime
import glob
import hashlib
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
import time
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(REPO, "tests", "wire")):
    if os.path.exists(os.path.join(_p, "layout_py.py")) and _p not in sys.path:
        sys.path.insert(0, _p)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402

try:
    import winreg
except ImportError:  # pragma: no cover
    winreg = None

# ----------------------------------------------------------------------------------------------------------------
# Paths (same defaults and overrides as Directory.Build.props / build.ps1)
# ----------------------------------------------------------------------------------------------------------------

SE_BIN64 = os.environ.get("DEEPENG_SE_BIN64") or r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64"
SUBNAUTICA = os.environ.get("DEEPENG_SUBNAUTICA") or r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"
PULSAR = os.environ.get("DEEPENG_PULSAR") or os.path.join(REPO, "third_party", "pulsar", "extracted")
LOCAL = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers")
LOG_DIR = os.path.join(LOCAL, "logs")
REGISTRY_BACKUP = os.path.join(LOCAL, "registry-backup.json")
REGISTRY_BACKUP_REPO = os.path.join(REPO, "docs", "install", "subnautica-screenmanager-backup.json")
MANIFEST = os.path.join(REPO, "docs", "install", "deepengineers-files.txt")
# S1 world mod (ROADMAP S1 "World mod (part 1)"): repository source and the folder tools/worldmod/deploy_worldmod.ps1
# writes; listed by `manifest` (read-only here) so the uninstall manifest names it (CONTRACT 7 Uninstall)
SE_APPDATA = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "SpaceEngineers")
WORLD_MOD_SRC = os.path.join(REPO, "src", "DeepEngineers.WorldMod")
WORLD_MOD_DST = os.path.join(SE_APPDATA, "Mods", "DeepEngineers.World")
BACKUPS = os.path.join(os.path.expanduser("~"), ".universal-modder", "backups")
# S9-installer: a release folder (<root>/launcher/launcher.py next to <root>/release.json) resolves its
# folders from the installer's paths.json or Steam instead of this PC's defaults above, and hands the plugins their
# environment inputs; in the repository (dev mode) every value above stays as it is. launcher_paths.py.
if HERE not in sys.path:
    sys.path.insert(0, HERE)
import launcher_paths as _LP  # noqa: E402
_PATHS = _LP.resolve(HERE)
RELEASE = _PATHS["mode"] == "release"
RELEASE_PLUGINS = _PATHS["plugins"]
BACKUP_HINT = _PATHS["backupHint"]
if RELEASE:
    SE_BIN64, SUBNAUTICA, PULSAR = _PATHS["seBin64"], _PATHS["subnautica"], _PATHS["pulsar"]
    REGISTRY_BACKUP_REPO, MANIFEST = _PATHS["registryBackupCopy"], _PATHS["manifest"]
    WORLD_MOD_SRC, BACKUPS = _PATHS["worldModSrc"], _PATHS["backups"]
    _LP.apply_release_env(_PATHS)
BACKUP_SETS = ("se-appdata", "subnautica-saves", "subnautica-locallow")
BACKUP_MAX_AGE_DAYS = 7   # S9 round 2: a set is renewed weekly, not every start
SN_REG_KEY = r"Software\Unknown Worlds\Subnautica"
GAME_PROCESSES = ("Legacy.exe", "SpaceEngineers.exe", "Subnautica.exe")
# Water Mod (Workshop 2200451495): the SE plugin's settings reflection is pinned to 3.23 (D58); another version makes
# DE.Site REFUSE "crushDamage unverifiable" after 600 ticks, so every S1+ session would be lost
WATER_MOD_DATA = os.environ.get("DEEPENG_WATER_MOD_DATA") or os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(SE_BIN64))), "workshop", "content", "244850", "2200451495", "Data",
    "Scripts", "Water", "Water", "WaterData.cs")
WATER_MOD_PINNED = "3.23"
CRASH_HANDLERS = ("UnityCrashHandler64.exe",)
CLOSE_WAIT_S = 45.0
GB = 1024 ** 3

def sn_saves_dir():
    return os.path.join(SUBNAUTICA, "SNAppData", "SavedGames")

def deploy_plan(configuration: str):
    """(source, destination) pairs: build.ps1's destinations."""
    se_out = os.path.join(REPO, "src", "DeepEngineers.SE", "bin", configuration, "net48")
    sn_out = os.path.join(REPO, "src", "DeepEngineers.SN", "bin", configuration, "net472")
    if RELEASE_PLUGINS:   # S9-installer: in a release folder its plugins/ are "the build"
        se_out = sn_out = RELEASE_PLUGINS
    se_dst = os.path.join(PULSAR, "Legacy", "Local", "DeepEngineers.SE")
    sn_dst = os.path.join(SUBNAUTICA, "BepInEx", "plugins", "DeepEngineers")
    plan = [(os.path.join(se_out, f), os.path.join(se_dst, f), f.endswith(".pdb"))
            for f in ("plugin.dll", "DeepEngineers.Wire.dll", "plugin.pdb")]
    plan += [(os.path.join(sn_out, f), os.path.join(sn_dst, f), f.endswith(".pdb"))
             for f in ("DeepEngineers.SN.dll", "DeepEngineers.Wire.dll", "DeepEngineers.SN.pdb")]
    return plan, se_dst, sn_dst

# ----------------------------------------------------------------------------------------------------------------
# Output and JSONL log
# ----------------------------------------------------------------------------------------------------------------

class Log:
    def __init__(self, command: str):
        self.path = None
        try:
            os.makedirs(LOG_DIR, exist_ok=True)
            self.path = os.path.join(LOG_DIR, "launcher-%s.jsonl" % datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
            self.f = open(self.path, "a", encoding="utf-8")
        except OSError:
            self.f = None
        self.event("command", command=command, argv=sys.argv[1:])

    def event(self, ev: str, **kw):
        if not self.f:
            return
        rec = {"utc": datetime.datetime.utcnow().isoformat(timespec="milliseconds") + "Z", "ev": ev}
        rec.update(kw)
        self.f.write(json.dumps(rec, default=str) + "\n")
        self.f.flush()

    def say(self, text: str, ev: str = "say", **kw):
        print(text, flush=True)
        self.event(ev, text=text, **kw)

def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()

# ----------------------------------------------------------------------------------------------------------------
# Processes
# ----------------------------------------------------------------------------------------------------------------

def processes_named(names) -> list:
    low = {n.lower() for n in names}
    return [p for p in S.list_processes() if p["name"].lower() in low]

def command_lines(pids) -> dict:
    """Command lines through CIM (PowerShell), only when needed (crash-form detection)."""
    if not pids:
        return {}
    flt = " or ".join("ProcessId=%d" % p for p in pids)
    ps = "Get-CimInstance Win32_Process -Filter '%s' | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress" % flt
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps], capture_output=True,
                             text=True, timeout=30).stdout.strip()
        data = json.loads(out) if out else []
        if isinstance(data, dict):
            data = [data]
        return {int(d["ProcessId"]): d.get("CommandLine") or "" for d in data}
    except (OSError, ValueError, subprocess.TimeoutExpired):
        return {}

def describe_process(p) -> str:
    wins = [w for w in S.top_level_windows(p["pid"]) if w[2]]
    zombie = not wins and p["threads"] <= 1
    return "%s pid %d (%d threads, %d visible windows%s)" % (p["name"], p["pid"], p["threads"], len(wins),
                                                             ", ZOMBIE signature" if zombie else "")

# ----------------------------------------------------------------------------------------------------------------
# Registry (Subnautica Screenmanager keys)
# ----------------------------------------------------------------------------------------------------------------

def read_screenmanager():
    """{value name: {"type": int, "data": json-able}} of the Screenmanager values (read-only)."""
    if winreg is None:
        return None
    try:
        k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, SN_REG_KEY, 0, winreg.KEY_READ)
    except OSError:
        return None
    out = {}
    try:
        i = 0
        while True:
            try:
                name, data, typ = winreg.EnumValue(k, i)
            except OSError:
                break
            i += 1
            if not name.lower().startswith("screenmanager"):
                continue
            if isinstance(data, bytes):
                data = {"hex": data.hex()}
            out[name] = {"type": typ, "data": data}
    finally:
        winreg.CloseKey(k)
    return out

def registry_backup_check(log: Log, write_backup: bool = True):
    cur = read_screenmanager()
    if cur is None:
        return "WARN", "Subnautica registry key HKCU\\%s not found (never started?)" % SN_REG_KEY
    if not os.path.exists(REGISTRY_BACKUP):
        if not write_backup:
            return "WARN", "no registry backup yet (%d Screenmanager values)" % len(cur)
        doc = {"key": "HKCU\\" + SN_REG_KEY, "created": datetime.datetime.now().isoformat(timespec="seconds"),
               "note": "Screenmanager values backed up by launcher.py at its first run (CONTRACT 1.1)",
               "values": cur}
        os.makedirs(os.path.dirname(REGISTRY_BACKUP), exist_ok=True)
        for path in (REGISTRY_BACKUP, REGISTRY_BACKUP_REPO):
            try:
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "w", encoding="utf-8") as f:
                    json.dump(doc, f, indent=1, sort_keys=True)
            except OSError as e:
                return "FAIL", "could not write the registry backup %s: %s" % (path, e)
        log.event("registry_backup", values=len(cur), path=REGISTRY_BACKUP)
        return "PASS", "registry backup created (%d Screenmanager values) -> %s and %s" % (
            len(cur), REGISTRY_BACKUP, REGISTRY_BACKUP_REPO)
    with open(REGISTRY_BACKUP, encoding="utf-8") as f:
        saved = json.load(f)["values"]
    diff = sorted(set(k for k in set(cur) | set(saved) if cur.get(k) != saved.get(k)))
    if diff:
        # S2 real run: the SN plugin's rect correction (Screen.SetResolution to the primary monitor's
 # native size, FullScreenWindow) is persisted by Unity itself in these three values at quit. That is the state
 # S2 needs (rectOk), so it is accepted here; the backup keeps the project's original values for the uninstall.
        try:
            import ctypes
            native = (ctypes.windll.user32.GetSystemMetrics(0), ctypes.windll.user32.GetSystemMetrics(1))
        except Exception:
            native = None
        rect_keys = {"Screenmanager Resolution Width_h182942802": native[0] if native else None,
                     "Screenmanager Resolution Height_h2627697771": native[1] if native else None,
                     "Screenmanager Resolution Use Native_h1405027254": 1}
        if native and set(diff) <= set(rect_keys) and all((cur.get(k) or {}).get("data") == v for k, v in rect_keys.items()):
            return "PASS", "Screenmanager resolution values are the primary monitor's native %dx%d (set by the S2 rect " \
                           "correction, persisted by Unity); the backup keeps the owner's %s for the uninstall" % (
                               native[0], native[1], ", ".join(str((saved.get(k) or {}).get("data")) for k in sorted(rect_keys)))
        # (second PC / friend): Subnautica rewrites these values whenever it is started on its own; the backup
 # keeps the originals for the uninstall and the SN plugin corrects the window at every start, so a difference
 # is information, not a reason to refuse play
        return "WARN", "Screenmanager values differ from the backup: %s (Subnautica rewrote them; the backup keeps the "                        "originals for the uninstall; restore-registry --yes puts them back now)" % (
            ", ".join(diff[:6]) + (" ..." if len(diff) > 6 else ""))
    return "PASS", "Screenmanager values equal to the backup (%d values)" % len(cur)

def restore_registry(log: Log, yes: bool) -> int:
    if not os.path.exists(REGISTRY_BACKUP):
        log.say("no registry backup at %s" % REGISTRY_BACKUP)
        return 1
    with open(REGISTRY_BACKUP, encoding="utf-8") as f:
        saved = json.load(f)["values"]
    cur = read_screenmanager() or {}
    diff = sorted(k for k in saved if cur.get(k) != saved[k])
    if not diff:
        log.say("nothing to restore: the Screenmanager values equal the backup")
        return 0
    log.say("values to restore: %s" % ", ".join(diff))
    if not yes:
        log.say("dry run (pass --yes to write them)")
        return 0
    if processes_named(("Subnautica.exe",)):
        log.say("Subnautica is running: close it first")
        return 1
    k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, SN_REG_KEY, 0, winreg.KEY_SET_VALUE)
    try:
        for name in diff:
            v = saved[name]
            data = bytes.fromhex(v["data"]["hex"]) if isinstance(v["data"], dict) else v["data"]
            winreg.SetValueEx(k, name, 0, v["type"], data)
            log.event("registry_restore", name=name)
    finally:
        winreg.CloseKey(k)
    log.say("restored %d values" % len(diff))
    return 0

# ----------------------------------------------------------------------------------------------------------------
# Backups
# ----------------------------------------------------------------------------------------------------------------

def backup_time(path: str):
    base = os.path.splitext(os.path.basename(path))[0]
    try:
        return datetime.datetime.strptime(base, "%Y%m%d-%H%M%S")
    except ValueError:
        return datetime.datetime.fromtimestamp(os.path.getmtime(path))

def latest_backup(name: str):
    files = glob.glob(os.path.join(BACKUPS, name, "*.zip"))
    if not files:
        return None, None
    best = max(files, key=backup_time)
    return best, backup_time(best)

REQUIRED_WORLD_MODS = (("DeepEngineers.World", "DeepEngineers.World", "0"), ("Water Mod", "2200451495.sbm", "2200451495"), ("Text HUD API", "758597413.sbm", "758597413"))   # (friendly, Name, PublishedFileId)
WORLD_MOD_WORKSHOP_ID = "3826432715"   # the world mod as published by the project: a published world lists it by id instead of the local folder

def last_session_mods():
    """: the mod list of the world SE's -continue loads
 (LastSession.sbl under Saves/<steamid>, then that world's Sandbox_config.sbc, the file SE's menu writes). Returns
 (world name, [(friendly, name, publishedFileId)], error) with error set when nothing could be read."""
    try:
        hits = glob.glob(os.path.join(SE_APPDATA, "Saves", "*", "LastSession.sbl"))
        if not hits:
            return None, [], "no LastSession.sbl under %s" % os.path.join(SE_APPDATA, "Saves")
        with open(max(hits, key=os.path.getmtime), encoding="utf-8-sig", errors="replace") as f:
            m = re.search(r"<Path>([^<]*)</Path>", f.read())
        if not m:
            return None, [], "LastSession.sbl without a <Path>"
        folder = m.group(1).rstrip("\\/")
        with open(os.path.join(folder, "Sandbox_config.sbc"), encoding="utf-8-sig", errors="replace") as f:
            txt = f.read()
        mods = []
        for item in re.finditer(r"<ModItem\b([^>]*)>(.*?)</ModItem>", txt, re.S):
            head, body = item.group(1), item.group(2)
            fm = re.search(r'FriendlyName="([^"]*)"', head)
            nm = re.search(r"<Name>([^<]*)</Name>", body)
            pm = re.search(r"<PublishedFileId>([^<]*)</PublishedFileId>", body)
            mods.append(((fm.group(1) if fm else "") or (nm.group(1) if nm else "?"), nm.group(1) if nm else "", pm.group(1).strip() if pm else "0"))
        return os.path.basename(folder), mods, None
    except (OSError, ValueError) as e:
        return None, [], "%s: %s" % (type(e).__name__, e)

def last_session_time():
    """The newest `session_start` of a real session in our launcher JSONL logs (None: no DE session yet). Runs with
 stand-in commands (--sn-cmd/--se-cmd) or another section name are tests and do not count."""
    best = None
    for path in glob.glob(os.path.join(LOG_DIR, "launcher-*.jsonl")):
        try:
            with open(path, encoding="utf-8") as f:
                for line in f:
                    if '"session_start"' not in line:
                        continue
                    rec = json.loads(line)
                    if rec.get("standin") or rec.get("name") != L.SECTION_NAME:
                        continue
                    utc = rec["utc"].rstrip("Z")
                    t = datetime.datetime.fromisoformat(utc).replace(tzinfo=datetime.timezone.utc).astimezone()
                    t = t.replace(tzinfo=None)
                    best = t if best is None or t > best else best
        except (OSError, ValueError, KeyError):
            continue
    return best

def create_saves_backup(log: Log):
    """A `um backup`-compatible zip of <Subnautica>\\SNAppData\\SavedGames (relative paths + _um_manifest.json)."""
    src = sn_saves_dir()
    if not os.path.isdir(src):
        return None, "no saves folder %s" % src
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    dst_dir = os.path.join(BACKUPS, "subnautica-saves")
    os.makedirs(dst_dir, exist_ok=True)
    dst = os.path.join(dst_dir, stamp + ".zip")
    files = {}
    tmp = dst + ".part"
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _dirs, names in os.walk(src):
            for n in sorted(names):
                full = os.path.join(root, n)
                rel = os.path.relpath(full, src).replace(os.sep, "/")
                with open(full, "rb") as f:
                    data = f.read()
                files[rel] = {"size": len(data), "sha1": hashlib.sha1(data).hexdigest()}
                z.writestr(rel, data)
        z.writestr("_um_manifest.json", json.dumps({"source": src, "created": stamp,
                                                    "note": "created by DeepEngineers launcher.py preflight",
                                                    "files": files}, indent=1))
    os.replace(tmp, dst)
    log.event("backup_created", set="subnautica-saves", path=dst, files=len(files))
    return dst, "%d files" % len(files)

# ----------------------------------------------------------------------------------------------------------------
# preflight
# ----------------------------------------------------------------------------------------------------------------

def preflight(log: Log, a) -> int:
    results = []

    def res(status, check, text):
        results.append((status, check, text))
        log.say("%-4s %-12s %s" % (status, check, text), ev="preflight", status=status, check=check)

    # 1. game processes, crash handlers, crash forms, zombies
    games = processes_named(GAME_PROCESSES)
    handlers = processes_named(CRASH_HANDLERS)
    cl = command_lines([p["pid"] for p in games if p["name"].lower() == "spaceengineers.exe"])
    if games:
        res("FAIL", "processes", "game processes alive: %s" % "; ".join(
            describe_process(p) + (" CRASH FORM (-report): close it" if "-report" in cl.get(p["pid"], "") else "")
            for p in games))
    else:
        res("PASS", "processes", "no Legacy, SpaceEngineers or Subnautica process")
    if handlers:
        res("FAIL", "crashhandler", "stale crash handlers: %s (close them by exact PID: launcher.py stop does not; "
                                    "use `um win kill <pid>` with the owner's confirmation)" %
            "; ".join(describe_process(p) for p in handlers))
    else:
        res("PASS", "crashhandler", "no UnityCrashHandler64")
    # 2. Steam
    steam = processes_named(("steam.exe",))
    res("PASS" if steam else "FAIL", "steam", "Steam %s" % ("running (pid %d)" % steam[0]["pid"] if steam else
                                                           "not running (SE needs it)"))
    # 3. memory: information only
    m = S.memory_status()
    tot = m.get("total")
    res("INFO", "memory", "%.1f GB available" % (m["available"] / GB) + (" of %.0f GB" % (tot / GB) if tot else ""))
    # 4. backups: S9 round 2: a backup set is fine while its newest zip is at most
 # BACKUP_MAX_AGE_DAYS old; older is a WARN (the launch goes on), none at all a FAIL; the old "newer than the last DE
 # session" rule made every start zip SE's AppData
    last = last_session_time()
    for name in BACKUP_SETS:
        path, t = latest_backup(name)
        stale = path is not None and (datetime.datetime.now() - t).total_seconds() > BACKUP_MAX_AGE_DAYS * 86400
        if (path is None or (stale and a.refresh_saves_backup)) and name == "subnautica-saves" and \
                not a.no_create_backup:
            dst, info = create_saves_backup(log)
            if dst:
                res("PASS", "backup", "%s: created %s (%s)" % (name, dst, info))
            else:
                res("FAIL", "backup", "%s: missing and not created (%s)" % (name, info))
            continue
        if path is None:
            res("FAIL", "backup", "%s: no backup in %s (%s)" % (name, BACKUPS, BACKUP_HINT % name))  # S9-installer hint
        elif stale:
            res("WARN", "backup", "%s: newest %s is %.0f days old (over %d; %s%s)" % (
                name, os.path.basename(path), (datetime.datetime.now() - t).total_seconds() / 86400, BACKUP_MAX_AGE_DAYS,
                BACKUP_HINT % name, "; or preflight --refresh-saves-backup" if name == "subnautica-saves" else ""))
        else:
            res("PASS", "backup", "%s: %s (%.1f days old, under %d%s)" % (
                name, os.path.basename(path), (datetime.datetime.now() - t).total_seconds() / 86400, BACKUP_MAX_AGE_DAYS,
                "; last DE session %s" % last.isoformat(timespec="seconds") if last else ""))
    # 5. the section must be absent
    state, pids, note = S.section_holders(a.name)
    if state == "absent":
        res("PASS", "section", "%s absent (ERROR_FILE_NOT_FOUND)" % a.name)
    else:
        names = {p["pid"]: p["name"] for p in S.list_processes()}
        holders = ", ".join("%d %s" % (p, names.get(p, "?")) for p in pids) if pids else (
            "no other process holds a handle (a mapped view without a handle keeps it alive)" if pids == [] else
            "holders unknown")
        res("FAIL", "section", "%s %s: %s%s" % (a.name, state, holders, (" (%s)" % note) if note else ""))
    # 6. registry
    st, text = registry_backup_check(log)
    res(st, "registry", text)
    # 7. the world's mods
    world, mods, err = last_session_mods()
    if err:
        res("WARN", "world.mods", "mod list not read (%s): SE's own world picker decides" % err)
    else:
        missing = [friendly for friendly, name, pid in REQUIRED_WORLD_MODS
                   if not any((pid != "0" and m[2] == pid) or (pid == "0" and (m[1] == name or m[2] == WORLD_MOD_WORKSHOP_ID)) for m in mods)]
        req_names = {n for _f, n, _p in REQUIRED_WORLD_MODS}
        req_ids = {p for _f, _n, p in REQUIRED_WORLD_MODS if p != "0"} | {WORLD_MOD_WORKSHOP_ID}
        others = [m[0] for m in mods if m[1] not in req_names and m[2] not in req_ids]
        text = "world %r: %d mod(s)%s" % (world, len(mods), ("; the player's own: " + ", ".join(others)) if others else "; no other mod")
        if missing:
            # a plain SE world as the last session is the player's choice:
 # the menu opens as usual; only -continue would load it without the sea, so a WARN, never a refusal
            res("WARN", "world.mods", text + "; not a DeepEngineers world (missing " + ", ".join(missing) + "): fine at the menu; with --continue it loads without the sea")
        else:
            res("PASS", "world.mods", text)
    # 6b. Water Mod version (the plugin's pin)
    st, text = water_mod_version_check()
    res(st, "watermod", text)
    # 6c. SE window mode
    st, text = se_window_mode_check()
    res(st, "windowmode", text)
    # 7. elevation and deployment state (information)
    el, rid = S.token_elevation(S.current_pid())
    res("INFO", "elevation", "launcher elevated=%s integrity=0x%x" % (el, rid or 0))
    plan, _se, _sn = deploy_plan(a.configuration)
    stale_dep = []
    for src, dst, optional in plan:
        if os.path.exists(src) and os.path.exists(dst) and sha256_file(src) != sha256_file(dst):
            stale_dep.append(tokenise(dst))
        elif not os.path.exists(dst) and not optional:
            stale_dep.append("%s (missing)" % os.path.basename(dst))
    #: the build that runs, by the first 8 hex digits of each deployed plugin's SHA-256 (the release zip keeps the
 # same name from build to build, so the log says which one was installed)
    stamp = ", ".join("%s %s" % (os.path.basename(dst), sha256_file(dst)[:8]) for _src, dst, _opt in plan
                      if os.path.basename(dst).lower() in ("plugin.dll", "deepengineers.se.dll", "deepengineers.sn.dll") and os.path.isfile(dst))
    res("WARN" if stale_dep else "PASS", "deployed", ("deployed files differ from the %s build: %s" % (
        a.configuration, ", ".join(stale_dep)) if stale_dep else "deployed files equal the %s build" % a.configuration) +
        (" (%s)" % stamp if stamp else ""))
    fails = [r for r in results if r[0] == "FAIL"]
    log.say("PREFLIGHT %s (%d FAIL, %d WARN)" % ("PASS" if not fails else "FAIL", len(fails),
                                                sum(1 for r in results if r[0] == "WARN")), ev="preflight_result",
            ok=not fails)
    return 0 if not fails else 1

def se_window_mode_check():
    """SpaceEngineers.cfg WindowMode (read-only): 1 "Fullscreen window" PASS; 2 exclusive "Fullscreen" FAIL (the desktop
 never composites it: no see-through, and the A' fallback minimises SE); 0 Window WARN; absent WARN."""
    try:
        with open(SE_CFG, encoding="utf-8-sig", errors="replace") as f:
            text = f.read()
    except OSError:
        return "WARN", "SpaceEngineers.cfg not readable: cannot check the window mode (needs \"Fullscreen window\")"
    m = re.search(r"<Key>WindowMode</Key>\s*<Value[^>]*>\s*(?:<Value[^>]*>)?\s*([0-9]+)", text, re.I)
    if not m:
        return "WARN", "SpaceEngineers.cfg has no WindowMode: set Options > Display > Window mode to \"Fullscreen window\""
    mode = m.group(1)
    if mode == "1":
        return "PASS", "Space Engineers window mode: Fullscreen window"
    if mode == "2":
        return "FAIL", "Space Engineers window mode is Fullscreen (exclusive): set Options > Display > Window mode to "                        "\"Fullscreen window\" in SE, then start again (exclusive fullscreen cannot show Subnautica "                        "through SE's window, and the fallback mode minimises SE)"
    return "WARN", "Space Engineers window mode is %s (0 = Window): \"Fullscreen window\" is the mode DeepEngineers "                    "is built for" % mode

def water_mod_version_check():
    """The Workshop copy's `public const string Version = "x"` in WaterData.cs (read-only) against the plugin's pin."""
    try:
        with open(WATER_MOD_DATA, encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError as e:
        return "WARN", "Water Mod WaterData.cs not readable (%s): check /wversion in game is %s" % (e, WATER_MOD_PINNED)
    m = re.search(r'Version\s*=\s*"([^"]+)"', text)
    if not m:
        return "WARN", "Water Mod version not found in %s: check it is %s" % (WATER_MOD_DATA, WATER_MOD_PINNED)
    if m.group(1) != WATER_MOD_PINNED:
        return "FAIL", ("Water Mod %s != pinned %s: the SE plugin will refuse the site (DE.Site REFUSE crushDamage "
                        "unverifiable, D58); re-pin and re-verify WaterModClient against the new source first") % (
            m.group(1), WATER_MOD_PINNED)
    return "PASS", "Water Mod %s (pinned; Workshop copy %s)" % (m.group(1), WATER_MOD_DATA)

# ----------------------------------------------------------------------------------------------------------------
# deploy and manifest
# ----------------------------------------------------------------------------------------------------------------

def tokenise(path: str) -> str:
    for root, token in ((SUBNAUTICA, "<Subnautica>"), (PULSAR, "<Pulsar>"), (REPO, "<repo>"), (SE_APPDATA, "<SEAppData>")):
        r = os.path.normcase(os.path.normpath(root))
        if os.path.normcase(os.path.normpath(path)).startswith(r + os.sep):
            return token + path[len(os.path.normpath(root)):]
    return path

def write_manifest(log: Log, configuration: str, deployed_now: bool) -> int:
    plan, se_dst, sn_dst = deploy_plan(configuration)
    now = datetime.datetime.utcnow().isoformat(timespec="seconds") + "Z"
    lines = [
        "# DeepEngineers install manifest: every file and entry we create or change in a game or loader folder",
        "# (CONTRACT 7 Uninstall, ROADMAP 5.1/5.5). Written by src/DeepEngineers.Launcher/launcher.py (deploy | manifest);",
        "# docs/install/UNINSTALL.md is drafted from it. The loaders themselves are in subnautica-bepinex-files.txt.",
        "# Root tokens: <Subnautica> = %s" % SUBNAUTICA,
        "#              <Pulsar>     = %s (portable, gitignored; wiped by a Pulsar self-update)" % PULSAR,
        "#              <SEAppData>  = %s (S1: the local world mod, deployed by tools/worldmod/deploy_worldmod.ps1)" %
        SE_APPDATA,
        "# Columns (tab-separated): kind, path, bytes, sha256, source (build output), state, recorded (UTC).",
        "# kind: dir = folder we create; file = file we copy; entry = a line we add to a loader file;",
        "#       created-by-loader = a file the loader writes for our plugin.",
        "# recorded %s by `launcher.py %s`" % (now, "deploy" if deployed_now else "manifest"),
    ]
    rows = []
    for d in (sn_dst, se_dst):
        rows.append(("dir", tokenise(d) + "\\", "-", "-", "-", "present" if os.path.isdir(d) else "absent"))
    for src, dst, optional in plan:
        if os.path.exists(dst):
            same = os.path.exists(src) and sha256_file(src) == sha256_file(dst)
            rows.append(("file", tokenise(dst), str(os.path.getsize(dst)), sha256_file(dst), tokenise(src),
                         "deployed (= build)" if same else ("deployed (build differs)" if os.path.exists(src)
                                                            else "deployed (no build output)")))
        else:
            rows.append(("file", tokenise(dst), "-", "-", tokenise(src), "absent" + (" (optional)" if optional else "")))
    profile = os.path.join(PULSAR, "Legacy", "Profiles", "Current.xml")
    has = False
    if os.path.exists(profile):
        with open(profile, encoding="utf-8-sig") as f:
            has = "<string>DeepEngineers.SE.dll</string>" in f.read()
    rows.append(("entry", tokenise(profile), "-", "-", "<Local><string>DeepEngineers.SE.dll</string></Local> "
                 "(written by build.ps1)", "present" if has else "absent"))
    cfg = os.path.join(SUBNAUTICA, "BepInEx", "config", "deepengineers.sn.cfg")
    rows.append(("created-by-loader", tokenise(cfg), str(os.path.getsize(cfg)) if os.path.exists(cfg) else "-", "-",
                 "BepInEx config of deepengineers.sn", "present" if os.path.exists(cfg) else "absent (not yet)"))
    # S1: the local world mod in SE's user folder (deployed by tools/worldmod/deploy_worldmod.ps1, not by build.ps1)
    rows.append(("dir", tokenise(WORLD_MOD_DST) + "\\", "-", "-", "-",
                 "present" if os.path.isdir(WORLD_MOD_DST) else "absent"))
    for base, _dirs, files in sorted(os.walk(WORLD_MOD_SRC)):
        for n in sorted(files):
            src = os.path.join(base, n)
            dst = os.path.join(WORLD_MOD_DST, os.path.relpath(src, WORLD_MOD_SRC))
            if os.path.exists(dst):
                same = sha256_file(src) == sha256_file(dst)
                rows.append(("file", tokenise(dst), str(os.path.getsize(dst)), sha256_file(dst), tokenise(src),
                             "deployed (= source)" if same else "deployed (source differs)"))
            else:
                rows.append(("file", tokenise(dst), "-", "-", tokenise(src), "absent"))
    body = "\n".join(lines + ["\t".join(r + (now,)) for r in rows]) + "\n"
    os.makedirs(os.path.dirname(MANIFEST), exist_ok=True)
    with open(MANIFEST, "w", encoding="utf-8", newline="\n") as f:
        f.write(body)
    log.say("manifest written: %s (%d rows)" % (MANIFEST, len(rows)), ev="manifest")
    for r in rows:
        log.say("  %-17s %-70s %s" % (r[0], r[1], r[5]))
    return 0

def deploy(log: Log, a) -> int:
    running = processes_named(GAME_PROCESSES)
    if running and not a.dry_run:
        log.say("REFUSE deploy: %s running (the loaders hold the DLLs open)" % ", ".join(
            "%s %d" % (p["name"], p["pid"]) for p in running))
        return 1
    plan, se_dst, sn_dst = deploy_plan(a.configuration)
    missing = [src for src, _d, opt in plan if not opt and not os.path.exists(src)]
    if missing:
        log.say("build output missing (run build.ps1 -NoDeploy or dotnet build -c %s): %s" % (
            a.configuration, ", ".join(missing)))
        return 1
    wires = [src for src, _d, _o in plan if os.path.basename(src) == "DeepEngineers.Wire.dll"]
    if len({sha256_file(w) for w in wires}) != 1:
        log.say("WARN the two DeepEngineers.Wire.dll build outputs differ (both copies should come from one build)")
    for src, dst, optional in plan:
        if not os.path.exists(src):
            continue
        h = sha256_file(src)
        log.say("%s %s -> %s sha256 %s" % ("would copy" if a.dry_run else "copy", tokenise(src), tokenise(dst), h),
                ev="deploy_file", src=src, dst=dst, sha256=h, dry=a.dry_run)
        if not a.dry_run:
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
            if sha256_file(dst) != h:
                log.say("FAIL copy verification %s" % dst)
                return 1
    if a.dry_run:
        log.say("dry run: nothing written")
        return 0
    return write_manifest(log, a.configuration, deployed_now=True)

# ----------------------------------------------------------------------------------------------------------------
# run
# ----------------------------------------------------------------------------------------------------------------

def popen_detached(cmd, cwd, env, stdout_path=None):
    """Starts a game (or, in stand-in tests, a Python stand-in whose stdout JSONL goes to `stdout_path`)."""
    flags = 0
    if cmd and cmd[0].lower().endswith("python.exe"):
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    if stdout_path:
        with open(stdout_path, "ab") as out, open(stdout_path + ".err", "ab") as err:
            return subprocess.Popen(cmd, cwd=cwd, env=env, creationflags=flags, stdin=subprocess.DEVNULL, stdout=out,
                                    stderr=err)
    return subprocess.Popen(cmd, cwd=cwd, env=env, creationflags=flags, stdin=subprocess.DEVNULL,
                            stdout=subprocess.DEVNULL if flags else None, stderr=subprocess.DEVNULL if flags else None)

def wait_ready(log: Log, name: str, ready_phase: int, timeout: float, sn_proc, check_pid: bool = True) -> bool:
    """Waits (open only, read-only) for magic, snPid = the started process and snPhase >= the ready phase (Quitting
 never counts); closes its handle. Never creates. Booting/Menu/LoadingSave keep waiting; None after a started bridge
 (the plugin withdrew) or the process exiting ends the wait. Logs the time to the ready phase (CONTRACT O11)."""
    t0 = time.monotonic()
    last = None
    seen_started = False
    sec = None
    extended = False
    creating_marker = os.path.join(LOCAL, "hostsave-creating.txt")   # S9 round 2: written by HostSave.StartNewFreedom
    try:
        while time.monotonic() - t0 < timeout:
            if not extended and os.path.isfile(creating_marker):
                extended = True
                timeout = max(timeout, HOSTSAVE_CREATE_WAIT_S)
                log.say("Subnautica is making its Freedom host save (first start: a new game, its intro once, one save): "
                        "waiting up to %.0f s" % timeout, ev="sn_hostsave_creating")
            if sn_proc.poll() is not None:
                log.say("Subnautica exited (code %s) before the ready phase" % sn_proc.returncode, ev="sn_exit_early")
                return False
            if sec is None:
                sec = S.Section.open(name, readonly=True)
            if sec is not None and sec.mapped_size >= L.HEADER_SIZE:
                magic, phase = sec.hdr("magic"), sec.hdr("snPhase")
                pid, gen = sec.identity("sn")
                state = (magic == L.MAGIC, phase, pid)
                if state != last:
                    last = state
                    log.say("  snPhase=%s (magic %s, snPid %d%s, generation %d) after %.1f s" % (
                        L.SN_PHASES.get(phase, phase), "ok" if magic == L.MAGIC else hex(magic), pid,
                        "" if not check_pid or pid in (0, sn_proc.pid) else " != started pid %d" % sn_proc.pid, gen,
                        time.monotonic() - t0), ev="sn_phase", phase=phase, snPid=pid)
                ours = not check_pid or pid == sn_proc.pid
                if magic == L.MAGIC and ours and phase != L.SN_PHASE["None"]:
                    seen_started = True
                if magic == L.MAGIC and ours and phase >= ready_phase and phase != L.SN_PHASE["Quitting"]:
                    secs = time.monotonic() - t0
                    log.say("Subnautica ready (%s) after %.1f s (CONTRACT O11: record this time)" % (
                        L.SN_PHASES.get(phase, phase), secs), ev="sn_ready", seconds=round(secs, 3), phase=phase)
                    return True
                if seen_started and ours and phase in (L.SN_PHASE["None"], L.SN_PHASE["Quitting"]):
                    log.say("Subnautica's bridge went to %s before the ready phase (withdrawn or quitting): see "
                            "LogOutput.log" % L.SN_PHASES[phase], ev="sn_withdrawn", phase=phase)
                    return False
            time.sleep(0.25)
        log.say("ready wait timed out after %.0f s (last snPhase %s)" % (
            timeout, L.SN_PHASES.get(last[1], last[1]) if last else "section absent"), ev="ready_timeout")
        return False
    finally:
        if sec:
            sec.close()

def elevation_text(pid):
    el, rid = S.token_elevation(pid)
    return el, rid, "elevated=%s integrity=0x%x" % (el, rid or 0)

def session_commands(a):
    sn_cmd = json.loads(a.sn_cmd) if a.sn_cmd else [os.path.join(SUBNAUTICA, "Subnautica.exe"), "-monitor", "1"]
    se_cmd = json.loads(a.se_cmd) if a.se_cmd else [os.path.join(PULSAR, "Legacy.exe"), "-noUpdate", "-noSplash",
                                                    "-noPrompt", "-bin64", SE_BIN64] + (["-continue"] if a.cont else [])
    return sn_cmd, se_cmd

def start_games(log: Log, a, stdout_dir: str = None, sn_extra_env=None, se_extra_env=None):
    """Preflight (unless skipped), Subnautica with the bridge token, the ready wait, SE as a child, ASFW, elevation.
 Returns (rc, sn, se): rc is None when both were started; otherwise the exit code (sn may be running).
 sn_extra_env / se_extra_env (s3: the seeded failures) go into that side's environment only."""
    name = a.name
    _sn_mutex, _se_mutex, launcher_object = L.mutex_names(name)
    sn_cmd, se_cmd = session_commands(a)
    sn_cwd = SUBNAUTICA if not a.sn_cmd else REPO
    se_cwd = PULSAR if not a.se_cmd else REPO
    ready_phase = L.SN_PHASE[a.ready_phase]
    if not a.skip_preflight:
        if preflight(log, a) != 0:
            log.say("REFUSE run: preflight failed (fix it, or --skip-preflight for stand-in tests)")
            return 1, None, None
    for exe in (sn_cmd[0], se_cmd[0]):
        if not os.path.exists(exe) and not shutil.which(exe):
            log.say("REFUSE run: %s not found" % exe)
            return 1, None, None
    log.event("session_start", name=name, standin=bool(a.sn_cmd or a.se_cmd))
    token = S.NamedObjectHolder(launcher_object)
    env = dict(os.environ)
    env[L.BRIDGE_ENV] = "1"
    # S11 hook: free play (`run`) has creature combat again: the S6 proxies, the character
 # capsule and the damage forwarding were off in `run` since the S6 integration, so creatures swam through ships and the
 # character there; scripted pre-S11 sessions keep S6 and S11 off unless their own runner sets them on
    _play = getattr(a, "cmd", None) == "run"
    env.setdefault("DEEPENGINEERS_S6", "on" if _play else "off")   # integrator S6: pre-S6 sessions without S6; s6 sets it on
    env.setdefault("DEEPENGINEERS_S11", "on" if _play else "off")   # S11 hook: s11 sets it on
    if _play:
        #: S8a (the planet window, the drill layer, the ores) was on
 # only when the project's own shell exported DEEPENGINEERS_S8A=on; a fresh shell left it unset and S4's guards blocked
 # every voxel edit. Free play (`run`) sets it on both sides; scripted sessions keep their runner's choice.
        env.setdefault("DEEPENGINEERS_S8A", "on")
    if _play and not (getattr(a, "sn_cmd", None) or getattr(a, "se_cmd", None)):   # S11  restart-renderer restarts with what this session had
        import launcher_s8 as _l8   # noqa: E402
        _l8.record_play_env(sys.modules[__name__], {k: env.get(k) for k in _l8.PLAY_ENV_VARS})
    env.update(sn_extra_env or {})
    # S10 round 2 C (loading): free play starts SE right after Subnautica instead of after its ready wait; SE opens the bridge section once a second and Subnautica creates it
 # when ready, so the order does not matter to the link. Env DEEPENGINEERS_S10_PARALLEL=off restores the sequential start;
 # scripted sessions keep it.
    _parallel = _play and not (getattr(a, "sn_cmd", None) or getattr(a, "se_cmd", None)) and \
        os.environ.get("DEEPENGINEERS_S10_PARALLEL", "on").strip().lower() not in ("off", "0", "false")
    se = None
    def _start_se():
        """SE as a child, ASFW, the elevation check (the same steps as before; S10 round 2 C may call it early)."""
        se_env = dict(os.environ)
        se_env.pop(L.BRIDGE_ENV, None)
        # review S7a F2: under the Site picture ships, astronaut and placement ghost above water come only from the
 # S5 mirror, so `run` (free play) and s7a keep it on; the pre-S5 scripted sessions run without it (s5/tp1 set it)
        se_env.setdefault("DEEPENGINEERS_S5", "on" if getattr(a, "cmd", None) in ("run", "s7a") else "off")
        se_env.setdefault("DEEPENGINEERS_S6", "on" if _play else "off")   # integrator S6 / S11 hook: `run` with S6 (proxies, C8); pre-S6 sessions without; s6 sets it on
        se_env.setdefault("DEEPENGINEERS_S11", "on" if _play else "off")   # S11 hook: `run` with S11; s11 sets it on
        se_env.setdefault("DEEPENGINEERS_WATERLOOK", "off")   #: scripted sessions keep the contract's flat water; `run` passes on
 # S8a: the designed planet's ring reaches 12 km and the window
 # universe ends at 7.6 km, so free play (`run`) moves the chart with the camera (the treadmill, S8a.N8's oracle) unless the
 # environment says otherwise; scripted sessions keep chart 0 unless their runner sets it (s8a --treadmill)
        se_env.setdefault("DEEPENGINEERS_S8A_TREADMILL", "on" if _play else "off")
        if _play:
            se_env.setdefault("DEEPENGINEERS_S8A", "on")   #: see the Subnautica side above (drill and window default on with it)
        if getattr(a, "cmd", None) not in ("run", "s7a"):
            # integrator S7a: the pre-S7a scripted sessions (s1-s6, tp1 without --picture) keep S2's picture policy, which their
 # oracles describe; `run` and s7a use the plugin default (site); `tp1 --picture` sets it in os.environ first
            se_env.setdefault("DEEPENGINEERS_PICTURE", "waterline")
            # review S7a F3: SE's time sync holds Subnautica's clock (LabScene refuses de.time: the S3b sun parts and S3.C9 captures
 # would run at SE's time) and the ghost streams a synthetic grid; neither existed for those oracles
            se_env.setdefault("DEEPENGINEERS_TIMESYNC", "off")
            se_env.setdefault("DEEPENGINEERS_GHOST", "off")
        se_env.update(se_extra_env or {})
        se = popen_detached(se_cmd, se_cwd, se_env, os.path.join(stdout_dir, "se-standin.jsonl")
                            if stdout_dir and a.se_cmd else None)
        log.say("SE started: pid %d" % se.pid, ev="se_started", pid=se.pid)
        ok, err = S.allow_set_foreground_window(se.pid)
        log.say("AllowSetForegroundWindow(%d): %s" % (se.pid, "ok" if ok else "error %d" % err), ev="asfw", ok=ok)
        el_se = elevation_text(se.pid)
        el_sn = elevation_text(sn.pid)
        if el_se[0] is not None and el_sn[0] is not None and (el_se[0], el_se[1]) != (el_sn[0], el_sn[1]):
            log.say("REFUSE mixed elevation: SE %s, Subnautica %s. Close both normally (launcher.py stop)." % (
                el_se[2], el_sn[2]), ev="refuse_elevation")
        return se

    sn = None
    try:
        sn = popen_detached(sn_cmd, sn_cwd, env, os.path.join(stdout_dir, "sn-standin.jsonl")
                            if stdout_dir and a.sn_cmd else None)
        log.say("Subnautica started: pid %d" % sn.pid, ev="sn_started", pid=sn.pid)
        el_me = elevation_text(S.current_pid())
        el_sn = elevation_text(sn.pid)
        if el_sn[0] is not None and (el_sn[0], el_sn[1]) != (el_me[0], el_me[1]):
            log.say("REFUSE mixed elevation: launcher %s, Subnautica %s; SE not started (close Subnautica "
                    "normally; never killed)" % (el_me[2], el_sn[2]), ev="refuse_elevation")
            return 1, sn, None
        if _parallel:
            se = _start_se()
            log.say("S10: SE started in parallel with Subnautica's start (DEEPENGINEERS_S10_PARALLEL=off: the old order)", ev="s10_parallel_start")
        ready = wait_ready(log, name, ready_phase, a.timeout, sn, check_pid=not getattr(a, "any_sn_pid", False))
        if _parallel:
            if not ready:
                log.say("Subnautica not ready after %.0f s: SE is already running (parallel start) with the bridge offline; Subnautica is "
                        "left running (never killed)" % a.timeout, ev="bridge_offline_parallel")
            log.say("PIDs: Subnautica %d, SE %d." % (sn.pid, se.pid), ev="pids", sn=sn.pid, se=se.pid)
            return None, sn, se
        if not ready and sn.poll() is not None:
            return 1, sn, None
        if not ready and not getattr(a, "se_anyway", False):
            # Real run (MODLOG): the bridge object died, snPhase stayed Quitting, and SE was started
 # 180 s later with the bridge offline, which hid the failure. Default now: stop here and report.
            log.say("Subnautica not ready after %.0f s: SE NOT started. Subnautica is left running (close it normally, "
                    "or launcher.py stop --sn-pid %d). Use --se-anyway to start SE with the bridge offline." % (
                        a.timeout, sn.pid), ev="not_ready_abort")
            return 1, sn, None
        if not ready:
            log.say("Subnautica not ready: starting SE anyway with the bridge offline (--se-anyway; tell the owner; never killed)",
                    ev="bridge_offline")
    finally:
        token.close()
        log.event("token_released")
    se = _start_se()
    log.say("PIDs: Subnautica %d, SE %d." % (sn.pid, se.pid), ev="pids", sn=sn.pid, se=se.pid)
    return None, sn, se

# ----------------------------------------------------------------------------------------------------------------
# S10 round 2 D: the performance preset (Wire S10Presets, CONTRACT §5.4 "S10 presets"): picked once per machine at the
# first play start (shared GPU -> low; 30 GB+ (a 32 GB machine) with a dedicated GPU -> high; else mid; memory never lowers it:
# project his 16 GB machine runs both games smooth), kept in
# %LOCALAPPDATA%\DeepEngineers\s10-preset.txt (edit it, or `launcher.py s10 --preset low|mid|high|auto`), and passed to
# both plugins as DEEPENGINEERS_S10_PRESET (an exported value wins). Life density for the S8a spawns: DEEPENGINEERS_S10_LIFE.
# project: the first Play (no preset file yet) shows the recommendation and asks (Enter keeps it; 45 s, then kept;
# DEEPENGINEERS_S10_ASK=off or no console: the silent pick as before).
# ----------------------------------------------------------------------------------------------------------------

S10_PRESET_FILE = os.path.join(LOCAL, "s10-preset.txt")
S10_PRESETS = {"low": 0.5, "mid": 1.0, "high": 1.5}   # name -> life factor (the twin of S10Presets' life column)
_INTEGRATED = ("radeon(tm) graphics", "radeon graphics", "780m", "760m", "680m", "vega", "intel(r) uhd", "intel(r) iris", "intel(r) hd", "arc(tm) graphics")

def s10_machine_facts():
    """(ramGB, sharedGpu, gpu names): RAM from GlobalMemoryStatusEx; the GPUs from Win32_VideoController (PowerShell CIM, 15 s
 timeout). Shared: no adapter that is not an integrated one (names above) - an integrated GPU alone, or nothing found."""
    import ctypes

    class MEMSTAT(ctypes.Structure):
        _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong), ("ullTotalPhys", ctypes.c_ulonglong),
                    ("ullAvailPhys", ctypes.c_ulonglong), ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
    ram = 0.0
    try:
        m = MEMSTAT()
        m.dwLength = ctypes.sizeof(MEMSTAT)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m)):
            ram = m.ullTotalPhys / (1024.0 ** 3)
    except Exception:  # noqa: BLE001 - facts only
        pass
    names = []
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command",
                              "Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }"],
                             capture_output=True, text=True, timeout=15)
        names = [n.strip() for n in (out.stdout or "").splitlines() if n.strip()]
    except Exception:  # noqa: BLE001
        names = []
    dedicated = [n for n in names if not any(k in n.lower() for k in _INTEGRATED) and "basic display" not in n.lower()]
    return ram, not dedicated, names

def s10_auto_preset():
    ram, shared, names = s10_machine_facts()
    if shared:
        return "low", ram, shared, names
    return ("high" if ram >= 30 else "mid"), ram, shared, names

S10_PRESET_ASK_TIMEOUT_S = 45   # the first Play's question waits this long, then keeps the recommendation (an unattended start never hangs)
S10_PRESET_ASK_TRIES = 3

def s10_preset_lines(rec, ram, shared, names):
    """The first Play's question, as console lines."""
    gpu = ", ".join(names) or "unknown graphics"
    return [
        "",
        "DeepEngineers performance preset for this PC: %s (recommended)" % rec.upper(),
        "  PC: %s%s, %.0f GB of memory" % (gpu, " (shared graphics chip)" if shared else "", ram),
        "  low   Subnautica capped at 30 fps, fewer ship lights, shorter ranges (shared graphics chips)",
        "  mid   the defaults",
        "  high  more lights, longer ranges (32 GB machines)",
        "  (change it later: Preset.cmd in the DeepEngineers folder)",
    ]

def s10_ask_preset(rec, ram, shared, names, ask, out=print, tries=S10_PRESET_ASK_TRIES, timeout_s=S10_PRESET_ASK_TIMEOUT_S):
    """Shows the recommendation and asks once per Play without a preset file; Enter, a timeout or no console keeps the
 recommendation, low/mid/high (any case) picks; an unknown answer is asked again up to `tries` times. Pure but for `ask` and `out`."""
    for line in s10_preset_lines(rec, ram, shared, names):
        out(line)
    for _ in range(max(1, tries)):
        try:
            answer = ask("Keep %s? Enter keeps it, or type low / mid / high (%d s): " % (rec.upper(), timeout_s))
        except (EOFError, KeyboardInterrupt, OSError):
            answer = None
        if answer is None:
            out("kept %s" % rec.upper())
            return rec
        v = answer.strip().lower()
        if v == "":
            return rec
        if v in S10_PRESETS:
            return v
        out("type low, mid or high")
    out("kept %s" % rec.upper())
    return rec

def s10_console_ask(timeout_s=S10_PRESET_ASK_TIMEOUT_S):
    """An `input`-like callable for the console: None without a console (stdin not a terminal) or after `timeout_s` seconds
 without a line (the reader thread is a daemon: it dies with the process)."""
    def ask(prompt):
        try:
            if not sys.stdin or not sys.stdin.isatty():
                return None
        except (AttributeError, ValueError):
            return None
        import threading
        box = []
        sys.stdout.write(prompt)
        sys.stdout.flush()
        t = threading.Thread(target=lambda: box.append(sys.stdin.readline()), daemon=True)
        t.start()
        t.join(timeout_s)
        if t.is_alive() or not box:
            sys.stdout.write("\n")
            sys.stdout.flush()
            return None
        return box[0]
    return ask

def s10_resolve_preset(log=None, ask=None):
    """The preset for this play start: the exported variable, else the machine's file, else the auto-pick, shown and asked
 about when `ask` is given (the first Play; DEEPENGINEERS_S10_ASK=off keeps the silent pick), then written."""
    v = (os.environ.get("DEEPENGINEERS_S10_PRESET") or "").strip().lower()
    if v in S10_PRESETS:
        return v
    try:
        with open(S10_PRESET_FILE, encoding="utf-8") as f:
            v = f.read().strip().lower()
        if v in S10_PRESETS:
            return v
    except OSError:
        pass
    rec, ram, shared, names = s10_auto_preset()
    v = rec
    asked = False
    if ask is not None and (os.environ.get("DEEPENGINEERS_S10_ASK") or "").strip().lower() != "off":
        asked = True
        v = s10_ask_preset(rec, ram, shared, names, ask)
    try:
        os.makedirs(os.path.dirname(S10_PRESET_FILE), exist_ok=True)
        with open(S10_PRESET_FILE, "w", encoding="utf-8") as f:
            f.write(v + "\n")
    except OSError:
        pass
    if log is not None:
        log.say("S10 preset %s (recommended %s, %s; RAM %.1f GB, GPU %s%s); kept in %s" % (
            v, rec, "asked at this first Play" if asked else "auto-picked", ram, ", ".join(names) or "unknown",
            " (shared)" if shared else "", S10_PRESET_FILE), ev="s10_preset", preset=v, recommended=rec, asked=asked, ramGB=round(ram, 1), shared=shared)
    return v

def run(log: Log, a) -> int:
    _sn_mutex, _se_mutex, launcher_object = L.mutex_names(a.name)
    sn_cmd, se_cmd = session_commands(a)
    log.say("plan: token %s + %s=1; Subnautica: %s; wait for snPhase %s (%.0f s); SE: %s" % (
        launcher_object, L.BRIDGE_ENV, subprocess.list2cmdline(sn_cmd), a.ready_phase, a.timeout,
        subprocess.list2cmdline(se_cmd)), ev="plan")
    if a.dry_run:
        log.say("dry run: nothing started")
        return 0
    import launcher_s8   # noqa: E402 (S8-settings / S8-exit: src/DeepEngineers.Launcher/launcher_s8.py, ROADMAP S8)
    launcher_s8.before_session(sys.modules[__name__], log, a)   # S8-settings: backup, restore at the next clean start, snapshot
    if not (getattr(a, "sn_cmd", None) or getattr(a, "se_cmd", None)):   # S10 hook (round 2 D): the preset for both plugins
        preset = s10_resolve_preset(log, ask=s10_console_ask())   # the first Play asks; later starts read the file
        os.environ.setdefault("DEEPENGINEERS_S10_PRESET", preset)
        os.environ.setdefault("DEEPENGINEERS_S10_LIFE", str(S10_PRESETS.get(preset, 1.0)))
        log.say("S10 preset %s (DEEPENGINEERS_S10_PRESET; %s)" % (os.environ["DEEPENGINEERS_S10_PRESET"], S10_PRESET_FILE), ev="s10_preset_used")
    rc, sn, se = start_games(log, a)
    if rc is not None:
        return rc
    log.say("Waiting on both (Ctrl-C leaves them running).")
    return launcher_s8.wait_exits(sys.modules[__name__], log, a, sn, se)   # S8-exit: hang offers, seExiting, no relaunch

# ----------------------------------------------------------------------------------------------------------------
# s1: one scripted S1 session (ROADMAP S1 oracle)
# ----------------------------------------------------------------------------------------------------------------

S1_DIR = os.path.join(REPO, "tests", "s1")
CAPTURES = os.path.join(LOCAL, "captures")
SN_READY_PHASES = ("ReadyIdle", "WarmingUp", "Active")
HOSTSAVE_CREATE_WAIT_S = 480.0   # S9 round 2: the first start makes the host save (the intro is about 2 min)
# ROADMAP S1 checks that these tools do not compute (data source for the runbook)
S1_OTHER_CHECKS = (
    ("S1.G2-E3", "planet behaves as computed (PB elevation, gravity, thrusters, oxygen, /wrradius, crush, floor; "
                 "changed WaterConfig.xml -> DE.Site REFUSE)", "owner session + SE log"),
    ("S1.N3", "streaming after 0.5/1/2 km camera jumps (IsWorldSettled, IsRangeActiveAndBuilt)", "SN JSONL telemetry"),
    ("S1.N8", "ghost Player rule (pelvis/camera, escapePod, currentSub, biome, IsInsideWalkable, CanBeAttacked)",
     "SN JSONL s1.probe + owner poses"),
    ("S1.N9", "UI hidden: capture without uGUI pixels, 0 ErrorMessage lines, C7 uiHidden", "capture + spy"),
    ("S1.N10", "water surface displacement at the camera over 60 s; Ocean.GetOceanLevel() = 0", "SN JSONL"),
    ("S1.N11", "run in background forced: snFrame keeps advancing 60 s after the focus hand-over", "spy + SN JSONL"),
    ("S1.N12", "SE world-load time (RunLoad -> first update), lab world and the owner's largest world", "SE log"),
)

def s1_modules():
    if S1_DIR not in sys.path:
        sys.path.insert(0, S1_DIR)
    import check_wm  # noqa: E402
    import latency  # noqa: E402
    import fmod_events  # noqa: E402
    return check_wm, latency, fmod_events

class SessionLog:
    """tests/out/s1/<stamp>/s1-session.jsonl: every command, reply, hold, pixel pair and capture (utc + qpc)."""

    def __init__(self, path):
        self.path = path
        self.f = open(path, "a", encoding="utf-8")

    def event(self, ev, **kw):
        rec = {"utc": datetime.datetime.utcnow().isoformat(timespec="milliseconds") + "Z", "qpc": S.qpc(), "ev": ev}
        rec.update(kw)
        self.f.write(json.dumps(rec, default=str) + "\n")
        self.f.flush()

    def close(self):
        self.f.close()

def _pick(d, *names):
    for k in names:
        if isinstance(d, dict) and d.get(k) is not None:
            return d[k]
    return None

def hash_tree(root: str) -> dict:
    out = {}
    for base, _dirs, files in os.walk(root):
        for n in sorted(files):
            full = os.path.join(base, n)
            try:
                out[os.path.relpath(full, root).replace(os.sep, "/")] = sha256_file(full)
            except OSError as e:
                out[os.path.relpath(full, root).replace(os.sep, "/")] = "unreadable: %s" % e
    return out

def host_slot_dir(a):
    if getattr(a, "host_slot_dir", None):
        return a.host_slot_dir
    if a.sn_cmd:
        return None                       # stand-in run: no host slot unless given
    return os.path.join(sn_saves_dir(), a.host_slot)

def wait_in_world(log: Log, sess: SessionLog, a, sn, se) -> bool:
    """SE linked and in world with its hello acknowledged, C1 published, Subnautica in a ready phase (read-only)."""
    t0 = time.monotonic()
    last, stable_since, refused_since = None, None, None
    sec = None
    try:
        while time.monotonic() - t0 < a.world_timeout:
            if sn.poll() is not None or se.poll() is not None:
                log.say("S1: %s exited while waiting for the world" % ("Subnautica" if sn.poll() is not None else "SE"),
                        ev="s1_exit_early")
                return False
            if sec is None:
                sec = S.Section.open(a.name, readonly=True)
            if sec is None or sec.mapped_size < L.SECTION_SIZE:
                time.sleep(0.5)
                continue
            h = sec.header()
            c5 = S.seqlock_read(sec, L.REGION["C5"].offset, L.SE_STATE.size)[2]
            c7 = S.seqlock_read(sec, L.REGION["C7"].offset, L.SN_STATUS.size)[2]
            c1 = S.seqlock_read(sec, L.REGION["C1"].offset, L.SE_FRAME.size)[2]
            c5 = L.SE_STATE.unpack(c5) if c5 else {}
            c7 = L.SN_STATUS.unpack(c7) if c7 else {}
            c1 = L.SE_FRAME.unpack(c1) if c1 else {}
            hello = c5.get("helloEpoch", 0)
            cond = {
                "magic": h["magic"] == L.MAGIC,
                "SE attached": h["sePid"] != 0 and S.process_state(h["sePid"]) == "alive",
                "sePhase InWorld": h["sePhase"] == L.SE_PHASE["InWorld"],
                "hello acked": bool(hello) and c7.get("helloAckEpoch") == hello,
                "C1 inWorld": bool(c1.get("flags", 0) & (1 << L.SE_FRAME_FLAGS["inWorld"])),
                "snPhase ready": L.SN_PHASES.get(h["snPhase"]) in SN_READY_PHASES,
            }
            state = (tuple(sorted(cond.items())), h["seFlags"])
            if state != last:
                last = state
                stable_since = None
                missing = [k for k, v in cond.items() if not v]
                log.say("  world wait %.0f s: %s; sePhase %s snPhase %s seFlags %s%s" % (
                    time.monotonic() - t0, "all met" if not missing else "waiting for " + ", ".join(missing),
                    L.SE_PHASES.get(h["sePhase"], h["sePhase"]), L.SN_PHASES.get(h["snPhase"], h["snPhase"]),
                    _flags(h["seFlags"], L.SE_FLAGS), "" if h["sePid"] in (0, se.pid) else
                    "; sePid %d is not the started process %d" % (h["sePid"], se.pid)), ev="s1_world_wait", cond=cond)
            refused = h["seFlags"] & (1 << L.SE_FLAGS["displayRefused"])
            if cond["sePhase InWorld"] and refused and not cond["hello acked"]:
                refused_since = refused_since or time.monotonic()
                if time.monotonic() - refused_since >= 60.0:
                    log.say("S1: SE has been in world for 60 s with displayRefused and no acknowledged hello (site, "
                            "version or self-test refused): see the SE log's DE.Site / DE.Version lines",
                            ev="s1_refused")
                    return False
            else:
                refused_since = None
            if all(cond.values()):
                stable_since = stable_since or time.monotonic()
                if time.monotonic() - stable_since >= 1.0:
                    sess.event("in_world", seconds=round(time.monotonic() - t0, 3), helloEpoch=hello,
                               seFlags=h["seFlags"], c1Tick=c1.get("tick"))
                    if h["seFlags"] & (1 << L.SE_FLAGS["displayRefused"]):
                        log.say("S1: SE reports displayRefused (site, version or self-test): the oracles will say "
                                "which part fails", ev="s1_display_refused")
                    return True
            time.sleep(0.5)
        log.say("S1: SE not in world with an acknowledged hello after %.0f s" % a.world_timeout, ev="s1_world_timeout")
        return False
    finally:
        if sec:
            sec.close()

def _flags(v, table):
    return ",".join(n for n, b in sorted(table.items(), key=lambda kv: kv[1]) if v & (1 << b)) or "-"

def run_steps(log: Log, sess: SessionLog, a, steps, sn, se) -> dict:
    """Sends the S1 steps through the C11 harness (devControl bit 0). Never touches a game window."""
    stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "aborted": False}
    sec = S.Section.open(a.name)
    if sec is None:
        log.say("S1: section absent at the harness attach", ev="s1_no_section")
        stats["aborted"] = True
        return stats
    h = S.Harness(sec)
    ok, why = h.attach()
    if not ok:
        log.say("S1: harness not attached: %s" % why, ev="s1_harness_refused")
        sec.close()
        stats["aborted"] = True
        return stats

    def alive():
        return sn.poll() is None and se.poll() is None

    def wait(seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end and alive():
            h.poll_replies()
            time.sleep(min(0.02, max(0.0, end - time.monotonic())))

    def call(side, cmd, args, timeout=None):
        t0 = time.monotonic()
        rid, st = h.send(side, cmd, args)
        stats["sent"] += 1
        sess.event("cmd", side=side, cmd=cmd, args=args, id=rid, write=st)
        rep = h.wait_reply(rid, timeout or a.cmd_timeout) if st == S.RW_OK else None
        sess.event("reply", side=side, cmd=cmd, args=args, id=rid, reply=rep, ms=round((time.monotonic() - t0) * 1000, 1))
        if rep is None:
            stats["noReply"] += 1
        elif rep.get("ok"):
            stats["ok"] += 1
        else:
            stats["failed"] += 1
        return rep

    try:
        for side in ("se", "sn"):
            rep = call(side, "de.ping", {})
            log.say("  de.ping %s: %s" % (side.upper(), json.dumps(rep) if rep else "no reply (release build?)"),
                    ev="s1_ping", side=side, reply=rep)
            if rep is None:
                log.say("S1: %s does not answer C11 (dev build needed): script not run" % side.upper())
                stats["aborted"] = True
                return stats
        n = len(steps)
        for i, st in enumerate(steps):
            if not alive():
                log.say("S1: a game exited during the script (step %d/%d)" % (i + 1, n), ev="s1_exit_during_script")
                stats["aborted"] = True
                break
            kind = st["kind"]
            if kind in ("se", "sn"):
                rep = call(kind, st["cmd"], st["args"])
                if rep is None or not rep.get("ok"):
                    log.say("  step %d/%d %s %s %s: %s" % (i + 1, n, kind.upper(), st["cmd"], st["args"].get("label", ""),
                                                          json.dumps(rep)[:300] if rep else "no reply"),
                            ev="s1_step_problem")
                wait(st["wait"])
            elif kind == "hold":
                q0, u0 = S.qpc(), datetime.datetime.utcnow().isoformat(timespec="milliseconds") + "Z"
                log.say("  hold %s %.0f s" % (st["label"], st["seconds"]))
                wait(st["seconds"])
                sess.event("hold", label=st["label"], qpcFrom=q0, qpcTo=S.qpc(), utcFrom=u0,
                           utcTo=datetime.datetime.utcnow().isoformat(timespec="milliseconds") + "Z",
                           seconds=st["seconds"], qpcFrequency=S.QPC_FREQUENCY)
            elif kind == "pixel":
                rse = call("se", "de.pixel", {"ahead": st["aheadM"], "aheadM": st["aheadM"], "label": st["label"]})
                point, tick = _pick(rse, "pointSn", "snPoint", "sn"), _pick(rse, "tick", "seTick")
                if tick is None and isinstance(rse, dict) and isinstance(rse.get("line"), str):
                    m = re.search(r"\btick=(\d+)", rse["line"])
                    tick = int(m.group(1)) if m else None
                rsn = None
                if point is not None and tick is not None:
                    rsn = call("sn", "de.pixel", {"pointSn": point, "minSeTick": tick, "label": st["label"]})
                sess.event("pixel", label=st["label"], se=rse, sn=rsn)
            elif kind == "capture":
                # C10 through SE (CONTRACT 5.1: request SE -> SN, relayed from the harness); when SE does not take
 # de.capture, Subnautica's own de.capture {path, checkId} is used
                args = {"path": st["path"], "label": st["label"], "kind": 1, "checkId": "S1.WM-2"}
                rep = call("se", "de.capture", args, timeout=10.0)
                side = "se"
                if rep is None or not rep.get("ok"):
                    rep = call("sn", "de.capture", args, timeout=10.0)
                    side = "sn"
                    wait(1.0)
                sess.event("capture", label=st["label"], path=st["path"], reply=rep, via=side)
            if (i + 1) % 20 == 0:
                log.say("  S1 script: %d/%d steps (%d ok, %d failed, %d without reply)" % (
                    i + 1, n, stats["ok"], stats["failed"], stats["noReply"]))
    finally:
        h.detach()
        sec.close()
    return stats

def close_session(log: Log, a, sn, se, standin_se: bool, standin_sn: bool):
    """SE first (CloseMainWindow; a stand-in SE by TerminateProcess), then Subnautica quits by itself (D11)."""
    if se is not None and se.poll() is None:
        if standin_se:
            S.terminate_pid(se.pid, 0)
            log.say("SE stand-in pid %d terminated" % se.pid, ev="se_closed", how="terminate-standin")
        else:
            posted = S.close_main_window(se.pid)
            log.say("CloseMainWindow(SE %d): %s" % (se.pid, "WM_CLOSE posted" if posted else "no main window"))
            if wait_pid(se.pid, CLOSE_WAIT_S):
                log.say("SE pid %d closed" % se.pid, ev="se_closed")
            else:
                log.say("SE pid %d still running after %.0f s: left running (never killed here; launcher.py stop "
                        "--kill asks the owner)" % (se.pid, CLOSE_WAIT_S), ev="se_not_closed")
    if sn is not None and sn.poll() is None:
        if wait_pid(sn.pid, L.SN_QUIT_AFTER_SE_EXIT_S + 5):
            log.say("Subnautica pid %d quit by itself" % sn.pid, ev="sn_closed", how="self")
        elif standin_sn:
            S.terminate_pid(sn.pid, 0)
            log.say("SN stand-in pid %d terminated" % sn.pid, ev="sn_closed", how="terminate-standin")
        else:
            stop_sn(log, a.name, sn.pid, allow_kill=False, timeout=CLOSE_WAIT_S)

def collect(log: Log, out: str, t_start: float, sn, standin_sn: bool, standin_se: bool, stamp: str) -> dict:
    """Copies what the session produced into `out` (read-only access to game and loader folders). A stand-in's JSONL
 is its stdout (already in `out`); a real side's JSONL comes from %LOCALAPPDATA%/DeepEngineers/logs (SE: the se-*.jsonl
 written since the session start; Subnautica: sn-*-<pid>.jsonl), with its game and loader logs."""
    got = {"se": [], "sn": [], "other": []}

    def cp(src, dst_name, kind):
        try:
            shutil.copy2(src, os.path.join(out, dst_name))
            got[kind].append(dst_name)
        except OSError as e:
            log.say("  collect: %s not copied (%s)" % (src, e))

    for side, flag in (("se", standin_se), ("sn", standin_sn)):
        if flag and os.path.exists(os.path.join(out, "%s-standin.jsonl" % side)):
            got[side].append("%s-standin.jsonl" % side)
    if not standin_se:
        for p in sorted(glob.glob(os.path.join(LOG_DIR, "se-*.jsonl"))):
            if os.path.getmtime(p) >= t_start - 5:
                cp(p, os.path.basename(p), "se")
        appdata = os.environ.get("APPDATA", "")
        for p in glob.glob(os.path.join(appdata, "SpaceEngineers", "SpaceEngineers_*.log")):
            if os.path.getmtime(p) >= t_start - 5:
                cp(p, os.path.basename(p), "other")
        p = os.path.join(PULSAR, "Legacy", "info.log")
        if os.path.exists(p) and os.path.getmtime(p) >= t_start - 5:
            cp(p, "Pulsar-info.log", "other")
    if not standin_sn and sn is not None:
        for p in sorted(glob.glob(os.path.join(LOG_DIR, "sn-*-%d.jsonl" % sn.pid))):
            cp(p, os.path.basename(p), "sn")
        p = os.path.join(SUBNAUTICA, "BepInEx", "LogOutput.log")
        if os.path.exists(p) and os.path.getmtime(p) >= t_start - 5:
            cp(p, "BepInEx-LogOutput.log", "other")
    cap = os.path.join(CAPTURES, "s1", stamp)
    if os.path.isdir(cap):
        dst = os.path.join(out, "captures")
        shutil.copytree(cap, dst, dirs_exist_ok=True)
        got["other"].append("captures/ (%d files)" % sum(len(f) for _r, _d, f in os.walk(dst)))
    log.say("collected into %s: SE %s; SN %s; %s" % (out, got["se"], got["sn"], got["other"]), ev="s1_collected",
            files=got)
    return got

def _grep(lines, pattern):
    rx = re.compile(pattern)
    return [x for x in lines if rx.search(x)]

def s1_analyze(log: Log, a, out: str) -> int:
    """Runs check_wm.py, latency.py and fmod_events.py on a collected session folder and prints the S1 oracle table."""
    W, LAT, FM = s1_modules()
    sess_path = os.path.join(out, "s1-session.jsonl")
    session = W.read_jsonl(sess_path) if os.path.exists(sess_path) else []
    start = next((r for r in session if r.get("ev") == "start"), {})
    p = start.get("plan") or W.plan(getattr(a, "quick", False))
    parts = start.get("parts") or list(p["parts"])
    se_files = sorted(glob.glob(os.path.join(out, "se-*.jsonl")))
    sn_files = sorted(glob.glob(os.path.join(out, "sn-*.jsonl")))
    se, sn = W.read_jsonl(se_files), W.read_jsonl(sn_files)
    strict = not getattr(a, "lenient", False)
    only = [x for x in ("wm2", "wm3", "wm4", "g1e5") if x in parts] + ["n7"]
    wm = W.evaluate(se, sn, session, p, only, strict)
    base = None
    if getattr(a, "baseline", None):
        with open(a.baseline, encoding="utf-8") as f:
            base = json.load(f)
    lat = LAT.evaluate(se, sn, p, None, base, strict)
    lat_rows = [r for r in lat["rows"] if r["part"] != "yaw" or "yaw" in parts]
    fm = FM.evaluate(sn, se, session, ("hold60", "hold60u"), None, None, strict) if "hold" in parts else \
        {"rows": [], "ok": True}
    rows = list(wm["rows"]) + lat_rows + list(fm["rows"])
    # S1.N4 host slot, S1.N5 Freedom, S1.N13 site binding (log lines named by ROADMAP S1)
    slots = [r for r in session if r.get("ev") == "host_slot"]
    before = next((r for r in slots if r.get("when") == "before"), None)
    after = next((r for r in reversed(slots) if r.get("when") == "after"), None)
    if before and after and before.get("files") is not None and after.get("files") is not None:
        b, e = before["files"], after["files"]
        diff = sorted(k for k in set(b) | set(e) if b.get(k) != e.get(k))
        exited = after.get("snExited", True)
        status = "FAIL" if diff or not b else ("PASS" if exited else "MISSING")
        rows.append({"id": "S1.N4", "what": "host slot %s: SHA-256 of every file before and after (%d files)%s" % (
            before.get("dir"), len(b), "" if exited else "; Subnautica had not exited at the after hash: hash again "
                                                          "after it exits"), "status": status,
            "measured": "%d changed" % len(diff), "threshold": "0 changed", "part": "n4", "required": True,
            "detail": {"changed": diff[:20], "snExited": exited}})
    else:
        rows.append({"id": "S1.N4", "what": "host slot hashes before/after", "status": "MISSING",
                     "measured": "before %s, after %s" % ("taken" if before and before.get("files") is not None else "missing",
                                                          "taken" if after and after.get("files") is not None else "missing"),
                     "threshold": "0 changed", "part": "n4", "required": True})
    sn_lines, se_lines = W.text_lines(sn), W.text_lines(se)
    for cid, what, lines, pat in (
            ("S1.N5", "DE.HostSave mode=Freedom invisible=false", sn_lines, r"DE\.HostSave mode=Freedom invisible=false"),
            ("S1.N13", "DE.Site OK (within 600 ticks of the first update: read the line)", se_lines, r"DE\.Site OK"),
            ("S1.N4", "DE.SaveBlocked on a test-triggered IngameMenu.SaveGame", sn_lines, r"DE\.SaveBlocked")):
        hit = _grep(lines, pat)
        # S1.N4's de.testSave runs in the "hold" part: required whenever that part ran
        req = cid != "S1.N4" or "hold" in parts
        rows.append({"id": cid, "what": what, "status": "PASS" if hit else ("MISSING" if req else "OTHER"),
                     "measured": (hit[0][:60] if hit else "no line"), "threshold": "line present", "part": cid,
                     "required": req})
    for cid, what, src in S1_OTHER_CHECKS:
        rows.append({"id": cid, "what": what, "status": "OTHER", "measured": "", "threshold": src, "part": "other",
                     "required": False})
    bad = ("FAIL", "MISSING") if strict else ("FAIL",)
    ok = not any(r.get("required", True) and r["status"] in bad for r in rows)
    res = {"tool": "launcher s1", "out": out, "ok": ok, "parts": parts, "quick": bool(p.get("quick")),
           "seLogs": se_files, "snLogs": sn_files, "rows": rows, "checkWm": wm["summary"],
           "latency": {"transport": {k: v for k, v in lat["transport"].items() if k != "histogram"},
                       "step": lat["step"], "m2": lat.get("m2")},
           "transportHistogram": lat["transport"]["histogram"], "fmod": fm.get("holds")}
    text = W.table(rows)
    hist = LAT.hist_text(lat["transport"]["histogram"])
    with open(os.path.join(out, "s1-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(out, "s1-table.txt"), "w", encoding="utf-8") as f:
        f.write(text + "\n\ntransport histogram:\n" + hist + "\n")
    print("\nS1 ORACLE TABLE (%s)  (* = not required; OTHER = runbook check outside these tools)" % out)
    print(text)
    print("transport histogram (ms):\n" + hist)
    log.say("S1 ORACLE %s: %d rows, %d FAIL, %d MISSING -> %s" % (
        "PASS" if ok else "FAIL", len(rows), sum(1 for r in rows if r["status"] == "FAIL"),
        sum(1 for r in rows if r["status"] == "MISSING" and r.get("required", True)),
        os.path.join(out, "s1-results.json")), ev="s1_result", ok=ok)
    return 0 if ok else 1

def s1(log: Log, a) -> int:
    if a.analyze:
        return s1_analyze(log, a, a.analyze)
    W, _LAT, _FM = s1_modules()
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(REPO, "tests", "out", "s1", stamp)
    p = W.plan(a.quick)
    parts = [x.strip() for x in a.parts.split(",") if x.strip()] if a.parts else list(p["parts"])
    unknown = [x for x in parts if x not in p["parts"]]
    if unknown:
        log.say("unknown parts %s (known: %s)" % (unknown, ",".join(p["parts"])))
        return 2
    steps = W.steps(p, parts, capture_prefix="s1/%s" % stamp)
    a.cont = not a.no_continue
    sn_cmd, se_cmd = session_commands(a)
    log.say("S1 session %s: parts %s, %d steps%s; Subnautica: %s; SE: %s; out %s" % (
        stamp, ",".join(parts), len(steps), " (quick plan)" if a.quick else "", subprocess.list2cmdline(sn_cmd),
        subprocess.list2cmdline(se_cmd), out), ev="s1_plan")
    if a.dry_run:
        for st in steps:
            print("  %-8s %-12s %s" % (st["kind"], st.get("cmd", ""), json.dumps(st.get("args") or
                                                                           {k: v for k, v in st.items()
                                                                            if k not in ("kind", "part")})))
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    sess = SessionLog(os.path.join(out, "s1-session.jsonl"))
    standin_sn, standin_se = bool(a.sn_cmd), bool(a.se_cmd)
    sess.event("start", stamp=stamp, plan=p, parts=parts, steps=len(steps), name=a.name, argv=sys.argv[1:],
               standin={"sn": standin_sn, "se": standin_se}, snCmd=sn_cmd, seCmd=se_cmd)
    slot = host_slot_dir(a)
    if slot:
        sess.event("host_slot", when="before", dir=slot, files=hash_tree(slot) if os.path.isdir(slot) else None)
    t_start = time.time()
    rc, sn, se = start_games(log, a, stdout_dir=out)
    stats = None
    try:
        if rc is None:
            if wait_in_world(log, sess, a, sn, se):
                stats = run_steps(log, sess, a, steps, sn, se)
                log.say("S1 script finished: %s" % json.dumps(stats), ev="s1_script_done", stats=stats)
            else:
                rc = 1
    finally:
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            close_session(log, a, sn, se, standin_se, standin_sn)
        if slot:
            # S1.N4: hash after Subnautica exited; if it has not, wait once more, then hash anyway (the row is then
 # MISSING unless a file already changed): the table never passes without the "after" hash (finding 12)
            sn_alive = sn is not None and sn.poll() is None
            if sn_alive and not a.keep_running:
                log.say("S1.N4: Subnautica pid %d still running: waiting up to 60 s for it to exit before the hash" % sn.pid)
                sn_alive = not wait_pid(sn.pid, 60.0)
            sess.event("host_slot", when="after", dir=slot, files=hash_tree(slot) if os.path.isdir(slot) else None,
                       snExited=not sn_alive)
        sess.event("end", rc=rc, stats=stats)
        sess.close()
        collect(log, out, t_start, sn, standin_sn, standin_se, stamp)
    if rc is not None:
        log.say("S1 session aborted (rc %d); analysing what was collected" % rc)
    res = s1_analyze(log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# s2: one scripted S2 session (ROADMAP S2 oracle: T-D1, N0, N2, N3, N4, N5, N6, N7, N8, N9, N10, N11, T-A1)
# ----------------------------------------------------------------------------------------------------------------

S2_DIR = os.path.join(REPO, "tests", "s2")
TOOLS = os.path.join(REPO, "tools")
S2_PARTS = ("n9", "n8", "td1", "n2", "n7", "n7b", "n3", "n4", "n11", "n6", "ta1", "n5")
S2_DEFAULT_PARTS = ("n9", "n8", "td1", "n2", "n7", "n7b", "n3", "n4", "n11", "n6", "ta1")    # n5 needs the project
Z_START = 7750.0         # S2.N2 zone exit: Underwater first, inside the Rho guard
S2_SPOT = (-1029.0, -1116.0)          # grandReef (S1.WM-2 spot): T-D1, N4, T-A1 horizontal position
S2_MENUS = (("inventory", "I"), ("terminal", "K"), ("toolbar", "G"), ("esc", "ESC"), ("chat", "Enter"), ("f10", "F10"))
S2_CALIB = (255, 0, 255)
# n7b, the re-dive part. Its windows are n7b.quick, n7b.near and
# n7b.far: the bare name "n7b" is the window of the n7 part's case (b) (analyze_n2.py, analyze_perf.py)
N7B_TOP, N7B_BOTTOM, N7B_FAR_TOP = 2.0, -3.0, 8.0      # SN y of the de.motion lines (m)
N7B_SPEED = 1.5                  # m/s, the project's swimming pace order of magnitude
N7B_CYCLES = 10                  # quick re-dives (3 with --quick)
N7B_CYCLE_PAUSE_S = 2.0          # at +2 m between quick cycles (Prime quiet about 3.7 s: above the old 3 s rule)
N7B_NEAR_PAUSE_S = 6.0           # the near pause at +2 m: Prime quiet about 7.7 s (0.7 s above +1 m on the way up, the
# path's 1 s slack, the pause): above the old 3 s, below the new 10 s near the surface
N7B_FAR_PAUSE_S = 12.0           # at +8 m: above +5 m the 3 s rule applies, so Track is expected
WATER_CLIENT_SETTINGS = os.path.join(SE_APPDATA, "Storage", "WaterClientSettings.xml")
S2_OTHER_ROWS = (
    ("S2.T-I1", "human-driven 60 s input script twice (see-through off, then on): SE event counts and mouse delta within "
                "1 %; Subnautica GameInput all zero", "owner (ROADMAP S2 owner actions)"),
    ("S2.N1", "full-screen sprites (blood overlay in a Survival copy, camera/turret overlay, medical screen, loading "
              "screens) keep < 2 % opaque coverage or are filtered", "owner session + de.display readback"),
    ("S2.A'", "fallback harness: 50 toggles with lower/raise (DEEPENGINEERS_DISPLAY=aprime, `launcher.py s2 --parts "
              "td1`): SE foreground 50/50, Subnautica fully visible when lowered", "orchestrator session"),
    ("S2.N10", "repeat with ForegroundLockTimeout at the Windows default (200000)", "owner changes and restores the "
                                                                                    "system setting"),
)

def s2_modules():
    for p in (S2_DIR, os.path.join(TOOLS, "display"), os.path.join(TOOLS, "capture"), os.path.join(TOOLS, "audio")):
        if p not in sys.path:
            sys.path.insert(0, p)
    import s2_common  # noqa: E402
    import analyze_td1  # noqa: E402
    import analyze_ta1  # noqa: E402
    import analyze_n2  # noqa: E402
    import analyze_n7b  # noqa: E402
    import analyze_perf  # noqa: E402
    return s2_common, analyze_td1, analyze_ta1, analyze_n2, analyze_perf, analyze_n7b

def s2_tools():
    s2_modules()
    import walker  # noqa: E402
    import notepad_between  # noqa: E402
    import ui_automation  # noqa: E402
    return walker, notepad_between, ui_automation

class S2SessionLog(SessionLog):
    """s2-session.jsonl; thread-safe (the samplers write from their own threads)."""

    def __init__(self, path):
        import threading
        super().__init__(path)
        self.lock = threading.Lock()

    def event(self, ev, **kw):
        with self.lock:
            super().event(ev, **kw)

def read_state(sec) -> dict:
    """One read-only snapshot of the section: C0 (heartbeats, phases, hwnds), C5, C7, C1 tick (seqlocks)."""
    h = sec.header()
    out = {"qpc": S.qpc(), "c0": {k: h.get(k) for k in ("snHeartbeatMs", "seHeartbeatMs", "snPhase", "sePhase", "snHwnd",
                                                         "seHwnd")}}
    st, _q, d5 = S.seqlock_read(sec, L.REGION["C5"].offset, L.SE_STATE.size)
    if d5:
        v = L.SE_STATE.unpack(d5)
        out["c5"] = {k: v[k] for k in ("seq", "tick", "flags", "viewState", "approachMode", "displayMode", "overlays",
                                       "zone", "perfMode", "targetFps", "snBusTarget", "crossfadeMs", "crossingTick",
                                       "camSnY")}
    st, _q, d7 = S.seqlock_read(sec, L.REGION["C7"].offset, L.SN_STATUS.size)
    if d7:
        v = L.SN_STATUS.unpack(d7)
        out["c7"] = {k: v[k] for k in ("seq", "flags", "snPhase", "perfModeApplied", "snFrame", "presentedSeTick",
                                       "vSyncCountApplied", "targetFrameRateApplied", "snHwnd")}
        out["c7"]["snRect"] = list(v["snRect"])
    st, _q, d1 = S.seqlock_read(sec, L.REGION["C1"].offset, L.SE_FRAME.size)
    if d1:
        v = L.SE_FRAME.unpack(d1)
        out["c1Tick"], out["c1Qpc"] = v["tick"], v["qpcCapture"]
    return out

class _Loop:
    def __init__(self):
        import threading
        self.stop_ev = threading.Event()
        self.thread = threading.Thread(target=self._guarded, daemon=True)

    def start(self):
        self.thread.start()
        return self

    def stop(self):
        self.stop_ev.set()
        self.thread.join(3.0)

    def _guarded(self):
        try:
            self.run()
        except Exception as e:   # a sampler never takes the session down
            sys.stderr.write("sampler stopped: %r\n" % (e,))

class SectionSampler(_Loop):
    """Read-only section samples ("sample" events) tagged with the current part, at `hz` while a part is set."""

    def __init__(self, name, sess, hz=10.0):
        super().__init__()
        self.name, self.sess, self.hz = name, sess, hz
        self.part = None

    def run(self):
        sec = None
        try:
            while not self.stop_ev.is_set():
                part = self.part
                if part is None:
                    self.stop_ev.wait(0.05)
                    continue
                if sec is None:
                    sec = S.Section.open(self.name, readonly=True)
                    if sec is None or sec.mapped_size < L.SECTION_SIZE:
                        sec = None
                        self.stop_ev.wait(0.5)
                        continue
                rec = read_state(sec)
                self.sess.event("sample", part=part, qpcFrequency=S.QPC_FREQUENCY, **rec)
                self.stop_ev.wait(1.0 / self.hz)
        finally:
            if sec:
                sec.close()

class CrossingWatcher(_Loop):
    """Crossing -> Subnautica ready (T-D1): polls C5 crossingTick, C1 (tick, qpcCapture) and C7 presentedSeTick about
 every millisecond; for each new crossingTick logs a "crossing" event with the latency from the crossing tick's C1
 capture (or the moment C5 showed it) to the first C7 presentedSeTick >= crossingTick."""

    def __init__(self, name, sess):
        super().__init__()
        self.name, self.sess = name, sess
        self.count = 0

    def run(self):
        sec = None
        ticks = collections.OrderedDict()
        pending = []
        last = None
        f = float(S.QPC_FREQUENCY)
        try:
            while not self.stop_ev.is_set():
                if sec is None:
                    sec = S.Section.open(self.name, readonly=True)
                    if sec is None or sec.mapped_size < L.SECTION_SIZE:
                        sec = None
                        self.stop_ev.wait(0.5)
                        continue
                now = S.qpc()
                _s, _q, d1 = S.seqlock_read(sec, L.REGION["C1"].offset, L.SE_FRAME.size)
                if d1:
                    v1 = L.SE_FRAME.unpack(d1)
                    if v1["tick"] not in ticks:
                        ticks[v1["tick"]] = v1["qpcCapture"]
                        while len(ticks) > 1200:
                            ticks.popitem(last=False)
                _s, _q, d5 = S.seqlock_read(sec, L.REGION["C5"].offset, L.SE_STATE.size)
                v5 = L.SE_STATE.unpack(d5) if d5 else {}
                ct = v5.get("crossingTick", 0)
                if ct and ct != last:
                    last = ct
                    _s, _q, d7a = S.seqlock_read(sec, L.REGION["C7"].offset, L.SN_STATUS.size)
                    pending.append({"crossingTick": ct, "qpcC5": now, "qpcC1": ticks.get(ct),
                                    "viewState": v5.get("viewState"), "approachMode": v5.get("approachMode"),
                                    "perfMode": v5.get("perfMode"),
                                    "snPerfApplied": L.SN_STATUS.unpack(d7a)["perfModeApplied"] if d7a else None})
                if pending:
                    _s, _q, d7 = S.seqlock_read(sec, L.REGION["C7"].offset, L.SN_STATUS.size)
                    pst = L.SN_STATUS.unpack(d7)["presentedSeTick"] if d7 else 0
                    keep = []
                    for p in pending:
                        if p["qpcC1"] is None:
                            p["qpcC1"] = ticks.get(p["crossingTick"])
                        if pst >= p["crossingTick"]:
                            ref = p["qpcC1"] or p["qpcC5"]
                            p.update(qpcPresented=now, presentedSeTick=pst, latencyMs=round((now - ref) / f * 1000.0, 2),
                                     reference="C1 qpcCapture" if p["qpcC1"] else "C5 first seen")
                            self.sess.event("crossing", **p)
                            self.count += 1
                        elif (now - p["qpcC5"]) / f > 5.0:
                            p.update(latencyMs=None, never=True)
                            self.sess.event("crossing", **p)
                        else:
                            keep.append(p)
                    pending = keep
                self.stop_ev.wait(0.001)
        finally:
            if sec:
                sec.close()

class ForegroundWatcher(_Loop):
    """S2.N10: the foreground window every 100 ms from before SE starts ("fg" events on changes) and the C0 phases
 ("phase" events), so the analysis can say who held the foreground after SE's menu and after ReadyIdle."""

    def __init__(self, name, sess, interval=0.1):
        super().__init__()
        self.name, self.sess, self.interval = name, sess, interval
        self.samples = 0
        self.by_image = collections.Counter()

    def run(self):
        WK = s2_tools()[0]
        sec = None
        last_fg, last_ph = None, None
        try:
            while not self.stop_ev.is_set():
                fg = WK.foreground()
                q = S.qpc()
                img = fg.get("image") or "?"
                self.samples += 1
                self.by_image[img] += 1
                key = (fg["hwnd"], img)
                if key != last_fg:
                    last_fg = key
                    self.sess.event("fg", qpc=q, image=img, pid=fg["pid"], hwnd=WK.hexh(fg["hwnd"]))
                if sec is None:
                    sec = S.Section.open(self.name, readonly=True)
                    if sec is not None and sec.mapped_size < L.HEADER_SIZE:
                        sec.close()
                        sec = None
                if sec is not None:
                    ph = (sec.hdr("sePhase"), sec.hdr("snPhase"))
                    if ph != last_ph:
                        last_ph = ph
                        self.sess.event("phase", qpc=q, sePhase=L.SE_PHASES.get(ph[0], ph[0]),
                                        snPhase=L.SN_PHASES.get(ph[1], ph[1]))
                self.stop_ev.wait(self.interval)
        finally:
            if sec:
                sec.close()
            self.sess.event("fg_summary", samples=self.samples, byImage=dict(self.by_image))

class S2Runner:
    """The scripted S2 parts (ROADMAP S2 oracle) through the C11 harness. Never touches a game window, except the
 Notepad of S2.N5 (its own window) and read-only window queries."""

    def __init__(self, log, a, sess, sn, se, out, standin_sn, standin_se):
        self.log, self.a, self.sess, self.sn, self.se, self.out = log, a, sess, sn, se, out
        self.standin_sn, self.standin_se = standin_sn, standin_se
        self.standin = standin_sn or standin_se
        self.q = a.quick
        self.sec = S.Section.open(a.name)
        self.h = S.Harness(self.sec) if self.sec else None
        self.sampler = SectionSampler(a.name, sess, a.sample_hz).start()
        self.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
        self.WK, self.NB, self.UA = s2_tools()
        self.bg = []

    # ------------------------------------------------------------------------------------------------ plumbing
    def alive(self):
        return self.sn.poll() is None and self.se.poll() is None

    def attach(self):
        if self.h is None:
            return False, "section absent"
        ok, why = self.h.attach()
        if not ok:
            return False, why
        for side in (("se",) if getattr(self.a, "se_alone", False) else ("se", "sn")):
            rep = self.call(side, "de.ping", {})
            if rep is None:
                return False, "%s does not answer C11 (dev build needed)" % side.upper()
        return True, ""

    def close(self):
        for p in self.bg:
            if p.poll() is None:
                try:
                    p.wait(timeout=90)
                except subprocess.TimeoutExpired:
                    p.terminate()
        self.sampler.stop()
        if self.h:
            self.h.detach()
        if self.sec:
            self.sec.close()

    def set_auto_fallback(self, on):
        """Review fix (finding 1): `de.display autoFallback=off` before the parts (the N0 probe verdict is
 judged in the table; a NOT HONOURED verdict must not switch SE to A' for the parts that follow)."""
        if getattr(self.a, "se_alone", False):
            return
        rep = self.call("se", "de.display", {"autoFallback": "on" if on else "off"}, label="session.autoFallback")
        self.sess.event("auto_fallback", on=bool(on), supported=self.supported(rep))
        self.log.say("  S2: SE autoFallback %s%s" % ("on" if on else "off", "" if self.supported(rep) else
                                                    " (NOT acknowledged: an older SE build?)"))

    def wait_probe(self, before, timeout):
        """Polls `de.display` until the probe requested after `before` probes has published its verdict."""
        t0 = time.monotonic()
        while time.monotonic() - t0 < timeout and self.alive():
            self.wait(0.3)
            rep = self.call("se", "de.display", {}, label="n0.probeState")
            st = (rep or {}).get("state") or {}
            if st.get("probesDone") is None:
                return None, None          # a stand-in or an older build: no probe state
            if st.get("probesDone", before) > before and not st.get("probeActive"):
                return st.get("probeVerdict"), st.get("probe")
        return None, "no verdict within %.0f s" % timeout

    def wait(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end and self.alive():
            self.h.poll_replies()
            time.sleep(min(0.02, max(0.0, end - time.monotonic())))

    def hold(self, seconds):
        return self.wait(seconds * (0.15 if self.q else 1.0))

    def call(self, side, cmd, args=None, timeout=None, label=None):
        t0 = time.monotonic()
        q0 = S.qpc()
        rid, st = self.h.send(side, cmd, args or {})
        self.stats["sent"] += 1
        rep = self.h.wait_reply(rid, timeout or self.a.cmd_timeout) if st == S.RW_OK else None
        self.sess.event("reply", side=side, cmd=cmd, args=args or {}, id=rid, reply=rep, qpc=q0, label=label,
                        ms=round((time.monotonic() - t0) * 1000, 1))
        if rep is None:
            self.stats["noReply"] += 1
        elif rep.get("ok"):
            self.stats["ok"] += 1
        else:
            self.stats["failed"] += 1
            err = str(rep.get("error", ""))
            if "unknown" in err.lower() and cmd not in self.stats["unknown"]:
                self.stats["unknown"].append(cmd)
        return rep

    def supported(self, rep):
        return bool(rep) and rep.get("ok")

    def state(self):
        return read_state(self.sec)

    def view(self):
        s = self.state()
        c5 = s.get("c5") or {}
        v, m = c5.get("viewState"), c5.get("approachMode")
        names = ("Space", "Approach", "Underwater", "Surfacing")
        name = names[v] if isinstance(v, int) and v < 4 else "?"
        if name == "Approach":
            name = {1: "Track", 2: "Prime"}.get(m, "Approach")
        return name, s

    def wait_view(self, want, timeout):
        t0 = time.monotonic()
        while time.monotonic() - t0 < timeout and self.alive():
            if self.view()[0] in want:
                return round(time.monotonic() - t0, 2)
            self.wait(0.1)
        return None

    def teleport(self, x, y, z, yaw=0.0, label=None):
        return self.call("se", "de.teleport", {"x": x, "y": y, "z": z, "yawDeg": yaw, "label": label or ""},
                         label=label)

    def part(self, name, phase):
        s = self.state()
        self.sess.event("part", name=name, phase=phase, qpc=S.qpc(), tick=s.get("c1Tick"),
                        view=self.view()[0])
        self.sampler.part = name if phase == "begin" else None
        self.log.say("  S2 part %s %s" % (name, phase), ev="s2_part", part=name, phase=phase)

    def owner(self, step, prompt, done_cond=None, timeout=60.0):
        """An project step: prints the instruction, waits for the condition (or the timeout); logs begin/end."""
        if not self.a.owner:
            self.sess.event("owner", step=step, phase="skipped", qpc=S.qpc())
            return False
        self.log.say("  OWNER: %s" % prompt, ev="owner_prompt", step=step)
        self.sess.event("owner", step=step, phase="begin", qpc=S.qpc(), prompt=prompt)
        t0 = time.monotonic()
        done = False
        while time.monotonic() - t0 < timeout and self.alive():
            if done_cond is not None and done_cond():
                done = True
                break
            self.wait(0.1)
        if done_cond is None:
            done = True
        self.sess.event("owner", step=step, phase="end", qpc=S.qpc(), done=done,
                        seconds=round(time.monotonic() - t0, 2))
        return done

    def c5_flag(self, name):
        c5 = (self.state().get("c5") or {})
        return bool(c5.get("flags", 0) & (1 << L.SE_STATE_FLAGS[name]))

    def c5_overlay(self, name):
        c5 = (self.state().get("c5") or {})
        return bool(c5.get("overlays", 0) & (1 << L.OVERLAYS[name]))

    def hwnds(self):
        c0 = self.state()["c0"]
        return int(c0.get("seHwnd") or 0), int(c0.get("snHwnd") or 0)

    # ------------------------------------------------------------------------------------------------ tools
    def bg_python(self, args, out_name):
        f = open(os.path.join(self.out, out_name), "w", encoding="utf-8")
        p = subprocess.Popen([sys.executable, "-I"] + args, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.bg.append(p)
        return p

    def measure(self, part, seconds):
        csv_path = os.path.join(self.out, "measure-%s.csv" % part)
        f = open(os.path.join(self.out, "measure-%s.txt" % part), "w", encoding="utf-8")
        p = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              os.path.join(TOOLS, "measure", "measure.ps1"), "-Seconds", str(int(seconds)), "-Interval",
                              "1", "-Csv", csv_path, "-Names", "Legacy,Subnautica"], stdout=f, stderr=subprocess.STDOUT,
                             stdin=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.bg.append(p)
        self.sess.event("measure", part=part, csv=os.path.basename(csv_path), seconds=seconds)
        return p

    def video(self, name, seconds, expect=None):
        if self.standin or self.a.no_video:
            self.sess.event("video", name=name, skipped="stand-in run" if self.standin else "--no-video")
            return None
        cx, cy = self.a.screen_centre
        args = [os.path.join(TOOLS, "capture", "composed_video.py"), "record", "--out", os.path.join(self.out, name),
                "--seconds", "%g" % seconds, "--fps", "60", "--probe", "centre=%d,%d" % (cx, cy),
                "--probe", "hud=%d,%d" % (cx, int(cy * 1.9)), "--guard-processes", "Legacy,Subnautica"]
        if expect:
            args += ["--expect", "centre=%d,%d,%d" % expect]
        p = self.bg_python(args, "%s.txt" % name)
        self.sess.event("video", name=name, seconds=seconds, expect=expect)
        return p

    def shot(self, name):
        if self.standin:
            return None
        path = os.path.join(self.out, "%s.png" % name)
        try:
            subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                            os.path.join(TOOLS, "capture", "desktop_shot.ps1"), "-Out", path, "-Screen", "-1",
                            "-Scale", "0.5", "-OnlyOwners", "Legacy,Subnautica"], capture_output=True, timeout=60)
        except (OSError, subprocess.TimeoutExpired):
            return None
        return os.path.basename(path) if os.path.exists(path) else None

    def file_hash(self, path, when):
        h = sha256_file(path) if os.path.exists(path) else None
        self.sess.event("file_hash", path=path, when=when, sha256=h, qpc=S.qpc())
        return h

    # ------------------------------------------------------------------------------------------------ parts
    def p_n9(self):
        """S2.N9: UI Automation taskbar buttons + window-style rules for Subnautica.exe after ReadyIdle"""
        self.part("n9", "begin")
        if self.standin:
            self.sess.event("n9", standin=True)
        else:
            r = self.UA.check("Subnautica.exe", "Subnautica")
            self.sess.event("n9", result=r)
            self.log.say("  S2.N9: %s" % r["verdict"])
            self.owner("n9.click", "click where Subnautica's taskbar button was, then try Alt+Tab to Subnautica "
                                   "(the foreground is sampled every 100 ms); 20 s", None, 20.0)
        self.part("n9", "end")

    def p_n8(self):
        """S2.N8: 60 s Prime hold at y=+0.5 m with margins on, then 60 s with de.display margins=off (10 Hz samples)"""
        x, z = S2_SPOT
        self.teleport(x, 0.5, z, label="n8.prime")
        took = self.wait_view(("Prime",), 30)
        self.wait(2.0)
        self.part("n8.on", "begin")
        self.hold(60)
        self.part("n8.on", "end")
        rep = self.call("se", "de.display", {"margins": "off"}, label="n8.marginsOff")
        self.part("n8.off", "begin")
        self.hold(60)
        self.part("n8.off", "end")
        self.call("se", "de.display", {"margins": "auto"}, label="n8.marginsAuto")
        self.sess.event("n8", primeAfterS=took, marginsOffSupported=self.supported(rep))

    def toggle_check(self, i, t0):
        se, sn = self.hwnds()
        fg = self.WK.foreground(se)
        wk = self.WK.result_json(self.WK.walk(se, sn)) if se and sn else {"ok": False, "verdict": "handles missing"}
        return {"isSe": fg["isSe"], "pid": fg["pid"], "image": fg["image"]}, wk

    def p_td1(self):
        """S2.T-D1 + S2.N0: 50 de.display toggles in Prime (walker + foreground each), menus, videos, calib"""
        # The SE plugin's override is "forced see-through OR the view machine's request" (DisplayController): toggled
 # off underwater, the machine still skips the scene. In Prime (y = +0.5 m) Subnautica already runs Full and SE
 # draws its scene unless forced, so each toggle switches the picture: SE's scene <-> Subnautica's live view.
        x, z = S2_SPOT
        self.teleport(x, 0.5, z, label="td1.spot")
        self.wait_view(("Prime",), 40)
        self.wait(3.0)
        n = self.a.toggles
        interval = 0.4 if self.q else 1.0
        vid = self.video("video-td1", n * interval + 6)
        self.wait(1.5 if vid else 0.2)
        self.part("td1", "begin")
        self.call("se", "de.display", {"readback": "once"}, label="td1.readback.scene")   # Prime: scene drawn
        self.wait(0.5)
        for i in range(n):
            if not self.alive():
                break
            t0 = S.qpc()
            m0 = time.monotonic()
            rep = self.call("se", "de.display", {"toggle": True}, label="td1.toggle%d" % (i + 1))
            self.wait(0.15)
            fg, wk = self.toggle_check(i, t0)
            if self.standin_se:
                fg["standin"] = True
            self.sess.event("toggle", index=i + 1, qpc=t0, reply=rep, fg=fg, walker=wk)
            if i == 0:
                self.call("se", "de.display", {"readback": "once"}, label="td1.readback.seeThrough")  # toggle #1 on
            self.wait(max(0.0, interval - (time.monotonic() - m0)))
        self.call("se", "de.display", {"seeThrough": "on"}, label="td1.seeThroughOn")
        for name, key in S2_MENUS:
            q0 = S.qpc()
            done = self.owner("menu." + name, "open %s (%s) now; it is captured in 5 s, then close it" % (name, key),
                              None, 5.0)
            shot = self.shot("menu-%s" % name) if done else None
            if done:
                self.call("se", "de.display", {"readback": "once"}, label="td1.readback.menu.%s" % name)
            self.sess.event("menu", name=name, key=key, qpc0=q0, qpc1=S.qpc(), shot=shot, owner=done)
        self.call("se", "de.display", {"seeThrough": "off"}, label="td1.seeThroughOff")
        self.part("td1", "end")
        if vid:
            vid.wait(timeout=120)
        # S2.N0: the calibration colour in the composed capture
        self.part("n0", "begin")
        cal = self.call("sn", "de.calib", {"colour": "%d,%d,%d" % S2_CALIB}, label="n0.calibOn")
        self.call("se", "de.display", {"seeThrough": "on"}, label="n0.seeThroughOn")
        self.wait(1.0)
        v = self.video("video-n0-on", 3.0, S2_CALIB)
        if v:
            v.wait(timeout=60)
        self.call("se", "de.display", {"seeThrough": "off"}, label="n0.seeThroughOff")
        self.wait(1.0)
        v = self.video("video-n0-off", 3.0, S2_CALIB)
        if v:
            v.wait(timeout=60)
        # review fix (finding 1): the composed-pixel probe runs while the calibration colour is still on (it
 # forces one see-through moment by itself; two GDI reads, finding 4); the verdict is a row of the table
        rep = self.call("se", "de.display", {"probe": True, "expect": list(S2_CALIB)}, label="n0.probe")
        before = (((rep or {}).get("state") or {}).get("probesDone"))
        verdict, text = self.wait_probe(before, 8.0) if before is not None else (None, None)
        self.sess.event("n0_probe", verdict=verdict, text=text, supported=self.supported(rep), qpc=S.qpc())
        self.call("sn", "de.calib", {"colour": "off"}, label="n0.calibOff")
        self.call("se", "de.display", {"seeThrough": "on"}, label="n0.seeThroughOnAnim")
        self.wait(1.0)
        v = self.video("video-n0-anim", 3.0)
        if v:
            v.wait(timeout=60)
        self.call("se", "de.display", {"seeThrough": "off"}, label="n0.release")
        self.part("n0", "end")
        self.sess.event("n0", calibSupported=self.supported(cal), probeVerdict=verdict)

    def bob(self, part, amp, hz, seconds):
        rep = self.call("se", "de.motion", {"kind": "bob", "x": 0.0, "z": 0.0, "centreY": -0.125, "amplitudeM": amp,
                                            "hz": hz, "seconds": seconds, "label": part}, label=part)
        if self.supported(rep):
            self.wait(seconds + 1.0)
            return "de.motion bob"
        # fallback: teleports at 10 Hz (each teleport re-arms streamingSettled: the trace is then not discriminating)
        self.sess.event("warning", part=part, text="de.motion not supported by SE: teleport fallback at 10 Hz (snReady "
                                                    "re-settles after each teleport; S2.N2 not discriminating)")
        t0 = time.monotonic()
        while time.monotonic() - t0 < seconds and self.alive():
            t = time.monotonic() - t0
            self.h.send("se", "de.teleport", {"x": 0.0, "y": -0.125 + amp * math.sin(2 * math.pi * hz * t), "z": 0.0,
                                              "yawDeg": 0.0})
            self.wait(0.1)
        return "teleport"

    def path(self, frm, to, speed, label):
        # a new command name on purpose: SE's S1 de.path ignores unknown keys and would start the 2 km WM-3 path
        rep = self.call("se", "de.motion", {"kind": "line", "fromSn": list(frm), "toSn": list(to), "speedMps": speed,
                                            "label": label}, label=label)
        secs = math.dist(frm, to) / speed + 1.0
        if self.supported(rep):
            self.wait(secs)
            return "de.motion line"
        self.sess.event("warning", part=label, text="de.motion not supported by SE: teleport to the end point (the "
                                                     "crossing then comes from a teleport)")
        self.teleport(*to, label=label)
        self.wait(secs)
        return "teleport"

    def p_n2(self):
        """S2.N2: de.motion bob +-0.05 m @1 Hz and +-0.1 m @5 Hz around -0.125 m (30 s each); zone exit from Underwater at 7,750 m past 7,900 m"""
        self.teleport(0.0, -1.0, 0.0, label="n2.arrive")
        self.wait_view(("Underwater",), 30)
        self.wait(2.0)
        # integration: a vsm row on every SE step for the replay oracle (the plugin otherwise logs the steps
 # around each transition and one per second; tests/s2/state_traces.py reseeds after gaps either way)
        self.call("se", "de.view", {"trace": "on"}, label="n2.traceOn")
        secs = 8.0 if self.q else 30.0
        self.part("n2a", "begin")
        how_a = self.bob("n2a", 0.05, 1.0, secs)
        self.part("n2a", "end")
        self.part("n2b", "begin")
        how_b = self.bob("n2b", 0.1, 5.0, secs)
        self.part("n2b", "end")
        # review fix (finding 7): Underwater first, at 7,750 m (the Rho guard allows Underwater only at
 # rho <= 7,800 m; the old start at 7,850 m never was Underwater, so the zone-exit row could not fail)
        self.teleport(Z_START, -2.0, 0.0, label="n2z.arrive")
        uw = self.wait_view(("Underwater",), 40 if not self.q else 15)
        self.sess.event("n2z", underwaterBeforeExit=uw is not None, waitS=uw, startX=Z_START, view=self.view()[0])
        if uw is None:
            self.log.say("  S2.N2 zone exit: NOT Underwater at x = %.0f m after the wait (the row will be MISSING)" % Z_START)
        self.wait(2.0)
        self.part("n2z", "begin")
        # review fix (finding 5): the composed video of the margins-off transition out of Underwater
        vid = self.video("video-n2z", (7960.0 - Z_START) / 10.0 + 4.0)
        self.wait(1.5 if vid else 0.0)
        how_z = self.path((Z_START, -2.0, 0.0), (7960.0, -2.0, 0.0), 10.0, "n2z.out")
        self.wait(2.0)
        if vid:
            vid.wait(timeout=120)
        self.path((7960.0, -2.0, 0.0), (Z_START, -2.0, 0.0), 10.0, "n2z.back")
        self.part("n2z", "end")
        self.call("se", "de.view", {"trace": "off"}, label="n2.traceOff")
        self.teleport(0.0, -1.0, 0.0, label="n2.home")
        self.sess.event("n2", methods={"a": how_a, "b": how_b, "zone": how_z})

    def p_n7(self):
        """S2.N7: after an Approach phase, dives at 5 m/s through de.motion {kind: line, fromSn, toSn, speedMps}"""
        self.teleport(500.0, 20.0, 500.0, label="n7a.arrive")
        self.wait(10.0 if not self.q else 3.0)
        self.call("se", "de.view", {"trace": "on"}, label="n7.traceOn")   # every step for state_traces.py --max-entry-ms
        self.part("n7a", "begin")
        how_a = self.path((500.0, 20.0, 500.0), (500.0, -10.0, 500.0), 5.0, "n7a.dive")
        self.wait(2.0)
        self.part("n7a", "end")
        self.teleport(500.0, 5.0, 500.0, label="n7b.arrive")
        self.wait(6.0 if not self.q else 3.0)
        self.part("n7b", "begin")
        how_b = self.path((500.0, 5.0, 500.0), (500.0, -10.0, 500.0), 5.0, "n7b.dive")
        self.wait(2.0)
        self.part("n7b", "end")
        self.call("se", "de.view", {"trace": "off"}, label="n7.traceOff")
        self.sess.event("n7", methods={"a": how_a, "b": how_b})

    def p_n7b(self):
        """S2.N7 re-dive: 10 quick dives +2 m -> -3 m -> +2 m at 1.5 m/s, a 6 s pause at +2 m and a dive, 12 s at +8 m and a dive"""
        # The project's re-dive defect: after lingering above the water > 3 s the machine
 # dropped Prime -> Track and the next dive showed SE for ~0.5-1 s. Windows (SE ticks, tests/s2/analyze_n7b.py):
 # n7b.quick (the quick cycles), n7b.near (the near pause and its dive), n7b.far (up to +8 m, the Track pause
 # and its dive). All moves are de.motion lines (no teleport inside a window: a teleport re-arms the settle).
        x, z = S2_SPOT
        top, bot, far = N7B_TOP, N7B_BOTTOM, N7B_FAR_TOP
        cycles = 3 if self.q else N7B_CYCLES
        self.teleport(x, top, z, label="n7b.arrive")
        arrived = self.wait_view(("Prime", "Track"), 30)
        self.wait(3.0)
        self.call("se", "de.view", {"trace": "on"}, label="n7b.traceOn")   # vsm rows for analyze_n2's replay row
 # one warm-up dive (not judged): the arrival teleport re-arms streamingSettled
        how = [self.path((x, top, z), (x, bot, z), N7B_SPEED, "n7b.warm.down"),
               self.path((x, bot, z), (x, top, z), N7B_SPEED, "n7b.warm.up")]
        self.wait(N7B_CYCLE_PAUSE_S)
        self.part("n7b.quick", "begin")
        for k in range(cycles):
            if not self.alive():
                break
            how.append(self.path((x, top, z), (x, bot, z), N7B_SPEED, "n7b.quick%d.down" % k))
            how.append(self.path((x, bot, z), (x, top, z), N7B_SPEED, "n7b.quick%d.up" % k))
            if k < cycles - 1:
                self.wait(N7B_CYCLE_PAUSE_S)       # between cycles only: the near pause follows the last one
        self.part("n7b.quick", "end")
        # the near pause: these waits are real time even with --quick (they are what the rule is about). Prime has
 # been quiet since the camera rose past +1 m: ~0.7 s of the last up line + the path's 1 s slack + the pause
        self.part("n7b.near", "begin")
        self.wait(N7B_NEAR_PAUSE_S)
        how.append(self.path((x, top, z), (x, bot, z), N7B_SPEED, "n7b.near.down"))
        self.part("n7b.near", "end")
        self.part("n7b.far", "begin")
        how.append(self.path((x, bot, z), (x, far, z), N7B_SPEED, "n7b.far.up"))
        self.wait(N7B_FAR_PAUSE_S)
        how.append(self.path((x, far, z), (x, bot, z), N7B_SPEED, "n7b.far.down"))
        self.part("n7b.far", "end")
        how.append(self.path((x, bot, z), (x, top, z), N7B_SPEED, "n7b.home"))
        self.call("se", "de.view", {"trace": "off"}, label="n7b.traceOff")
        st = self.call("se", "de.view", {}, label="n7b.viewState")
        self.sess.event("n7b", cycles=cycles, nearPauseS=N7B_NEAR_PAUSE_S, farPauseS=N7B_FAR_PAUSE_S,
                        cyclePauseS=N7B_CYCLE_PAUSE_S, speedMps=N7B_SPEED, arrivedS=arrived,
                        methods=sorted(set(how)), bandHolds=((st or {}).get("state") or {}).get("bandHolds"))

    def p_n3(self):
        """S2.N3: 60 s in Space at +400 m with measure.ps1 (1 s) and C7 samples"""
        self.teleport(0.0, 400.0, 0.0, label="n3.space")
        self.wait_view(("Space",), 20)
        self.wait(10.0 if not self.q else 2.0)
        secs = 60.0 if not self.q else 8.0
        self.part("n3", "begin")
        self.measure("n3", secs)
        self.wait(secs + 1.0)
        self.part("n3", "end")

    def p_n4(self):
        """S2.N4: 60 s above water (+3 m) and 60 s underwater (-30 m) at the same spot with measure.ps1"""
        x, z = S2_SPOT
        secs = 60.0 if not self.q else 8.0
        for sub, y in (("n4.above", 3.0), ("n4.under", -30.0)):
            self.teleport(x, y, z, label=sub)
            if y < 0:
                self.wait_view(("Underwater",), 30)
            self.wait(10.0 if not self.q else 2.0)
            self.part(sub, "begin")
            self.measure(sub, secs)
            self.wait(secs + 1.0)
            self.part(sub, "end")

    def p_n11(self):
        """S2.N11: 60 s hovering at +50 m, 60 s sailing at +1.5 m (de.motion line, 5 m/s) with measure.ps1"""
        secs = 60.0 if not self.q else 8.0
        self.teleport(0.0, 50.0, 0.0, label="n11.hover")
        self.wait(8.0 if not self.q else 2.0)
        self.part("n11.hover", "begin")
        self.measure("n11.hover", secs)
        self.wait(secs + 1.0)
        self.part("n11.hover", "end")
        self.teleport(0.0, 1.5, 0.0, label="n11.sail")
        self.wait(5.0 if not self.q else 2.0)
        self.part("n11.sail", "begin")
        self.measure("n11.sail", secs)
        self.path((0.0, 1.5, 0.0), (5.0 * secs, 1.5, 0.0), 5.0, "n11.sail")
        self.part("n11.sail", "end")

    def p_n6(self):
        """S2.N6: OUTER VOID at SN x=+2,500 m (de.biome asks LargeWorld.GetBiome there: "void")"""
        self.teleport(2500.0, -50.0, 0.0, label="n6.outerVoid")
        self.wait(10.0 if not self.q else 3.0)
        self.part("n6", "begin")
        # review fix (finding 8): a direct query, not the cumulative counter of the DE.S2 sec line
        rep = self.call("sn", "de.biome", {"x": 2500.0, "y": -50.0, "z": 0.0}, label="n6.biome")
        inside = self.call("sn", "de.biome", {"x": -1029.0, "y": -50.0, "z": -1116.0}, label="n6.biomeInside")
        self.sess.event("n6_biome", reply=rep, inside=inside, supported=self.supported(rep))
        self.wait(5.0)
        self.part("n6", "end")

    def p_ta1(self):
        """S2.T-A1: 10 dive/surface cycles by de.teleport with tools/audio/peaks.py; de.audio per cycle; project steps"""
        x, z = S2_SPOT
        cycles = 10
        dur = cycles * (13 if not self.q else 5) + (240 if self.a.owner else 20)
        args = [os.path.join(TOOLS, "audio", "peaks.py"), "--out", os.path.join(self.out, "peaks-ta1.jsonl"),
                "--seconds", str(int(dur))]
        if self.standin:
            args += ["--backend", "section", "--name", self.a.name]
        pk = self.bg_python(args, "peaks-ta1.txt")
        self.sess.event("peaks", path="peaks-ta1.jsonl", seconds=dur)
        self.wait(2.0)
        self.part("ta1", "begin")
        for k in range(cycles):
            if not self.alive():
                break
            self.teleport(x, 2.0, z, label="ta1.up%d" % k)
            self.wait(5.0 if not self.q else 1.5)
            self.teleport(x, -3.0, z, label="ta1.down%d" % k)
            self.wait(3.0 if not self.q else 1.5)
            self.call("se", "de.audio", {}, label="ta1.cycle%d" % k)
            self.wait(4.0 if not self.q else 1.0)
        if self.a.owner:
            self.owner("escUnder", "press ESC now (underwater), wait 5 s, press ESC again", lambda: self.c5_overlay(
                "Paused"), 30.0)
            self.owner("escUnder.resume", "resume (ESC)", lambda: not self.c5_overlay("Paused"), 30.0)
            self.owner("altTab", "Alt+Tab away from SE now, stay away 5 s, then come back to SE",
                       lambda: self.c5_overlay("Unfocused"), 30.0)
            self.owner("altTab.back", "come back to SE", lambda: not self.c5_overlay("Unfocused"), 60.0)
            self.wait(2.0)
            self.call("se", "de.audio", {}, label="ta1.afterAltTab")
            self.owner("save", "save the game in SE now (ESC -> Save), underwater",
                       lambda: self.state()["c0"].get("sePhase") == L.SE_PHASE["Saving"], 60.0)
            self.wait(5.0)
            self.teleport(x, 400.0, z, label="ta1.space")
            self.wait(8.0)
            self.owner("escSpace", "press ESC now (in Space), wait 5 s, then resume with ESC",
                       lambda: self.c5_overlay("Paused"), 30.0)
            self.owner("escSpace.resume", "resume (ESC)", lambda: not self.c5_overlay("Paused"), 30.0)
            self.wait(6.0)
        self.part("ta1", "end")
        self.wait(1.0)
        if pk.poll() is None:
            pk.terminate()           # peaks.py flushes about once per second
            pk.wait(timeout=10)

    def p_n5(self):
        """S2.N5: Notepad between the games 5 times + walker; project Alt+Tab away/back (else de.zorder reassert)"""
        if not self.a.owner and not self.standin:
            self.sess.event("n5", skipped="needs the owner (--owner)")
            return
        self.part("n5", "begin")
        for i in range(5):
            se, sn = self.hwnds()
            if not (se and sn):
                self.sess.event("n5", index=i + 1, error="handles missing")
                break
            rect = (-29000, -29000, 400, 300) if self.standin else (300, 300, 900, 600)
            rec = self.NB.place(se, sn, rect)
            placed = rec.get("walker")
            if self.a.owner and not self.standin:
                self.owner("n5.away%d" % (i + 1), "Alt+Tab away from SE now (to the Notepad), then come back to SE",
                           lambda: not self.WK.foreground(se)["isSe"], 60.0)
                self.owner("n5.back%d" % (i + 1), "come back to SE", lambda: self.WK.foreground(se)["isSe"], 60.0)
            else:
                self.call("se", "de.zorder", {"reassert": True}, label="n5.reassert%d" % (i + 1))
            t0 = time.monotonic()
            restored = None
            while time.monotonic() - t0 < 3.0:
                if self.WK.walk(se, sn)["ok"]:
                    restored = round(time.monotonic() - t0, 3)
                    break
                time.sleep(0.05)
            closed = self.NB.close(rec.get("hwnd", 0))
            rec.pop("hwnd", None)
            self.sess.event("n5", index=i + 1, placed=rec.get("placed"), walkerAfterPlace=placed,
                            restoredAfterS=restored, closed=closed, owner=bool(self.a.owner and not self.standin))
        self.part("n5", "end")

def s2_launcher_rows(sess_dir, C) -> list:
    """The S2 rows the launcher computes itself: S2.N9, S2.N10, S2.N5, S2.N6, and the OTHER rows."""
    rows = []
    sess = C.Session(sess_dir)
    events = sess.session
    start = next((r for r in events if r.get("ev") == "start"), {})
    standin = bool((start.get("standin") or {}).get("sn") or (start.get("standin") or {}).get("se"))
    parts = set(sess.parts)
    if "n9" in parts:
        n9 = next((r for r in events if r.get("ev") == "n9"), None)
        if not n9 or n9.get("standin"):
            C.row(rows, "S2.N9", "OTHER", "stand-in run" if n9 else "not run", "real Subnautica",
                  "no Subnautica taskbar button and no Alt+Tab entry after ReadyIdle (UI Automation)", "n9",
                  required=False)
        else:
            r = n9["result"]
            C.row(rows, "S2.N9", "PASS" if r.get("green") else ("FAIL" if r.get("processRunning") else "MISSING"),
                  r.get("verdict", "")[:60], "no button, no entry",
                  "no Subnautica taskbar button and no Alt+Tab entry after ReadyIdle (UI Automation + style rules)",
                  "n9", windows=r.get("windows"))
    # S2.N10 from the foreground watcher
    fgs = sorted((r for r in events if r.get("ev") == "fg"), key=lambda r: r["qpc"])
    phases = sorted((r for r in events if r.get("ev") == "phase"), key=lambda r: r["qpc"])
    f = float(sess.qpcf)
    menu_q = next((p["qpc"] for p in phases if p.get("sePhase") == "Menu"), None)
    ready_q = next((p["qpc"] for p in phases if p.get("snPhase") in ("ReadyIdle", "WarmingUp", "Active")), None)

    def fg_at(q):
        cur = None
        for r in fgs:
            if r["qpc"] <= q:
                cur = r
            else:
                break
        return cur
    if standin:
        C.row(rows, "S2.N10", "OTHER", "stand-in run", "real games", "start-up foreground", "n10", required=False)
    elif menu_q is None or not fgs:
        C.row(rows, "S2.N10", "MISSING", "SE menu phase or foreground samples missing", "SE foreground",
              "5 s after SE's main menu appears, GetForegroundWindow() == seHwnd", "n10")
    else:
        at = fg_at(menu_q + 5 * f)
        img = (at or {}).get("image", "?")
        C.row(rows, "S2.N10", "PASS" if img and img.lower() in ("legacy.exe", "spaceengineers.exe") else "FAIL",
              "foreground %s at menu + 5 s" % img, "SE (Legacy.exe)",
              "5 s after SE's main menu appears, GetForegroundWindow() == seHwnd", "n10")
        sn_fg = [r for r in fgs if ready_q is not None and r["qpc"] >= ready_q and
                 str(r.get("image", "")).lower() == "subnautica.exe"]
        C.row(rows, "S2.N10", "PASS" if ready_q is not None and not sn_fg else ("MISSING" if ready_q is None else "FAIL"),
              "%d Subnautica foreground changes after ReadyIdle" % len(sn_fg), "0 (100 ms samples)",
              "Subnautica never foreground after ReadyIdle", "n10")
    if "n5" in parts:
        n5 = [r for r in events if r.get("ev") == "n5" and r.get("index")]
        ok = [r for r in n5 if r.get("restoredAfterS") is not None and r.get("placed")]
        red = [r for r in n5 if (r.get("walkerAfterPlace") or {}).get("ok") is False]
        owner = any(r.get("owner") for r in n5)
        if not n5:
            C.row(rows, "S2.N5", "MISSING", "not run (needs --owner)", "5/5", "Alt+Tab away and back with Notepad "
                  "between: SE re-raised, Subnautica directly under SE within 3 retries", "n5")
        else:
            C.row(rows, "S2.N5", "PASS" if len(ok) == 5 and owner else ("OTHER" if not owner else "FAIL"),
                  "%d/5 restored (%s)" % (len(ok), "owner Alt+Tab" if owner else "de.zorder re-assert, no Alt+Tab"),
                  "5/5 within 3 retries", "Notepad between, Alt+Tab away and back: Subnautica directly under SE again",
                  "n5", required=owner)
            C.row(rows, "S2.M9", "PASS" if len(red) == len(n5) else "FAIL", "%d/%d walker red with Notepad between" % (
                len(red), len(n5)), "red every time", "seeded S2.M9: the z-order oracle turns red with a window "
                                                      "between the games", "n5")
    if "n6" in parts and any(r.get("ev") == "n6_biome" and (r.get("reply") or {}).get("ok") for r in events):
        # review fix (finding 8): the direct GetBiome query of the n6 part decides
        q = next(r for r in events if r.get("ev") == "n6_biome" and (r.get("reply") or {}).get("ok"))
        rep = q["reply"]
        if rep.get("standin") or standin:
            C.row(rows, "S2.N6", "OTHER", "stand-in run (emulated de.biome: %s)" % rep.get("biome"), "real Subnautica",
                  "OUTER VOID biome at SN x = +2,500 m (LargeWorld.GetBiome patched)", "n6", required=False)
        else:
            # "void" from the patch itself: the unpatched lookup wraps east of the tile and may also land on a void
 # cell, so S2.M5 is red through answeredByPatch even then
            good = str(rep.get("biome")).lower() == "void" and rep.get("answeredByPatch") is True
            C.row(rows, "S2.N6", "PASS" if good else "FAIL",
                  "GetBiome(2500, -50, 0) = %r (outsideTile=%s, answeredByPatch=%s)" % (
                      rep.get("biome"), rep.get("outsideTile"), rep.get("answeredByPatch")), "void, by the patch",
                  "OUTER VOID biome at SN x = +2,500 m (LargeWorld.GetBiome patched; de.biome direct query)", "n6")
            ins = q.get("inside") or {}
            if ins.get("ok"):
                C.row(rows, "S2.N6", "INFO", "GetBiome(-1029, -50, -1116) = %r" % ins.get("biome"), "not void",
                      "inside the tile the vanilla biome answers (grandReef spot)", "n6", required=False)
    elif "n6" in parts:
        b0, b1 = sess.window("n6")
        lines = [(e.get("qpc"), e) for e in C.events(sess.sn_raw, ("s1.", "s2."))]
        txt = []
        for r in sess.sn_raw:
            t = r.get("text") or r.get("msg") or ""
            q = sess.utc_to_qpc(r.get("t")) if not isinstance(r.get("qpc"), int) else r["qpc"]
            if b0 is not None and q is not None and b0 <= q <= b1 and "biome" in t:
                txt.append(t)
        # the void counter of the DE.S2 sec line is cumulative since start (finding 8): its increase inside the window
        cnt = [int(m.group(1)) for m in (re.search(r"biome\{void=(\d+)", t) for t in txt) if m]
        void = [t for t in txt if "biome=void" in t or "biome=\"void\"" in t]
        if len(cnt) >= 2 and cnt[-1] > cnt[0]:
            void.append("void answers +%d inside the n6 window" % (cnt[-1] - cnt[0]))
        probes = [e for _q, e in lines if e.get("_ev") == "s1.probe" and b0 is not None and e.get("qpc") and
                  b0 <= e["qpc"] <= b1]
        void += [e for e in probes if str(e.get("biome")).lower() == "void"]
        if standin:
            C.row(rows, "S2.N6", "OTHER", "stand-in run (%d emulated readings)" % (len(txt) + len(probes)),
                  "real Subnautica", "OUTER VOID biome at SN x = +2,500 m (LargeWorld.GetBiome patched)", "n6",
                  required=False)
        else:
            C.row(rows, "S2.N6", "PASS" if void else ("MISSING" if not txt and not probes else "FAIL"),
                  "%d biome readings, %d void" % (len(txt) + len(probes), len(void)), "void",
                  "OUTER VOID biome at SN x = +2,500 m (LargeWorld.GetBiome patched)", "n6")
    for cid, what, src in S2_OTHER_ROWS:
        C.row(rows, cid, "OTHER", "", src[:26], what, "other", required=False)
    return rows

def start_se_alone(log: Log, a, out: str, se_extra_env=None):
    """The SE-alone baseline of S2.N3 / S2.N11 ("SE frame time within 5 % of SE alone"): a passive stand-in host
 (tests/standins/fake_sn_host.py --passive: creates the section, then writes nothing, so SE links but sees no live
 Subnautica: renderer offline, view state Space) and SE as usual. No Subnautica process. Returns (rc, host, se).
 `se_extra_env` (s2b: DEEPENGINEERS_MUTANTS) goes into SE's environment only."""
    if not a.skip_preflight and preflight(log, a) != 0:
        log.say("REFUSE %s --se-alone: preflight failed" % getattr(a, "cmd", "s2"))
        return 1, None, None
    host_cmd = [sys.executable, "-I", os.path.join(REPO, "tests", "standins", "fake_sn_host.py"), "--name", a.name,
                "--no-token-check", "--passive", "--hold-seconds", "7200", "--quiet"]
    log.event("session_start", name=a.name, standin=True, seAlone=True)
    host = popen_detached(host_cmd, REPO, dict(os.environ), os.path.join(out, "sn-passive-host.txt"))
    t0 = time.monotonic()
    while time.monotonic() - t0 < 15 and S.section_exists(a.name)[0] != "present":
        time.sleep(0.25)
    _sn, se_cmd = session_commands(a)
    se_env = dict(os.environ)
    se_env.pop(L.BRIDGE_ENV, None)
    se_env.setdefault("DEEPENGINEERS_S5", "off")   # review F9: pre-S5 sessions run without the S5 mirror; s5/tp1 set it on
    se_env.setdefault("DEEPENGINEERS_S6", "off")   # integrator S6: pre-S6 sessions without S6; s6 sets it on
    se_env.setdefault("DEEPENGINEERS_S11", "off")   # S11 hook: pre-S11 SE-alone sessions without S11 (explosion rows ungrouped); s11 sets it on
    se_env.setdefault("DEEPENGINEERS_WATERLOOK", "off")   #: scripted sessions keep the contract's flat water; `run` passes on
    se_env.setdefault("DEEPENGINEERS_S8A_TREADMILL", "off")   # S8a: SE-alone baselines keep chart 0
    se_env.setdefault("DEEPENGINEERS_PICTURE", "waterline")   # integrator S7a: SE-alone sessions are pre-S7a ones (no Subnautica picture)
    se_env.setdefault("DEEPENGINEERS_TIMESYNC", "off")   # review S7a F3
    se_env.setdefault("DEEPENGINEERS_GHOST", "off")
    se_env.update(se_extra_env or {})
    try:
        se = popen_detached(se_cmd, PULSAR if not a.se_cmd else REPO, se_env,
                            os.path.join(out, "se-standin.jsonl") if a.se_cmd else None)
    except OSError as e:
        # never leave the passive host holding the section (our own Python process, never a game)
        log.say("SE-alone: SE not started (%s); passive host pid %d terminated" % (e, host.pid), ev="se_start_failed")
        S.terminate_pid(host.pid, 0)
        return 1, None, None
    log.say("S2 SE-alone: passive host pid %d, SE pid %d%s" % (host.pid, se.pid, "; SE env %s" % json.dumps(
        se_extra_env) if se_extra_env else ""), ev="se_alone", host=host.pid, se=se.pid, seExtraEnv=se_extra_env)
    S.allow_set_foreground_window(se.pid)
    return None, host, se

def wait_se_in_world(log: Log, a, se, timeout) -> bool:
    """SE linked and in world (C0 sePhase InWorld with a fresh heartbeat): the SE-alone wait (no hello: no peer)."""
    t0 = time.monotonic()
    while time.monotonic() - t0 < timeout:
        if se.poll() is not None:
            return False
        sec = S.Section.open(a.name, readonly=True)
        if sec is not None:
            try:
                if sec.mapped_size >= L.HEADER_SIZE and sec.hdr("sePhase") == L.SE_PHASE["InWorld"] and \
                        S.tick64() - sec.hdr("seHeartbeatMs") < 2000:
                    log.say("S2 SE-alone: SE in world after %.0f s" % (time.monotonic() - t0))
                    return True
            finally:
                sec.close()
        time.sleep(1.0)
    log.say("S2 SE-alone: SE not in world after %.0f s" % timeout)
    return False

def s2_analyze(log: Log, a, out: str) -> int:
    C, TD1, TA1, N2, PERF, N7B = s2_modules()
    sess = C.Session(out)
    baseline = PERF.load_baseline(getattr(a, "baseline", None))
    rows = []
    for name, fn in (("td1", lambda: TD1.evaluate(sess)), ("n2", lambda: N2.evaluate(sess)),
                     ("n7b", lambda: N7B.evaluate(sess)),
                     ("perf", lambda: PERF.evaluate(sess, baseline)), ("ta1", lambda: TA1.evaluate(sess)),
                     ("launcher", lambda: s2_launcher_rows(out, C))):
        try:
            rows += fn()
        except Exception as e:     # an analyser bug must not hide the other rows
            C.row(rows, "S2.%s" % name, "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120], name)
    strict = not getattr(a, "lenient", False)
    ok = C.verdict(rows, strict)
    text = C.table(rows)
    res = {"tool": "launcher s2", "out": out, "ok": ok, "parts": sess.parts, "rows": rows,
           "baseline": PERF.baseline_from_session(sess)}
    with open(os.path.join(out, "s2-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(out, "s2-table.txt"), "w", encoding="utf-8") as f:
        f.write(text + "\n")
    print("\nS2 ORACLE TABLE (%s)  (* = not required; OTHER/MANUAL = owner or another session)" % out)
    print(text)
    log.say("S2 ORACLE %s: %d rows, %d FAIL, %d MISSING -> %s" % (
        "PASS" if ok else "FAIL", len(rows), sum(1 for r in rows if r["status"] == "FAIL"),
        sum(1 for r in rows if r["status"] == "MISSING" and r.get("required", True)),
        os.path.join(out, "s2-results.json")), ev="s2_result", ok=ok)
    return 0 if ok else 1

def s2(log: Log, a) -> int:
    if a.analyze:
        return s2_analyze(log, a, a.analyze)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(REPO, "tests", "out", "s2", stamp)
    parts = [x.strip() for x in a.parts.split(",") if x.strip()] if a.parts else (
        ["n3", "n11"] if a.se_alone else list(S2_DEFAULT_PARTS))
    unknown = [x for x in parts if x not in S2_PARTS]
    if unknown:
        log.say("unknown parts %s (known: %s)" % (unknown, ",".join(S2_PARTS)))
        return 2
    a.cont = not a.no_continue
    sn_cmd, se_cmd = session_commands(a)
    log.say("S2 session %s: parts %s%s%s; Subnautica: %s; SE: %s; out %s" % (
        stamp, ",".join(parts), " (quick)" if a.quick else "", " (owner steps)" if a.owner else "",
        "none (SE alone: a passive stand-in host keeps the section)" if a.se_alone else subprocess.list2cmdline(sn_cmd),
        subprocess.list2cmdline(se_cmd), out), ev="s2_plan")
    if a.dry_run:
        for p in parts:
            print("  part %-5s %s" % (p, (getattr(S2Runner, "p_" + p).__doc__ or "").strip().split("\n")[0]))
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    sess = S2SessionLog(os.path.join(out, "s2-session.jsonl"))
    standin_sn, standin_se = bool(a.sn_cmd), bool(a.se_cmd)
    sess.event("start", stamp=stamp, parts=parts, name=a.name, argv=sys.argv[1:], quick=a.quick, owner=a.owner,
               seAlone=a.se_alone,
               standin={"sn": standin_sn, "se": standin_se}, snCmd=sn_cmd, seCmd=se_cmd, qpcFrequency=S.QPC_FREQUENCY)
    hash_wcs = "ta1" in parts and not standin_se
    if hash_wcs:
        sess.event("file_hash", path=WATER_CLIENT_SETTINGS, when="before", qpc=S.qpc(),
                   sha256=sha256_file(WATER_CLIENT_SETTINGS) if os.path.exists(WATER_CLIENT_SETTINGS) else None)
    S.fine_timer(True)          # 1 ms sleeps for the crossing watcher (timeBeginPeriod; ended in the finally below)
    fgw = ForegroundWatcher(a.name, sess).start()
    xw = None
    t_start = time.time()
    if a.se_alone:
        rc, sn, se = start_se_alone(log, a, out)
    else:
        rc, sn, se = start_games(log, a, stdout_dir=out)
    runner = None
    try:
        in_world = rc is None and (wait_se_in_world(log, a, se, a.world_timeout) if a.se_alone else
                                   wait_in_world(log, sess, a, sn, se))
        if in_world:
            xw = CrossingWatcher(a.name, sess).start()
            runner = S2Runner(log, a, sess, sn, se, out, standin_sn, standin_se)
            ok, why = runner.attach()
            if not ok:
                log.say("S2: harness not usable: %s" % why, ev="s2_harness_refused")
                rc = 1
            else:
                runner.set_auto_fallback(a.auto_fallback)
                for p in parts:
                    if not runner.alive():
                        log.say("S2: a game exited before part %s" % p, ev="s2_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:
                        log.say("S2 part %s failed: %r" % (p, e), ev="s2_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
                log.say("S2 script finished: %s" % json.dumps(runner.stats), ev="s2_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if xw:
            xw.stop()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            close_session(log, a, sn, se, standin_se, standin_sn or a.se_alone)
        if a.se_alone and sn is not None and sn.poll() is None:
            S.terminate_pid(sn.pid, 0)       # the passive stand-in host (our own Python process, never a game)
        fgw.stop()
        S.fine_timer(False)
        if hash_wcs:
            sess.event("file_hash", path=WATER_CLIENT_SETTINGS, when="after", qpc=S.qpc(),
                       sha256=sha256_file(WATER_CLIENT_SETTINGS) if os.path.exists(WATER_CLIENT_SETTINGS) else None)
        sess.event("end", rc=rc, stats=runner.stats if runner else None)
        sess.close()
        collect(log, out, t_start, sn, standin_sn, standin_se, stamp)
    if rc is not None:
        log.say("S2 session aborted (rc %d); analysing what was collected" % rc)
    res = s2_analyze(log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# s2b: one scripted S2b swimming session in SE alone
# ----------------------------------------------------------------------------------------------------------------

S2B_DIR = os.path.join(REPO, "tests", "s2b")
S2B_PARTS = ("n1", "n2", "n3", "n4")
S2B_SPOT = (0.0, 0.0)            # SN x, z: the site centre, open water (SE's flat floor is at -3,000 m, CONTRACT §3)
S2B_SETTLE_S = 3.0               # after a teleportChar: the controller takes the character, the velocity settles
S2B_ASCENT_MPS = 4.5             # planning speed for "swim up to the surface": the model's steady vertical speed is
# 6.3 m/s x (1 - 2.5 x 0.1 s) = 4.7 m/s (tests/s2b/sn_reference.py derived)
# A segment is (label, seconds, move, sprint[, jetpack request]); move = (strafe x, vertical y, forward z) as
# Subnautica's GameInput.GetMoveDirection. S2b.N1 runs at -50 m in two scripts with a refill before each: Subnautica's
# diver holds 45 s of O2 without a tank and the whole sequence (47 s plus the arrivals) would empty it (the character
# would then suffocate in SE).
S2B_N1_Y = -50.0                 # character origin (feet), SN y: the eye is about 1.7 m higher
S2B_N1_SCRIPTS = (
    ("n1a", (("n1.fwd", 10.0, (0, 0, 1), False), ("n1.rel1", 5.0, (0, 0, 0), False),
             ("n1.strafe", 5.0, (1, 0, 0), False), ("n1.rel2", 5.0, (0, 0, 0), False))),
    ("n1b", (("n1.up", 3.0, (0, 1, 0), False), ("n1.down", 3.0, (0, -1, 0), False),
             ("n1.rel3", 3.0, (0, 0, 0), False), ("n1.back", 5.0, (0, 0, -1), False),
             ("n1.rel5", 4.0, (0, 0, 0), False), ("n1.sprint", 5.0, (0, 0, 1), True),
             ("n1.rel4", 5.0, (0, 0, 0), False))),
)
# S2b.N2 from -20 m: a jetpack request with the head under water (must be refused: S2b.M2 leaves it on), then vertical
# up held 20 s (the surface clamp); part n2j: at the surface a jetpack request (must be granted with the head out, which
# ends the swim: exit reason "jetpack" in the SE plugin's policy), then the jetpack off and a fall back in
S2B_N2_Y, S2B_N2_UP_S, S2B_N2_DUMPS_S = -20.0, 20.0, (0.5, 1.5, 5.0, 10.0, 15.0, 20.0)
S2B_N2_SCRIPT = (("n2.jetreq", 1.0, (0, 0, 0), False, True), ("n2.up", S2B_N2_UP_S, (0, 1, 0), False, None),
                 ("n2.rel", 1.0, (0, 0, 0), False, None))
S2B_N2J_SCRIPT = (("n2j.up", 2.0, (0, 1, 0), False, None), ("n2j.jet", 1.0, (0, 0, 0), False, True),
                  ("n2j.off", 3.0, (0, 0, 0), False, False))
# S2b.N3 entry/exit, the scripted vertical oscillation across the surface: per cycle swim up into the surface clamp,
# jetpack on with vertical input (SE's jetpack lifts the eye past +0.8 m: one exit, reason jetpack), jetpack off and
# fall back in (one entry). de.swim has no kinematic oscillation, and a teleportChar keeps the swim latch (no
# transition), so the crossing is driven by the script itself (the ROADMAP's "jump out" case: jetpack + up)
S2B_N3_START_Y, S2B_N3_CYCLES = -3.0, 4
S2B_N3_CYCLE = (("up", 2.5, (0, 1, 0), False, None), ("jet", 1.5, (0, 1, 0), False, True),
                ("fall", 3.0, (0, 0, 0), False, False))
S2B_N4_Y, S2B_N4_DIVE_S, S2B_N4_SURFACE_S, S2B_N4_DUMP_S = -30.0, 60.0, 20.0, 5.0
S2B_REFILL_Y = -3.0              # a refill: teleportChar here, swim up into the surface clamp, wait for a full tank
S2B_PREP_Y = 2.0                 # the suit preparation: feet here (eye ~ +3.7 m, out of the water), jetpack off, fall in
S2B_PREP_SCRIPT = (("prep.jetoff", 0.5, (0, 0, 0), False, False), ("prep.fall", 3.0, (0, 0, 0), False))
S2B_MIN_H2 = 0.25                # precheck: hydrogen fill for the session's jetpack uses (n2, n2j, n3, the M3 hold)
S2B_SCRIPT_WAIT_MAX_S = 60.0     # after a script's planned end: poll the dump until the plugin reports no script
S2B_SE_ENV = {"DEEPENGINEERS_SWIM": "standby"}   # the model starts off; the first command switches it on
SE_CFG = os.path.join(SE_APPDATA, "SpaceEngineers.cfg")
S2B_O2_KEYS = ("o2", "o2Frac", "oxygen", "suitO2", "o2Level", "suitOxygenLevel")
S2B_SEGMENT_KEYS = ("seconds", "forward", "strafe", "vertical", "sprint", "jetpack", "label")   # SwimScript.ParseObject

def s2b_n3_cycles(a):
    return a.n3_cycles if a.n3_cycles else (2 if a.quick else S2B_N3_CYCLES)

def s2b_analyzer():
    for p in (S2B_DIR, S2_DIR):
        if p not in sys.path:
            sys.path.insert(0, p)
    import analyze_swim  # noqa: E402
    return analyze_swim

def s2b_precheck_problems(rep):
    """The session's preconditions from a de.swim dump: a list of problems (empty = ok)
 and the suit fields read."""
    suit = rep.get("suit") if isinstance(rep, dict) and isinstance(rep.get("suit"), dict) else None
    if suit is None or "helmet" not in suit:
        return ["the de.swim dump carries no suit fields (an SE build without the 2026-10-16 review fixes?)"], suit or {}
    out = []
    if suit.get("error"):
        out.append("suit fields unreadable: %s" % suit.get("error"))
    if suit.get("helmet") is not True:
        out.append("helmet open: close it (SE's suit breathes only with the helmet closed; the Water Mod drowns an "
                   "open helmet under water)")
    if suit.get("jetpackPowered") is not True:
        out.append("jetpack not powered (hydrogen %s): TurnOnJetpack refuses, N2/N3 and S2b.M2 cannot be judged" %
                   suit.get("h2Level"))
    h2 = suit.get("h2Level")
    if isinstance(h2, (int, float)) and not isinstance(h2, bool) and h2 < S2B_MIN_H2:
        out.append("hydrogen %.0f %% below %.0f %%" % (100.0 * h2, 100.0 * S2B_MIN_H2))
    o2b = suit.get("o2Bottles")
    if not isinstance(o2b, (int, float)) or isinstance(o2b, bool):
        out.append("O2 bottle count unknown")
    elif o2b > 0 and suit.get("survival") is not False:
        # the automatic refill from a carried bottle exists in Survival only; in Creative the bottle cannot touch N4, so it is a note, not a refusal
        out.append("%d O2 bottle(s) carried: SE refills the suit from them below 50 %% (breaks N4); remove them" % o2b)
    return out, suit

def _o2_of(rep):
    """The O2 fraction (0..1) of a de.swim dump / o2=dump reply (top level, "state" or "o2" objects), or None."""
    if not isinstance(rep, dict):
        return None
    for d in (rep, rep.get("state"), rep.get("o2") if isinstance(rep.get("o2"), dict) else None, rep.get("swim")):
        if not isinstance(d, dict):
            continue
        for k in S2B_O2_KEYS + ("fraction", "level"):
            v = d.get(k)
            if isinstance(v, (int, float)) and not isinstance(v, bool):
                return float(v) / 100.0 if v > 1.0 + 1e-9 else float(v)
    return None

class S2bRunner:
    """The scripted S2b parts through the C11 harness and the SE plugin's de.swim command (SE alone). Never touches a
 game window: the character is driven by de.swim's scripted-input source."""

    def __init__(self, log, a, sess, host, se, out):
        self.log, self.a, self.sess, self.host, self.se, self.out = log, a, sess, host, se, out
        self.q = a.quick
        self.sec = S.Section.open(a.name)
        self.h = S.Harness(self.sec) if self.sec else None
        self.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
        self.x, self.z = S2B_SPOT

    # ------------------------------------------------------------------------------------------------ plumbing
    def alive(self):
        return self.se.poll() is None

    def attach(self):
        if self.h is None:
            return False, "section absent"
        ok, why = self.h.attach()
        if not ok:
            return False, why
        if self.call("de.ping", {}, label="ping") is None:
            return False, "SE does not answer C11 (dev build needed)"
        return True, ""

    def close(self):
        if self.h:
            self.h.detach()
        if self.sec:
            self.sec.close()

    def wait(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end and self.alive():
            self.h.poll_replies()
            time.sleep(min(0.02, max(0.0, end - time.monotonic())))

    def call(self, cmd, args=None, timeout=None, label=None):
        t0 = time.monotonic()
        q0 = S.qpc()
        rid, st = self.h.send("se", cmd, args or {})
        self.stats["sent"] += 1
        rep = self.h.wait_reply(rid, timeout or self.a.cmd_timeout) if st == S.RW_OK else None
        self.sess.event("reply", side="se", cmd=cmd, args=args or {}, id=rid, reply=rep, qpc=q0, label=label,
                        ms=round((time.monotonic() - t0) * 1000, 1))
        if rep is None:
            self.stats["noReply"] += 1
        elif rep.get("ok"):
            self.stats["ok"] += 1
        else:
            self.stats["failed"] += 1
            err = str(rep.get("error", ""))
            if "unknown" in err.lower() and cmd not in self.stats["unknown"]:
                self.stats["unknown"].append(cmd)
        return rep

    @staticmethod
    def supported(rep):
        return bool(rep) and bool(rep.get("ok"))

    def tick(self):
        try:
            return read_state(self.sec).get("c1Tick")
        except Exception:     # a read problem never stops the session
            return None

    def part(self, name, phase):
        self.sess.event("part", name=name, phase=phase, qpc=S.qpc(), tick=self.tick())
        self.log.say("  S2b part %s %s" % (name, phase), ev="s2b_part", part=name, phase=phase)

    def swim(self, args, label):
        return self.call("de.swim", args, label=label)

    def teleport_char(self, y, label):
        rep = self.swim({"teleportChar": [self.x, y, self.z], "yawDeg": 0.0}, label)
        if not self.supported(rep):
            self.log.say("  S2b %s: de.swim teleportChar not acknowledged: %s" % (label, json.dumps(rep)[:200] if rep
                                                                                   else "no reply"), ev="s2b_problem")
        return rep

    def dump(self, label):
        return self.swim({"dump": True}, label)

    def o2_dump(self, label):
        rep = self.swim({"o2": "dump"}, label)
        o2 = _o2_of(rep)
        self.sess.event("o2", label=label, o2=o2, qpc=S.qpc(), tick=self.tick(), reply=rep)
        return o2

    @staticmethod
    def segments(segs):
        """The plan of a script: (label, seconds, move [strafe, vertical, forward], sprint[, jetpack request]) ->
 de.swim script steps (SwimScript.ParseObject keys; "move" is kept for the session log and ignored by SE;
 lookYawDeg/lookPitchDeg are left out: the first step looks level at yaw 0, as the teleport faces)."""
        out = []
        for seg in segs:
            lab, secs, move, sprint = seg[:4]
            jet = seg[4] if len(seg) > 4 else None
            d = {"label": lab, "seconds": secs, "forward": float(move[2]), "strafe": float(move[0]),
                 "vertical": float(move[1]), "sprint": bool(sprint), "move": [float(m) for m in move]}
            if jet is not None:
                d["jetpack"] = bool(jet)
            out.append(d)
        return out

    def script_start(self, name, segs):
        """Sends one de.swim script (non-blocking); returns its planned end (monotonic) or None when refused."""
        body = self.segments(segs)
        total = sum(s["seconds"] for s in body)
        q0, t0 = S.qpc(), time.monotonic()
        rep = self.swim({"script": body, "label": name}, name)
        start_tick = _pick(rep, "startTick", "tick")
        if start_tick is None and isinstance(rep, dict) and isinstance(rep.get("line"), str):
            m = re.search(r"from tick ~(\d+)", rep["line"])       # "DE.Swim script start... from tick ~N"
            start_tick = int(m.group(1)) if m else None
        self.sess.event("swim_script", label=name, segments=body, seconds=total, qpc=q0, tick=self.tick(), reply=rep,
                        startTick=start_tick, supported=self.supported(rep))
        if not self.supported(rep):
            self.log.say("  S2b script %s refused: %s" % (name, json.dumps(rep)[:300] if rep else "no reply"),
                         ev="s2b_problem")
            return None
        return t0 + total

    def script(self, name, segs, slack=1.0):
        end = self.script_start(name, segs)
        if end is not None:
            self.wait(max(0.0, end - time.monotonic()) + slack)
            self.wait_script_done(name)
        return end is not None

    def wait_script_done(self, name):
        """Polls de.swim dumps until the plugin reports no running script."""
        t0 = time.monotonic()
        k = 0
        while self.alive() and time.monotonic() - t0 < S2B_SCRIPT_WAIT_MAX_S:
            rep = self.swim({"dump": True}, "%s.done.%d" % (name, k))
            k += 1
            if rep is None or not isinstance(rep, dict) or rep.get("script") is None:
                return True
            self.wait(0.5)
        self.log.say("  S2b %s: the plugin still runs the script after %.0f s more" % (name, S2B_SCRIPT_WAIT_MAX_S),
                     ev="s2b_problem")
        return False

    def precheck(self):
        """The session's preconditions (s2b_precheck_problems) from a fresh dump; logged as a "precheck" event."""
        rep = self.dump("session.precheck")
        problems, suit = s2b_precheck_problems(rep)
        self.sess.event("precheck", ok=not problems, problems=problems, qpc=S.qpc(), tick=self.tick(),
                        **{k: suit.get(k) for k in ("helmet", "jetpackPowered", "h2Level", "o2Bottles", "h2Bottles",
                                                    "survival")})
        for p in problems:
            self.log.say("  S2b precheck: %s" % p, ev="s2b_precheck_problem")
        if suit.get("survival") is False:
            self.log.say("  S2b precheck note: not survival (no suit damage, no bottle refill)", ev="s2b_precheck_note")
        return not problems

    def suit_prep(self):
        """A known jetpack state before the parts: out of the water (the exit restores the
 player's states), a jetpack-off request, fall back in: the entry remembers jetpackWas=off, so the S2b.M3 surface
 hold bounces (a restored jetpack would hover) and every restore check has a known reference."""
        self.teleport_char(S2B_PREP_Y, "prep.arrive")
        self.wait(0.5)
        self.dump("prep.exit")
        self.script("prep", S2B_PREP_SCRIPT, slack=0.5)
        rep = self.dump("prep.after")
        pol = rep.get("policy") if isinstance(rep, dict) and isinstance(rep.get("policy"), dict) else {}
        ok = isinstance(rep, dict) and rep.get("state") == "swim" and pol.get("jetpackWas") is False
        self.sess.event("suit_prep", ok=ok, state=rep.get("state") if isinstance(rep, dict) else None,
                        jetpackWas=pol.get("jetpackWas"), dampenersWas=pol.get("dampenersWas"), qpc=S.qpc())
        if not ok:
            self.log.say("  S2b suit prep: expected in the water with jetpackWas=off, got %s" % (
                json.dumps({"state": rep.get("state") if isinstance(rep, dict) else None, "policy": pol})[:200]),
                ev="s2b_problem")
        return ok

    def timed_dumps(self, end, times, label, o2=False, t0=None):
        """Dumps at the offsets `times` (s) after t0 (default now) until `end` (monotonic), then waits for `end`."""
        t0 = time.monotonic() if t0 is None else t0
        for off in times:
            if not self.alive():
                return
            self.wait(max(0.0, t0 + off - time.monotonic()))
            if o2:
                self.o2_dump("%s.o2.%g" % (label, off))
            else:
                self.dump("%s.dump.%g" % (label, off))
        self.wait(max(0.0, end - time.monotonic()))

    def refill(self, label):
        """Up into the surface clamp from S2B_REFILL_Y until an o2=dump reads a full tank (Subnautica refills 30 units/s
 while the eye is above -1 m). Logged as a "refill" event (O2 before and after)."""
        self.teleport_char(S2B_REFILL_Y, label + ".arrive")
        self.wait(1.0)
        before = self.o2_dump(label + ".before")
        after = None
        for k in range(3):
            if not self.script("%s.up%d" % (label, k), (("%s.up" % label, abs(S2B_REFILL_Y) / S2B_ASCENT_MPS + 3.0,
                                                          (0, 1, 0), False), ("%s.rel" % label, 0.5, (0, 0, 0), False)),
                               slack=0.3):
                break
            after = self.o2_dump(label + ".after%d" % k)
            if after is None or after >= 0.95:
                break
        self.sess.event("refill", label=label, before=before, after=after, qpc=S.qpc())
        return after

    # ------------------------------------------------------------------------------------------------ parts
    def p_n1(self):
        """S2b.N1 at -50 m, a refill before each script: fwd 10 s, release 5 s, strafe 5 s, release 5 s | up 3 s, down 3 s, release 3 s, back 5 s, release 4 s, sprint fwd 5 s, release 5 s"""
        for name, segs in S2B_N1_SCRIPTS:
            if not self.alive():
                return
            self.refill(name + ".refill")       # a full tank for each script (45 s of Subnautica O2)
            self.teleport_char(S2B_N1_Y, name + ".arrive")
            self.wait(S2B_SETTLE_S)
            self.dump(name + ".before")
            self.part(name, "begin")
            self.script(name, segs)
            self.part(name, "end")
            self.dump(name + ".after")

    def p_n2(self):
        """S2b.N2 surface: from -20 m a jetpack request under water (refused), vertical up 20 s (clamp at the surface); n2j: jetpack request with the head out (granted), off, fall"""
        self.teleport_char(S2B_N2_Y, "n2.arrive")
        self.wait(S2B_SETTLE_S)
        self.dump("n2.before")
        segs = [list(x) for x in S2B_N2_SCRIPT]
        if self.q:
            segs[1][1] = 10.0
        self.part("n2", "begin")
        t0 = time.monotonic()
        end = self.script_start("n2", segs)
        if end is not None:
            self.timed_dumps(end + 1.0, [t for t in S2B_N2_DUMPS_S if t <= segs[0][1] + segs[1][1]], "n2", t0=t0)
            self.wait_script_done("n2")
        self.part("n2", "end")
        # the jetpack back with the head out (the character is at the surface clamp)
        self.part("n2j", "begin")
        t0 = time.monotonic()
        end_j = self.script_start("n2j", S2B_N2J_SCRIPT)
        if end_j is not None:
            self.timed_dumps(end_j + 1.0, [S2B_N2J_SCRIPT[0][1] + 0.5], "n2j", t0=t0)
            self.wait_script_done("n2j")
        self.part("n2j", "end")
        self.sess.event("n2", startY=S2B_N2_Y, upS=segs[1][1], supported=end is not None, jetSupported=end_j is not None)

    def p_n3(self):
        """S2b.N3 entry/exit: 4 scripted cycles across the surface (swim up 2.5 s into the clamp, jetpack on + up 1.5 s, jetpack off and fall 3 s): one exit and one entry per cycle"""
        cycles = s2b_n3_cycles(self.a)
        self.teleport_char(S2B_N3_START_Y, "n3.arrive")
        self.wait(S2B_SETTLE_S)
        self.dump("n3.before")
        segs = [("n3.c%d.%s" % (k, seg[0]),) + tuple(seg[1:]) for k in range(cycles) for seg in S2B_N3_CYCLE]
        self.part("n3", "begin")
        t0 = time.monotonic()
        end = self.script_start("n3", segs)
        if end is not None:
            # a dump near the end of every phase of every cycle (state, eye, jetpack as SE reports them)
            per = sum(x[1] for x in S2B_N3_CYCLE)
            marks = []
            for k in range(cycles):
                acc = k * per
                for seg in S2B_N3_CYCLE:
                    acc += seg[1]
                    marks.append(round(acc - 0.25, 3))
            self.timed_dumps(end + 1.0, marks, "n3", t0=t0)
            self.wait_script_done("n3")
        self.part("n3", "end")
        self.dump("n3.after")
        self.sess.event("n3", cycles=cycles, cycle=[list(x[:2]) for x in S2B_N3_CYCLE], startY=S2B_N3_START_Y,
                        method="script jetpack cycles", supported=end is not None)

    def p_n4(self):
        """S2b.N4 oxygen: refill, 60 s at -30 m with o2=dump every 5 s (early surface below --n4-min-o2), then up and 20 s at the surface (dumps every 5 s)"""
        start_o2 = self.refill("n4.refill")
        self.teleport_char(S2B_N4_Y, "n4.arrive")
        self.wait(1.0)
        dive = S2B_N4_DIVE_S if not self.q else 30.0
        self.part("n4.dive", "begin")
        t0 = time.monotonic()
        early, k, last, prev = False, 0, None, None
        while self.alive():
            off = k * S2B_N4_DUMP_S
            if off > dive + 1e-6:
                break
            self.wait(max(0.0, t0 + off - time.monotonic()))
            prev, last = last, self.o2_dump("n4.dive.o2.%g" % off)
            k += 1
            # the character must not suffocate: stop when this dump, or the next one by the last slope, is too low
            nxt = last - (prev - last) if (last is not None and prev is not None) else last
            if last is not None and self.a.n4_min_o2 > 0 and min(last, nxt) <= self.a.n4_min_o2:
                early = True
                self.log.say("  S2b n4: O2 %.3f (next %.3f) <= %.3f after %.0f s at %g m: surfacing early" % (
                    last, nxt, self.a.n4_min_o2, off, S2B_N4_Y), ev="s2b_n4_early", o2=last)
                break
        self.part("n4.dive", "end")
        up = abs(S2B_N4_Y) / S2B_ASCENT_MPS + (S2B_N4_SURFACE_S if not self.q else 10.0)
        self.part("n4.surface", "begin")
        t1 = time.monotonic()
        end = self.script_start("n4.surface", (("n4.up", up, (0, 1, 0), False), ("n4.rel", 1.0, (0, 0, 0), False)))
        if end is not None:
            self.timed_dumps(end + 0.5, [S2B_N4_DUMP_S * j for j in range(1, int(up // S2B_N4_DUMP_S) + 1)],
                             "n4.surface", o2=True, t0=t1)
            self.wait_script_done("n4.surface")
        self.o2_dump("n4.surface.o2.end")
        self.part("n4.surface", "end")
        self.sess.event("n4", depthY=S2B_N4_Y, diveS=dive, dumpEveryS=S2B_N4_DUMP_S, early=early, lastDiveO2=last,
                        startO2=start_o2, minO2=self.a.n4_min_o2, surfaceUpS=up)

def s2b_plan_text(a) -> list:
    """The parts and their steps, for --dry-run."""
    out = ["  SE environment %s (the model starts off)" % json.dumps(S2B_SE_ENV),
           "  start  de.swim dump (session.check); de.swim {model: on, rows: on, teleportChar: [x, %g, z]}; precheck "
           "(helmet closed, jetpack powered, hydrogen >= %.0f %%, no O2 bottle; refused otherwise); refill" % (
               S2B_REFILL_Y, 100.0 * S2B_MIN_H2),
           "  prep   teleportChar feet %g m (out of the water), script prep: %s; dump: in the water, jetpackWas=off" % (
               S2B_PREP_Y, json.dumps([{k: v for k, v in d.items() if k in S2B_SEGMENT_KEYS}
                                       for d in S2bRunner.segments(S2B_PREP_SCRIPT)]))]
    for p in a.parts_list:
        doc = (getattr(S2bRunner, "p_" + p).__doc__ or "").strip().split("\n")[0]
        out.append("  part %-3s %s" % (p, doc))
        scripts = {"n1": list(S2B_N1_SCRIPTS), "n2": [("n2", S2B_N2_SCRIPT), ("n2j", S2B_N2J_SCRIPT)],
                   "n3": [("n3 (one cycle of %d)" % s2b_n3_cycles(a), S2B_N3_CYCLE)]}.get(p, [])
        for name, segs in scripts:
            body = [{k: v for k, v in d.items() if k in S2B_SEGMENT_KEYS} for d in S2bRunner.segments(segs)]
            out.append("           script %s: %s" % (name, json.dumps(body)))
    return out

def s2b_analyze(log: Log, a, out: str) -> int:
    AS = s2b_analyzer()
    try:
        rows = AS.evaluate(out)
    except Exception as e:     # an analyser bug must not hide the session
        import traceback
        rows = []
        AS.C.row(rows, "S2b", "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120] + " | " +
                 traceback.format_exc().strip().splitlines()[-1][:120], "all")
    strict = not getattr(a, "lenient", False)
    ok = AS.C.verdict(rows, strict)
    text = AS.C.table(rows)
    res = {"tool": "launcher s2b", "out": out, "ok": ok, "rows": rows, "model": AS.model_status()}
    with open(os.path.join(out, "s2b-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(out, "s2b-table.txt"), "w", encoding="utf-8") as f:
        f.write(text + "\n")
    print("\nS2B ORACLE TABLE (%s)  (* = not required; INFO/OTHER = information or the owner's)" % out)
    print(text)
    log.say("S2B ORACLE %s: %d rows, %d FAIL, %d MISSING -> %s" % (
        "PASS" if ok else "FAIL", len(rows), sum(1 for r in rows if r["status"] == "FAIL" and r.get("required", True)),
        sum(1 for r in rows if r["status"] == "MISSING" and r.get("required", True)),
        os.path.join(out, "s2b-results.json")), ev="s2b_result", ok=ok)
    return 0 if ok else 1

def s2b(log: Log, a) -> int:
    if a.analyze:
        return s2b_analyze(log, a, a.analyze)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(REPO, "tests", "out", "s2b", stamp)
    parts = [x.strip() for x in a.parts.split(",") if x.strip()] if a.parts else list(S2B_PARTS)
    unknown = [x for x in parts if x not in S2B_PARTS]
    if unknown:
        log.say("unknown parts %s (known: %s)" % (unknown, ",".join(S2B_PARTS)))
        return 2
    a.parts_list = parts
    a.cont = not a.no_continue
    a.sn_cmd = None
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    se_env = dict(S2B_SE_ENV)
    if mutants:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(mutants)
    _sn_cmd, se_cmd = session_commands(a)
    log.say("S2b session %s: parts %s%s%s; SE alone: %s; out %s" % (
        stamp, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants else "",
        subprocess.list2cmdline(se_cmd), out), ev="s2b_plan")
    if a.dry_run:
        for line in s2b_plan_text(a):
            print(line)
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    sess = S2SessionLog(os.path.join(out, "s2b-session.jsonl"))
    standin_se = bool(a.se_cmd)
    shell_mutants = os.environ.get("DEEPENGINEERS_MUTANTS", "")
    sess.event("start", stamp=stamp, parts=parts, name=a.name, argv=sys.argv[1:], quick=a.quick, seAlone=True,
               mutants=mutants or [m for m in shell_mutants.split(",") if m.strip()], standin={"se": standin_se},
               seCmd=se_cmd, qpcFrequency=S.QPC_FREQUENCY,
               plan={"spot": S2B_SPOT, "n1Y": S2B_N1_Y, "n1Scripts": {n: S2bRunner.segments(s) for n, s in
                                                                     S2B_N1_SCRIPTS},
                     "n2Y": S2B_N2_Y, "n3StartY": S2B_N3_START_Y, "n3Cycles": s2b_n3_cycles(a),
                     "n4Y": S2B_N4_Y, "n4DiveS": S2B_N4_DIVE_S, "n4MinO2": a.n4_min_o2})
    hashed = [] if standin_se else [SE_CFG, WATER_CLIENT_SETTINGS]
    for path in hashed:
        sess.event("file_hash", path=path, when="before", qpc=S.qpc(),
                   sha256=sha256_file(path) if os.path.exists(path) else None)
    t_start = time.time()
    rc, host, se, runner = None, None, None, None
    try:
        rc, host, se = start_se_alone(log, a, out, se_env)
        in_world = rc is None and wait_se_in_world(log, a, se, a.world_timeout)
        if in_world:
            runner = S2bRunner(log, a, sess, host, se, out)
            ok, why = runner.attach()
            if not ok:
                log.say("S2b: harness not usable: %s" % why, ev="s2b_harness_refused")
                rc = 1
            else:
                st = runner.dump("session.check")
                if not runner.supported(st):
                    log.say("S2b: SE does not take de.swim (%s): an SE build without S2b? Parts not run." % (
                        json.dumps(st)[:200] if st else "no reply"), ev="s2b_no_swim")
                    rc = 1
                else:
                    # SE started with the model off (standby): the DE Lab character waits on SE's floor at -3,000 m,
 # Subnautica's crush class (22.5 s of air). The model goes on with the first teleport to the refill
 # spot, together with a row every tick for the oracle
                    st = runner.swim({"model": "on", "rows": "on", "teleportChar": [runner.x, S2B_REFILL_Y, runner.z],
                                      "yawDeg": 0.0}, "session.start")
                    if not runner.supported(st):
                        log.say("S2b: de.swim model/rows/teleport not acknowledged: %s" % (
                            json.dumps(st)[:200] if st else "no reply"), ev="s2b_problem")
                    runner.wait(1.0)
                    ok_pre = runner.precheck()
                    if not ok_pre and not standin_se:
                        log.say("S2b: session preconditions not met (see the precheck lines): parts not run. Fix DE Lab "
                                "(helmet closed, hydrogen, no O2 bottle), save, and run again.", ev="s2b_refused")
                        rc = 1
                    else:
                        runner.refill("session.refill")
                        runner.suit_prep()
                        for p in parts:
                            if not runner.alive():
                                log.say("S2b: SE exited before part %s" % p, ev="s2b_exit_during_script")
                                break
                            try:
                                getattr(runner, "p_" + p)()
                            except Exception as e:
                                log.say("S2b part %s failed: %r" % (p, e), ev="s2b_part_error", part=p)
                                sess.event("part_error", name=p, error=repr(e))
                log.say("S2b script finished: %s" % json.dumps(runner.stats), ev="s2b_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if not a.keep_running:
            close_session(log, a, None, se, standin_se, True)
        if host is not None and host.poll() is None:
            S.terminate_pid(host.pid, 0)     # the passive stand-in host (our own Python process, never a game)
        for path in hashed:
            sess.event("file_hash", path=path, when="after", qpc=S.qpc(),
                       sha256=sha256_file(path) if os.path.exists(path) else None)
        sess.event("end", rc=rc, stats=runner.stats if runner else None)
        sess.close()
        collect(log, out, t_start, host, True, standin_se, stamp)
    if rc is not None:
        log.say("S2b session aborted (rc %d); analysing what was collected" % rc)
    res = s2b_analyze(log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# s5a: one scripted S5a "Arms and breath" session in SE alone (tests/s5a/analyze_s5a.py; CONTRACT §2.10 fins, §5.4 "C1
# swim extension"). Modelled on s2b: the passive host, Legacy.exe -continue, the C11 de.swim command. The SE side only
# sends its swim state through C1; the Subnautica side (arms, OxygenManager mirror, breath/choke sounds) is judged when
# the orchestrator runs Subnautica with the s2 session recipe alongside and passes its log to `s5a --analyze DIR
# --sn-log FILE` (there is no --both: launching both games is the s2 recipe's job, not this session's). Parts:
# fins `de.swim fins=ultra` then `fins=off`: the two S2b.N1 scripts (n1a, n1b) at -50 m with a refill before each;
# the replay oracle (rows carry the fin modifier) and the steady speeds 8.1 / 5.70 m/s judge them; fins back to
# ultra at the end
# o2 at -20 m: `de.swim o2=1.0`, `0.2`, `0.05`, `auto`, each held S5A_O2_HOLD_S while the C1 swim extension is
# sampled from the section (~20 Hz, "c1swim" events) and a dump records SE's own suit level
# arms at -20 m: forward 3 s, strafe 3 s, up 3 s, release 3 s (sampled), then a teleport out of the water (feet
# +2 m) sampled 3 s: the flags, axes and speed fractions the SN arms need
# choke at -20 m: `o2=0.05` held S5A_CHOKE_HOLD_S (sampled) then `auto`: the SN low-O2 alert / choke must fire
# --mutants S5a.M1,S5a.M2,S5a.M3 (DEEPENGINEERS_MUTANTS in SE's environment only) each make a row red.
# ----------------------------------------------------------------------------------------------------------------

S5A_DIR = os.path.join(REPO, "tests", "s5a")
S5A_PARTS = ("fins", "o2", "arms", "choke")
S5A_FINS_MODES = ("ultra", "off")
S5A_SWIM_Y = -20.0               # character origin (feet) for o2, arms and choke: the eye about 1.7 m higher
S5A_OUT_Y = 12.0                 # arms.out: feet here; the fall back into the water takes about 1.5 s, so the analyzer's
 # 1 s tail is out of the water (eye above +5 m: the controller idles = flags 0, mode Walk).
 # Real run S5a-1: from +2 m the engineer was back in the water in < 1 s
S5A_ARMS_Y = -45.0               # arms: start depth; 3 s of "up" climbs about 20 m, so from -20 m the script reached the
 # surface (S5a-1: mode Surface during up/release, the InWater+Underwater rows at 67 %)
S5A_O2_STEPS = (1.0, 0.2, 0.05, "auto")
S5A_O2_HOLD_S = 4.0
S5A_ARMS_SCRIPT = (("arms.fwd", 3.0, (0, 0, 1), False), ("arms.strafe", 3.0, (1, 0, 0), False),
                   ("arms.up", 3.0, (0, 1, 0), False), ("arms.rel", 3.0, (0, 0, 0), False))
S5A_OUT_HOLD_S = 3.0
S5A_CHOKE_HOLD_S = 8.0
S5A_SAMPLE_S = 0.05              # C1 swim extension sampling period while a part samples (~20 Hz)
S5A_SE_ENV = dict(S2B_SE_ENV)    # the model starts off (standby); the first teleport switches it on
C1_SWIM_OFFSET = 0x94            # CONTRACT §5.4 "C1 swim extension": 12 bytes inside the C1 pad (layout 1.0 unchanged)
C1_SWIM_FORMAT = "<BBBBBBbbbBBb"
C1_SWIM_KEYS = ("version", "flags", "mode", "fins", "speedH", "speedV", "moveF", "moveS", "moveV", "o2", "dc", "vy")

def s5a_analyzer():
    for p in (S5A_DIR, S2B_DIR, S2_DIR):
        if p not in sys.path:
            sys.path.insert(0, p)
    import analyze_s5a  # noqa: E402
    return analyze_s5a

def decode_c1_swim(record: bytes) -> dict:
    v = struct.unpack_from(C1_SWIM_FORMAT, record, C1_SWIM_OFFSET)
    return dict(zip(C1_SWIM_KEYS, v))

class S5aRunner(S2bRunner):
    """The S5a parts over the s2b plumbing (teleport, scripts, refill, dumps) plus the C1 swim extension sampler:
 while `sampling` is on, every wait loop reads the C1 record (seqlock, read-only) and logs a "c1swim" event each
 time the tick advanced and S5A_SAMPLE_S passed."""

    def __init__(self, log, a, sess, host, se, out):
        super().__init__(log, a, sess, host, se, out)
        self.sampling = False
        self._last_sample = 0.0
        self._last_tick = None
        self.samples = 0

    def wait(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end and self.alive():
            self.h.poll_replies()
            if self.sampling:
                self.sample()
            time.sleep(min(0.02, max(0.0, end - time.monotonic())))

    def sample(self):
        now = time.monotonic()
        if now - self._last_sample < S5A_SAMPLE_S:
            return
        self._last_sample = now
        try:
            st, seq, d1 = S.seqlock_read(self.sec, L.REGION["C1"].offset, L.SE_FRAME.size)
        except Exception:     # a read problem never stops the session
            return
        if not d1:
            return
        tick = L.SE_FRAME.unpack(d1)["tick"]
        if tick == self._last_tick:
            return
        self._last_tick = tick
        rec = decode_c1_swim(d1)
        self.sess.event("c1swim", tick=tick, seq=seq, qpc=S.qpc(), **rec)
        self.samples += 1

    def set_fins(self, mode, label):
        rep = self.swim({"fins": mode}, label)
        self.sess.event("fins", label=label, value=mode, reply=rep, qpc=S.qpc(), tick=self.tick())
        if not self.supported(rep):
            self.log.say("  S5a %s: de.swim fins=%s not acknowledged: %s" % (label, mode, json.dumps(rep)[:200] if rep
                                                                             else "no reply"), ev="s5a_problem")
        return rep

    def set_o2(self, value, label):
        rep = self.swim({"o2": value}, label)
        self.sess.event("o2_step", label=label, value=value, reply=rep, qpc=S.qpc(), tick=self.tick())
        if not self.supported(rep):
            self.log.say("  S5a %s: de.swim o2=%s not acknowledged: %s" % (label, value, json.dumps(rep)[:200] if rep
                                                                           else "no reply"), ev="s5a_problem")
        return rep

    def settle_in_water(self, label):
        self.teleport_char(S5A_SWIM_Y, label + ".arrive")
        self.wait(S2B_SETTLE_S)

    # ------------------------------------------------------------------------------------------------ parts
    def p_fins(self):
        """S5a fins: de.swim fins=ultra then fins=off, the two S2b.N1 scripts at -50 m each (a refill before each); fins back to ultra"""
        for mode in S5A_FINS_MODES:
            if not self.alive():
                return
            self.set_fins(mode, "fins.%s" % mode)
            for name, segs in S2B_N1_SCRIPTS:
                if not self.alive():
                    return
                pname = "fins.%s.%s" % (mode, name)
                self.refill(pname + ".refill")
                self.teleport_char(S2B_N1_Y, pname + ".arrive")
                self.wait(S2B_SETTLE_S)
                self.dump(pname + ".before")
                self.sampling = True
                self.part(pname, "begin")
                self.script(pname, segs)
                self.part(pname, "end")
                self.sampling = False
                self.dump(pname + ".after")
        self.set_fins("ultra", "fins.restore")

    def p_o2(self):
        """S5a o2: at -20 m de.swim o2=1.0, 0.2, 0.05, auto, each held 4 s while the C1 swim extension is sampled; a dump per step"""
        self.settle_in_water("o2")
        hold = S5A_O2_HOLD_S if not self.q else 2.0
        for v in S5A_O2_STEPS:
            if not self.alive():
                return
            lab = "o2.%s" % (v if isinstance(v, str) else "%g" % v)
            self.set_o2(v, lab)
            self.sampling = True
            self.part(lab, "begin")
            self.wait(hold)
            self.dump(lab + ".dump")
            self.part(lab, "end")
            self.sampling = False

    def p_arms(self):
        """S5a arms: at -45 m forward 3 s, strafe 3 s, up 3 s, release 3 s (C1 sampled), then a teleport out of the water (feet +12 m) sampled 3 s"""
        self.settle_in_water("arms")
        self.teleport_char(S5A_ARMS_Y, "arms.deep")      # deeper than the o2/choke depth: the up segment must stay under
        self.wait(S2B_SETTLE_S)
        self.dump("arms.before")
        self.sampling = True
        self.part("arms", "begin")
        self.script("arms", S5A_ARMS_SCRIPT)
        self.part("arms", "end")
        self.part("arms.out", "begin")
        self.teleport_char(S5A_OUT_Y, "arms.out.arrive")
        self.wait(S5A_OUT_HOLD_S)
        self.part("arms.out", "end")
        self.sampling = False
        self.dump("arms.out.after")

    def p_choke(self):
        """S5a choke: at -20 m de.swim o2=0.05 held 8 s (C1 sampled; the SN low-O2 alert / choke must fire), then o2=auto"""
        self.settle_in_water("choke")
        self.sampling = True
        self.part("choke", "begin")
        self.set_o2(0.05, "choke")
        self.wait(S5A_CHOKE_HOLD_S if not self.q else 5.0)
        self.dump("choke.dump")
        self.set_o2("auto", "choke.auto")
        self.part("choke", "end")
        self.sampling = False

def s5a_plan_text(a) -> list:
    out = ["  SE environment %s (the model starts off; the first teleport switches it on)" % json.dumps(S5A_SE_ENV),
           "  start  de.swim dump (session.check); de.swim {model: on, rows: on, teleportChar: [x, %g, z]}; precheck; refill; "
           "suit prep (as s2b)" % S2B_REFILL_Y,
           "  C1     the swim extension (bytes 0x%02x-0x%02x of the C1 record: %s) sampled every %.0f ms while a part "
           "samples -> c1swim events" % (C1_SWIM_OFFSET, C1_SWIM_OFFSET + 11, ",".join(C1_SWIM_KEYS), 1000 * S5A_SAMPLE_S)]
    for p in a.parts_list:
        doc = (getattr(S5aRunner, "p_" + p).__doc__ or "").strip().split("\n")[0]
        out.append("  part %-5s %s" % (p, doc))
        if p == "fins":
            for name, segs in S2B_N1_SCRIPTS:
                body = [{k: v for k, v in d.items() if k in S2B_SEGMENT_KEYS} for d in S2bRunner.segments(segs)]
                out.append("             script %s (x2: fins ultra, fins off): %s" % (name, json.dumps(body)))
        if p == "arms":
            body = [{k: v for k, v in d.items() if k in S2B_SEGMENT_KEYS} for d in S2bRunner.segments(S5A_ARMS_SCRIPT)]
            out.append("             script arms: %s; then teleportChar feet %g m, %g s" % (json.dumps(body), S5A_OUT_Y, S5A_OUT_HOLD_S))
        if p == "o2":
            out.append("             steps %s, %g s each" % (json.dumps(list(S5A_O2_STEPS)), S5A_O2_HOLD_S if not a.quick else 2.0))
    out.append("  SN side: not launched here; run Subnautica with the s2 recipe alongside and judge its log with "
               "`s5a --analyze DIR --sn-log FILE` (the SN log contract: tests/s5a/analyze_s5a.py docstring)")
    if a.mutants:
        out.append("  mutants %s (DEEPENGINEERS_MUTANTS, SE only)" % a.mutants)
    return out

def s5a_analyze(log: Log, a, out: str) -> int:
    AS = s5a_analyzer()
    sn_logs = list(a.sn_log or [])
    try:
        rows = AS.evaluate(out, sn_logs)
    except Exception as e:     # an analyser bug must not hide the session
        import traceback
        rows = []
        AS.C.row(rows, "S5a", "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120] + " | " +
                 traceback.format_exc().strip().splitlines()[-1][:120], "all")
    strict = not getattr(a, "lenient", False)
    ok = AS.C.verdict(rows, strict)
    text = AS.C.table(rows)
    res = {"tool": "launcher s5a", "out": out, "ok": ok, "rows": rows, "snLogs": sn_logs}
    with open(os.path.join(out, "s5a-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(out, "s5a-table.txt"), "w", encoding="utf-8") as f:
        f.write(text + "\n")
    print("\nS5A ORACLE TABLE (%s)  (* = not required; INFO/OTHER = information or the owner's)" % out)
    print(text)
    log.say("S5A ORACLE %s: %d rows, %d FAIL, %d MISSING -> %s" % (
        "PASS" if ok else "FAIL", len(rows), sum(1 for r in rows if r["status"] == "FAIL" and r.get("required", True)),
        sum(1 for r in rows if r["status"] == "MISSING" and r.get("required", True)),
        os.path.join(out, "s5a-results.json")), ev="s5a_result", ok=ok)
    return 0 if ok else 1

def s5a(log: Log, a) -> int:
    if a.analyze:
        return s5a_analyze(log, a, a.analyze)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(REPO, "tests", "out", "s5a", stamp)
    parts = [x.strip() for x in a.parts.split(",") if x.strip()] if a.parts else list(S5A_PARTS)
    unknown = [x for x in parts if x not in S5A_PARTS]
    if unknown:
        log.say("unknown parts %s (known: %s)" % (unknown, ",".join(S5A_PARTS)))
        return 2
    a.parts_list = parts
    a.cont = not a.no_continue
    a.sn_cmd = None
    a.n3_cycles = 0
    a.n4_min_o2 = 0.08
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    se_env = dict(S5A_SE_ENV)
    if mutants:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(mutants)
    _sn_cmd, se_cmd = session_commands(a)
    log.say("S5a session %s: parts %s%s%s; SE alone: %s; out %s" % (
        stamp, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants else "",
        subprocess.list2cmdline(se_cmd), out), ev="s5a_plan")
    if a.dry_run:
        for line in s5a_plan_text(a):
            print(line)
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    sess = S2SessionLog(os.path.join(out, "s5a-session.jsonl"))
    standin_se = bool(a.se_cmd)
    sess.event("start", stamp=stamp, parts=parts, name=a.name, argv=sys.argv[1:], quick=a.quick, seAlone=True,
               mutants=mutants, standin={"se": standin_se}, seCmd=se_cmd, qpcFrequency=S.QPC_FREQUENCY,
               snLog=bool(a.sn_log), plan={"swimY": S5A_SWIM_Y, "outY": S5A_OUT_Y, "o2Steps": list(S5A_O2_STEPS),
                                            "finsModes": list(S5A_FINS_MODES), "n1Y": S2B_N1_Y,
                                            "armsScript": S2bRunner.segments(S5A_ARMS_SCRIPT)})
    hashed = [] if standin_se else [SE_CFG, WATER_CLIENT_SETTINGS]
    for path in hashed:
        sess.event("file_hash", path=path, when="before", qpc=S.qpc(),
                   sha256=sha256_file(path) if os.path.exists(path) else None)
    t_start = time.time()
    rc, host, se, runner = None, None, None, None
    try:
        rc, host, se = start_se_alone(log, a, out, se_env)
        in_world = rc is None and wait_se_in_world(log, a, se, a.world_timeout)
        if in_world:
            runner = S5aRunner(log, a, sess, host, se, out)
            ok, why = runner.attach()
            if not ok:
                log.say("S5a: harness not usable: %s" % why, ev="s5a_harness_refused")
                rc = 1
            else:
                st = runner.dump("session.check")
                if not runner.supported(st) or not isinstance(st, dict) or "fins" not in st:
                    log.say("S5a: SE does not take de.swim with the S5a fields (%s): an SE build before S5a? Parts not run." % (
                        json.dumps(st)[:200] if st else "no reply"), ev="s5a_no_swim")
                    rc = 1
                else:
                    st = runner.swim({"model": "on", "rows": "on", "teleportChar": [runner.x, S2B_REFILL_Y, runner.z],
                                      "yawDeg": 0.0}, "session.start")
                    if not runner.supported(st):
                        log.say("S5a: de.swim model/rows/teleport not acknowledged: %s" % (
                            json.dumps(st)[:200] if st else "no reply"), ev="s5a_problem")
                    runner.wait(1.0)
                    ok_pre = runner.precheck()
                    if not ok_pre and not standin_se:
                        log.say("S5a: session preconditions not met (see the precheck lines): parts not run.", ev="s5a_refused")
                        rc = 1
                    else:
                        runner.refill("session.refill")
                        runner.suit_prep()
                        for p in parts:
                            if not runner.alive():
                                log.say("S5a: SE exited before part %s" % p, ev="s5a_exit_during_script")
                                break
                            try:
                                getattr(runner, "p_" + p)()
                            except Exception as e:
                                log.say("S5a part %s failed: %r" % (p, e), ev="s5a_part_error", part=p)
                                sess.event("part_error", name=p, error=repr(e))
                            runner.sampling = False
                        try:
                            runner.set_o2("auto", "session.o2auto")   # never leave a test override behind
                        except Exception:
                            pass
                log.say("S5a script finished: %s; %d C1 samples" % (json.dumps(runner.stats), runner.samples),
                        ev="s5a_script_done", stats=runner.stats, samples=runner.samples)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if not a.keep_running:
            close_session(log, a, None, se, standin_se, True)
        if host is not None and host.poll() is None:
            S.terminate_pid(host.pid, 0)     # the passive stand-in host (our own Python process, never a game)
        for path in hashed:
            sess.event("file_hash", path=path, when="after", qpc=S.qpc(),
                       sha256=sha256_file(path) if os.path.exists(path) else None)
        sess.event("end", rc=rc, stats=runner.stats if runner else None)
        sess.close()
        collect(log, out, t_start, host, True, standin_se, stamp)
    if rc is not None:
        log.say("S5a session aborted (rc %d); analysing what was collected" % rc)
    res = s5a_analyze(log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# s3: one scripted S3 "First block" session (ROADMAP S3 oracle: C1-C14, G1-E2, N1-N3)
# ----------------------------------------------------------------------------------------------------------------

S3_DIR = os.path.join(REPO, "tests", "s3")
S3_PARTS = ("place", "geometry", "views", "sun", "n2", "n3", "n1", "plate", "shallow", "teardown")
S3_DEFAULT_PARTS = {"S3a": ("place", "geometry", "views", "sun", "n2", "n3", "n1", "shallow", "teardown"),
                    "S3b": ("place", "geometry", "views", "sun", "n2", "n3", "plate", "shallow", "teardown")}
S3_DEEP_SPOT = (-1029.0, -1116.0)        # grandReef lab spot of the S1/S2 sessions (SN x, z); seabed about -150..-160 m
S3_SHALLOW_SPOT = (0.0, 0.0)             # the site anchor, Safe Shallows, seabed about -18 m (probe-world-mapping 3)
S3_SEABED_FALLBACK = {"deep": -150.0, "shallow": -18.0, "n1": -150.0, "plate": -150.0}
S3_PROBE_HOLD_S = 6.0                    # per camera hold: the +-16 m collision box builds in < 1 s (S1.N3)
S3_PROBE_STEP_M = 28.0                   # the next hold this much lower (the box is +-16 m: holds overlap)
S3_PROBE_HOLDS = 14                      # down to the expected seabed - 364 m (grandReef floors reach -480 m)
S3_HEADING_DEG = 30.0                    # grid Forward heading: a non-trivial yaw, so the mirror shows on every corner
S3_SEAMOTH_OFFSET = (6.0, 0.0, 0.0)      # the reference Seamoth 6 m east of the block (ROADMAP "6 m aside")
S3_SETTLE_FRAMES = 90                    # ROADMAP: captures after 90 frames at each pose
S3_SETTLE_MIN_S = 1.5
S3_N2 = {"amplitudeDeg": 30.0, "hz": 0.5, "seconds": 10.0, "restS": 2.0}
S3_SN_MUTANTS = ("S3.M1", "S3.M2", "S3.M3", "S3.M5", "S3.M6", "S3.M7", "S3.M8")
# Seeded-failure groups for the runbook (one launch each; mutants in one group never touch the same rows). S3.M4 is the
# producer's: the fake producer's --s3-mutant M4 in S3a, SE's DEEPENGINEERS_MUTANTS in S3b.
S3_MUTANT_GROUPS = {
    "mirror": {"mutants": ("S3.M1",), "parts": ("place", "geometry", "views"), "red": ("S3.C2", "S3.C3")},
    "winding-shear": {"mutants": ("S3.M2", "S3.M7"), "parts": ("place", "geometry"), "red": ("S3.C3", "S3.G1-E2")},
    "sky": {"mutants": ("S3.M3",), "parts": ("place", "geometry", "views", "sun"),
            "red": ("S3.C4",), "watch": ("S3.C6", "S3.C7")},
    "lag": {"mutants": ("S3.M4",), "parts": ("place", "n2"), "red": ("S3.N2m",)},
    "capture-teardown": {"mutants": ("S3.M5", "S3.M6"), "parts": ("place", "shallow", "teardown"),
                         "red": ("S3.C10", "S3.C12")},
    # S3.M8 (an sRGB conversion target for _ng) is measured by the conversion fidelity check S3.N1d: the sRGB encode is
 # monotonic, so the bump ordering N1b judges survives it
    "bump": {"mutants": ("S3.M8",), "parts": ("place", "n1"), "red": ("S3.N1d",), "watch": ("S3.N1b",)},
}
# de.time day fraction of the shallow captures: Subnautica's sun (decomp DayNightCycle.GetDayNightCycleTime with sunrise
# 0.125 and sunset 0.875, uSkyManager.InitSunAndMoon with sunMaxAngle 65) stands at s3_sun_elevation_deg(t); 0.24 gives
# about 45 degrees, so the 1.25 m exposed block casts a 1.25 m shadow
S3_C9_TIME = 0.24

def s3_sun_elevation_deg(t: float) -> float:
    """Elevation of Subnautica's sun light at day fraction t (de.time t): the cycle time (t - 0.125) / 0.75 * 0.5 + 0.25
 during the day, uSkyManager's tilt lerp(-65, 65, clamp(2 * cycle - 0.5)) from the vertical, so 90 - |tilt|; the
 light's vertical tilt mirrors at night (the same formula, below the horizon: negative)."""
    rise, sett, max_angle = 0.125, 0.875, 65.0
    if rise < t < sett:
        cyc = (t - rise) / (sett - rise) * 0.5 + 0.25
        k = min(1.0, max(0.0, cyc * 2.0 - 0.5))
        tilt = -max_angle + 2.0 * max_angle * k
        return 90.0 - abs(tilt)
    return -1.0
PLAYER_LOG = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow", "Unknown Worlds", "Subnautica", "Player.log")

def s3_analyzer():
    if S3_DIR not in sys.path:
        sys.path.insert(0, S3_DIR)
    import analyze_s3   # noqa: E402
    return analyze_s3

def process_cpu_seconds(pid: int):
    """Kernel + user CPU seconds of a process (GetProcessTimes; read-only, PROCESS_QUERY_LIMITED_INFORMATION)."""
    try:
        import ctypes
        from ctypes import wintypes
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        k32.CloseHandle.argtypes = [wintypes.HANDLE]
        h = k32.OpenProcess(0x1000, False, int(pid))
        if not h:
            return None
        try:
            ft = [wintypes.FILETIME() for _ in range(4)]
            if not k32.GetProcessTimes(h, *[ctypes.byref(f) for f in ft]):
                return None
            sec = [((f.dwHighDateTime << 32) | f.dwLowDateTime) / 1e7 for f in ft]
            return sec[2] + sec[3]
        finally:
            k32.CloseHandle(h)
    except (OSError, AttributeError):
        return None

def _q_rot(q, v):
    ux, uy, uz, w = q
    tx, ty, tz = 2 * (uy * v[2] - uz * v[1]), 2 * (uz * v[0] - ux * v[2]), 2 * (ux * v[1] - uy * v[0])
    return (v[0] + w * tx + uy * tz - uz * ty, v[1] + w * ty + uz * tx - ux * tz, v[2] + w * tz + ux * ty - uy * tx)

def s3_apply(rep, l_se):
    """CONTRACT 2.3 Apply(o, q, s, l) = o + S (q Mirror(l)) from a de.s3 place reply (for the N1 camera and N3 points)."""
    o, q, s = rep["o"], rep["q"], rep["s"]
    v = _q_rot(q, (l_se[0], l_se[1], -l_se[2]))
    return (o[0] + s * v[0], o[1] + v[1], o[2] + s * v[2])

class S3Runner:
    """The scripted S3 parts through the C11 harness. Subnautica's side is the plugin's lab interface (SN_ACTIONS: de.s3
 {action: probe | status | shear | frametime | visible | placement | count | teardown}, de.s3 {lab: [x, z], captures,...} and de.time {t}); captures come from Subnautica's lab runs (its plan poses, its S3 sidecars with the block, the
 Seamoth, the buried-corner rays and the sun) and, where a part needs the SE camera's pose or Subnautica has no lab run
 (a stand-in), from the harness flow: SE camera at the pose (de.teleport/de.look), 90 frames, Subnautica's de.capture
 (else SE's C10 relay). Never touches a game window."""

    # logical action ->: the plugin's syntax, one table
    SN_ACTIONS = {
        "status": ("de.s3", lambda: {"action": "status"}),
        # warp: the seabed colliders exist only near Subnautica's camera; in S3a the fake producer's camera is not at the
 # probe point yet
 # y (expected seabed): the plugin holds the warped camera at y + 5; the collision clipmap builds only +-16 m
 # around the camera (S1.N3), so a camera 20-30 m above the seabed never sees a collider; fromY of the lab = the same rule
        "probe": ("de.s3", lambda x, z, y=None: dict({"action": "probe", "x": float(x), "z": float(z), "warp": True},
                                                      **({"y": float(y)} if y is not None else {}))),
        "lab": ("de.s3", lambda x, z, captures, session, heading, camera, from_y=None: dict({
            "lab": [float(x), float(z)], "seamoth": True, "captures": list(captures), "frames": S3_SETTLE_FRAMES,
            "headingDeg": float(heading), "camera": camera, "session": session, "requireBlock": True},
            **({"fromY": float(from_y)} if from_y is not None else {}))),
        "shear": ("de.s3", lambda s: {"action": "shear", "s": float(s), "frames": 360}),
        "frametime": ("de.s3", lambda n: {"action": "frametime", "frames": int(n)}),
        "visible": ("de.s3", lambda on: {"action": "visible", "on": bool(on)}),
        "placement": ("de.s3", lambda: {"action": "placement"}),
        "count": ("de.s3", lambda: {"action": "count"}),
        "teardown": ("de.s3", lambda: {"action": "teardown"}),
        "time": ("de.time", lambda f: {"t": float(f)}),
        "trace": ("de.trace", lambda on: {"on": bool(on)}),
    }

    def __init__(self, log, a, sess, sn, se, out, stamp, standin_sn, standin_se):
        self.log, self.a, self.sess, self.sn, self.se, self.out = log, a, sess, sn, se, out
        self.stamp, self.standin_sn, self.standin_se = stamp, standin_sn, standin_se
        self.q = a.quick
        self.sec = S.Section.open(a.name)
        self.h = S.Harness(self.sec) if self.sec else None
        self.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
        self.cap_n = 0
        self.places = {}
        self.labs = {}
        self.seabeds = {}
        self.unsupported = set()
        self.lab_sessions = []

    # ------------------------------------------------------------------------------------------------ plumbing
    def alive(self):
        return self.sn.poll() is None and self.se.poll() is None

    def attach(self):
        if self.h is None:
            return False, "section absent"
        ok, why = self.h.attach()
        if not ok:
            return False, why
        for side in ("se", "sn"):
            rep = self.call(side, "de.ping", {})
            if rep is None:
                return False, "%s does not answer C11 (dev build needed)" % side.upper()
        return True, ""

    def close(self):
        if self.h:
            self.h.detach()
        if self.sec:
            self.sec.close()

    def wait(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end and self.alive():
            self.h.poll_replies()
            time.sleep(min(0.02, max(0.0, end - time.monotonic())))

    def call(self, side, cmd, args=None, timeout=None, label=None):
        t0 = time.monotonic()
        q0 = S.qpc()
        rid, st = self.h.send(side, cmd, args or {})
        self.stats["sent"] += 1
        rep = self.h.wait_reply(rid, timeout or self.a.cmd_timeout) if st == S.RW_OK else None
        self.sess.event("reply", side=side, cmd=cmd, args=args or {}, id=rid, reply=rep, qpc=q0, label=label,
                        ms=round((time.monotonic() - t0) * 1000, 1))
        if rep is None:
            self.stats["noReply"] += 1
        elif rep.get("ok"):
            self.stats["ok"] += 1
        else:
            self.stats["failed"] += 1
            err = str(rep.get("error", ""))
            if "unknown" in err.lower() and cmd not in self.stats["unknown"]:
                self.stats["unknown"].append(cmd)
        return rep

    @staticmethod
    def ok(rep):
        return bool(rep) and bool(rep.get("ok"))

    def sn_s3(self, action, *args, label=None, timeout=None):
        """One Subnautica S3 action (SN_ACTIONS); an action Subnautica refuses as unknown is not sent again."""
        if action in self.unsupported:
            return None
        cmd, build = self.SN_ACTIONS[action]
        rep = self.call("sn", cmd, build(*args), timeout=timeout, label=label or "sn." + action)
        if rep is not None and not rep.get("ok") and "unknown" in str(rep.get("error", "")).lower():
            self.unsupported.add(action)
            self.log.say("  S3: Subnautica does not take %s (%s): its rows will be MISSING" % (action, rep.get("error")))
        return rep

    def sn_status(self):
        rep = self.sn_s3("status", label="sn.status")
        return rep if self.ok(rep) else {}

    def part(self, name, phase):
        self.sess.event("part", name=name, phase=phase, qpc=S.qpc(), c7=self.c7())
        self.log.say("  S3 part %s %s" % (name, phase), ev="s3_part", part=name, phase=phase)

    def c7(self):
        if not self.sec:
            return {}
        st, _q, d = S.seqlock_read(self.sec, L.REGION["C7"].offset, L.SN_STATUS.size)
        if not d:
            return {}
        v = L.SN_STATUS.unpack(d)
        return {k: v[k] for k in ("snFrame", "presentedSeTick", "mirroredEntityCount", "pendingParts", "frameMsP50",
                                  "frameMsP99", "snPhase", "flags")}

    def c1_tick(self):
        st, _q, d = S.seqlock_read(self.sec, L.REGION["C1"].offset, L.SE_FRAME.size)
        return L.SE_FRAME.unpack(d)["tick"] if d else 0

    def settle(self, frames=S3_SETTLE_FRAMES, min_s=S3_SETTLE_MIN_S, timeout=20.0):
        """ROADMAP: 90 Subnautica frames presenting the SE tick of the pose (C7 presentedSeTick >= the C1 tick of the
 command, then snFrame + 90), and at least min_s for streaming."""
        t0 = time.monotonic()
        tick = self.c1_tick()
        f0 = None
        while time.monotonic() - t0 < timeout and self.alive():
            c = self.c7()
            if c.get("presentedSeTick", 0) >= tick:
                if f0 is None:
                    f0 = c.get("snFrame", 0)
                if c.get("snFrame", 0) - f0 >= frames and time.monotonic() - t0 >= min_s:
                    return True
            self.wait(0.05)
        if self.standin_sn:
            return True
        self.log.say("  S3: settle: %d frames not seen within %.0f s (presentedSeTick %s, tick %d)" % (
            frames, timeout, self.c7().get("presentedSeTick"), tick))
        return False

    def teleport(self, x, y, z, yaw=0.0, label=None):
        return self.call("se", "de.teleport", {"x": x, "y": y, "z": z, "yawDeg": yaw, "label": label or ""},
                         label=label)

    def look(self, d, label=None):
        return self.call("se", "de.look", {"dir": d, "label": label or ""}, label=label)

    def wait_file(self, path, timeout):
        t0, last = time.monotonic(), -1
        while time.monotonic() - t0 < timeout:
            try:
                n = os.path.getsize(path)
            except OSError:
                n = -1
            if n > 0 and n == last:
                return True
            last = n
            self.wait(0.05)
        return False

    def capture(self, name, target, spot, time_=None, label=None):
        """Harness flow: Subnautica's de.capture {path, checkId} (the S1 capture service: PNG at the end of the next
 presented frame, UI hidden, sidecar <png>.json); else SE's de.capture (the C10 relay). Waits for the PNG."""
        self.cap_n += 1
        fname = "%s-%s-%s-%03d.png" % (spot, name, target, self.cap_n)
        rel = "s3/%s/%s" % (self.stamp, fname)
        args = {"path": rel, "checkId": "S3.%s" % name, "label": label or "%s.%s" % (name, target), "kind": 1}
        rep = self.call("sn", "de.capture", args, timeout=10.0, label="capture")
        via = "sn"
        if not self.ok(rep):
            rep = self.call("se", "de.capture", args, timeout=10.0, label="capture")
            via = "se"
        full = os.path.join(CAPTURES, *rel.split("/"))
        got = self.wait_file(full, 2.0 if self.standin_sn else 8.0)
        self.sess.event("capture", name=name, target=target, spot=spot, time=time_, png="captures/" + fname,
                        source=full, written=got, via=via, reply=rep, c7=self.c7(), qpc=S.qpc())
        return got

    def c7_frame_times(self, label):
        """The launcher's own C11 frame-time samples: C7 frameMsP50 over 600 Subnautica frames."""
        c = self.c7()
        f0, t0, samples = c.get("snFrame", 0), time.monotonic(), []
        while time.monotonic() - t0 < 30.0 and self.alive():
            c = self.c7()
            if c.get("frameMsP50"):
                samples.append(round(c["frameMsP50"], 4))
            if c.get("snFrame", 0) - f0 >= 600:
                break
            self.wait(0.1)
        self.sess.event("perf", label=label, samples=samples, frames=self.c7().get("snFrame", 0) - f0)

    def sn_frame_time(self, label, timeout=40.0):
        """Subnautica's own 600-frame measure (de.s3 frametime; DE.S3 FRAMETIME... mirrorVisible=...)."""
        before = self.sn_status().get("lastFrameTime")
        rep = self.sn_s3("frametime", 600, label="frametime." + label)
        if not self.ok(rep):
            return None
        t0 = time.monotonic()
        while time.monotonic() - t0 < timeout and self.alive():
            self.wait(0.5)
            got = self.sn_status().get("lastFrameTime")
            if got and got != before and got != "running":
                self.sess.event("snFrameTime", label=label, line=got)
                return got
        return None

    def wait_mirror(self, timeout):
        t0 = time.monotonic()
        while time.monotonic() - t0 < timeout and self.alive():
            c = self.c7()
            if c.get("mirroredEntityCount", 0) >= 1 and c.get("pendingParts", 1) == 0:
                self.sess.event("mirror", seconds=round(time.monotonic() - t0, 2), c7=c)
                return True
            self.wait(0.1)
        self.sess.event("mirror", seconds=None, c7=self.c7())
        self.log.say("  S3: no mirrored entity with 0 pending parts in C7 after %.0f s" % timeout)
        return False

    # ------------------------------------------------------------------------------------------------ the scene
    def seabed_at(self, spot, pts):
        """Subnautica's layer-30 probe per point (de.s3 action probe, warp: the camera is held near the expected seabed);
 None where it fails. The collision clipmap builds only +-16 m around Subnautica's camera, so when nothing is hit
 within S3_PROBE_HOLD_S the camera is re-held S3_PROBE_STEP_M lower (a new probe supersedes the pending one in the
 plugin): real runs and 18:45 at the grandReef spot found no collider within +-16 m of a camera
 at -147 m, so the seabed there is below -163 m, not the -150 m guessed."""
        out = []
        for x, z in pts:
            y = None
            guess = S3_SEABED_FALLBACK.get(spot, -150.0) - 2.0
            for k in range(S3_PROBE_HOLDS):
                hold = guess - S3_PROBE_STEP_M * k
                if not self.alive():
                    break
                rep = self.sn_s3("probe", x, z, hold, label="probe." + spot)
                if not self.ok(rep):
                    break
                if not rep.get("pending") and isinstance(rep.get("seabedY"), (int, float)):
                    y = rep["seabedY"]
                    break
                t0 = time.monotonic()
                while time.monotonic() - t0 < S3_PROBE_HOLD_S and self.alive():
                    self.wait(0.5)
                    st = self.sn_status()
                    if st.get("probe") == "ok" and abs((st.get("probeX") or 1e9) - x) < 1e-3 and                             abs((st.get("probeZ") or 1e9) - z) < 1e-3:
                        y = st.get("seabedY")
                        break
                    if st.get("probe") == "failed":
                        break
                if isinstance(y, (int, float)):
                    break
                self.log.say("  S3: probe (%g, %g): no terrain collider within +-16 m of a camera held at %g m, lower" % (
                    x, z, hold + 5.0))
            out.append(y if isinstance(y, (int, float)) and y == y else None)
        self.sess.event("seabed", spot=spot, points=[list(p) for p in pts], seabedY=out)
        return out

    def go_spot(self, spot, x, z):
        y0 = S3_SEABED_FALLBACK.get(spot, -150.0) + 10.0
        self.teleport(x, y0, z - 8.0, 0.0, "s3.%s.arrive" % spot)
        self.settle(min_s=3.0)

    def place(self, spot, x, z, lift=0.0, **extra):
        """Seabed under the block (Subnautica's layer-30 probe; the spot's fallback otherwise), then SE de.s3 place with
 the block centre at the seabed (lower half buried) + lift."""
        h = self.seabeds.get((spot, "block"))
        if h is None:
            got = self.seabed_at(spot, [(x, z)])
            h = got[0] if got[0] is not None else S3_SEABED_FALLBACK.get(spot, -150.0)
            self.seabeds[(spot, "block")] = h
            self.seabeds.setdefault((spot, "seamoth"), h)
        centre = [x, h + lift, z]
        args = {"place": dict({"sn": centre, "yawDeg": self.a.heading, "label": spot}, **extra)}
        rep = self.call("se", "de.s3", args, timeout=10.0, label="place." + spot)
        if not self.ok(rep):
            self.log.say("  S3: de.s3 place refused by SE: %s" % (json.dumps(rep)[:200] if rep else "no reply"))
            self.sess.event("place", spot=spot, centre=centre, reply=rep, ok=False)
            return None
        self.places[spot] = rep
        self.sess.event("place", spot=spot, centre=centre, seabedY=h, reply=rep, ok=True)
        self.wait_mirror(5.0 if self.standin_sn else 15.0)
        self.sn_s3("placement", label="placement." + spot)
        return rep

    def lab_run(self, spot, tag, captures, timeout=None):
        """Subnautica's lab run at the spot's block: probe, the mirrored block, the reference Seamoth (kinematic, lights
 off) 6 m to its right, then each capture at the plan pose (camera override, 90 frames) with the S1 sidecar and the
 S3 sidecar. Follows de.s3 status until Done/Failed; Subnautica's CPU time is sampled at each finished capture (the
 C10 pair). None when Subnautica has no lab run (the caller falls back to the harness flow)."""
        rep = self.places.get(spot) or {}
        lab = rep.get("labSn") or rep.get("centreSn")
        if not lab or "lab" in self.unsupported:
            return None
        session = "%s-%s%s" % (self.stamp, spot, "-" + tag if tag else "")
        h = self.seabeds.get((spot, "block"))
        from_y = (h if isinstance(h, (int, float)) else S3_SEABED_FALLBACK.get(spot, -150.0)) + 8.0   # camera at +10
        got = self.sn_s3("lab", lab[0], lab[2], captures, session, self.a.lab_heading, self.a.lab_camera, from_y,
                         label="lab.%s.%s" % (spot, tag or "-"), timeout=10.0)
        if not self.ok(got):
            self.sess.event("labrun", session=session, spot=spot, tag=tag, captures=captures, reply=got, state="refused")
            return None
        n = int(got.get("captures") or len(captures))
        limit = timeout or (90.0 + 6.0 * n)
        t0, last_ok, cpu, st = time.monotonic(), -1, [], {}
        while time.monotonic() - t0 < limit and self.alive():
            self.wait(0.25)
            st = self.sn_status()
            k = st.get("labOk")
            if isinstance(k, (int, float)) and k != last_ok:
                last_ok = k
                cpu.append({"labOk": k, "cpu": process_cpu_seconds(self.sn.pid), "qpc": S.qpc()})
            if st.get("labSession") == session and st.get("lab") in ("Done", "Failed"):
                break
        self.labs[spot] = st
        self.lab_sessions.append(session)
        self.sess.event("labrun", session=session, spot=spot, tag=tag, captures=captures, reply=got, state=st.get("lab"),
                        status=st, cpu=cpu, seconds=round(time.monotonic() - t0, 1))
        if st.get("lab") != "Done":
            self.log.say("  S3: lab run %s ended %s (%s)" % (session, st.get("lab"), st.get("labError")))
        return st

    def centre(self, spot, target):
        rep = self.places.get(spot) or {}
        if target == "block":
            c = rep.get("centreSn") or rep.get("labSn")
            return tuple(c) if c else None
        if target == "glyph":
            g = rep.get("glyph") or {}
            pl = g.get("partLocal")
            return s3_apply(rep, pl[9:12]) if pl and rep.get("o") else None
        c = self.centre(spot, "block")
        return (c[0] + S3_SEAMOTH_OFFSET[0], c[1], c[2] + S3_SEAMOTH_OFFSET[2]) if c else None

    def pose(self, name, spot, target):
        """Harness camera pose (x, y, z, yawDeg, look) relative to the target's centre c and the seabed h under it: P1
 front (south) 6 m at h + 0.6 looking north, P2 north side 6 m at 0.3 m above the seabed there looking south, P3 10 m
 above looking down, P4 front 40 m at h + 2, N1 4.5 m in front of the glyph block."""
        c = self.centre(spot, target)
        if c is None:
            return None
        h = self.seabeds.get((spot, target), self.seabeds.get((spot, "block"), c[1]))
        if name == "P1":
            return (c[0], h + 0.6, c[2] - 6.0, 0.0, None)
        if name == "P2":
            got = self.seabed_at(spot, [(c[0], c[2] + 6.0)])
            hc = got[0] if got[0] is not None else h
            return (c[0], hc + 0.3, c[2] + 6.0, 180.0, None)
        if name == "P3":
            return (c[0], c[1] + 10.0, c[2], 0.0, "down")
        if name == "P4":
            return (c[0], h + 2.0, c[2] - 40.0, 0.0, None)
        if name == "N1":
            return (c[0], c[1], c[2] - 4.5, 0.0, None)
        raise ValueError(name)

    def at(self, name, spot, target, frames=S3_SETTLE_FRAMES):
        p = self.pose(name, spot, target)
        if p is None:
            self.log.say("  S3: no %s pose for %s at %s (no placement)" % (name, target, spot))
            return False
        self.teleport(p[0], p[1], p[2], p[3], "%s.%s.%s" % (spot, name, target))
        if p[4]:
            self.look(p[4], "%s.%s.%s" % (spot, name, target))
        self.settle(frames)
        return True

    def shoot(self, name, spot, targets=("block", "seamoth"), pose=None, time_=None):
        for t in targets:
            if self.at(pose or name, spot, t):
                self.capture(name, t, spot, time_)

    # ------------------------------------------------------------------------------------------------ parts
    def p_place(self):
        """deep spot: arrive, Subnautica's seabed probe, the launcher's C7 frame times at P1 without the block, de.s3 count
 (C12 baseline), SE de.s3 place (block centre at the seabed), mirror wait, placement lines, frame times with the
 block (C7 samples; Subnautica's own 600-frame measure with the mirror visible and hidden)"""
        x, z = self.a.deep_spot
        self.go_spot("deep", x, z)
        self.seabeds.pop(("deep", "block"), None)
        got = self.seabed_at("deep", [(x, z)])
        h = got[0] if got[0] is not None else S3_SEABED_FALLBACK["deep"]
        self.seabeds[("deep", "block")] = self.seabeds[("deep", "seamoth")] = h
        self.teleport(x, h + 0.6, z - 6.0, 0.0, "deep.P1.without")
        self.settle()
        self.c7_frame_times("without")
        self.sn_s3("count", label="count.baseline")
        if self.place("deep", x, z) is None:
            return
        self.settle()                       # same pose; the Seamoth comes later, so the delta is the block's alone
        self.c7_frame_times("with")
        if self.sn_frame_time("with") is not None:
            self.sn_s3("visible", False, label="visible.off")
            self.sn_frame_time("without")
            self.sn_s3("visible", True, label="visible.on")

    def p_geometry(self):
        """C1-C4 come from the logs; G1-E2: Subnautica's shear test at s 1.0284 and 1.0345 (de.s3 action shear, 360
 one-degree frames each), then the placement lines"""
        for s in (1.0284, 1.0345):
            before = self.sn_status().get("lastShear")
            rep = self.sn_s3("shear", s, label="shear.%g" % s, timeout=10.0)
            if not self.ok(rep):
                break
            t0 = time.monotonic()
            while time.monotonic() - t0 < 30.0 and self.alive():
                self.wait(0.5)
                got = self.sn_status().get("lastShear")
                if got and got != "running" and got != before:
                    break
        self.sn_s3("placement", label="placement.geometry")
        self.call("se", "de.s3", {"status": True}, label="se.status")

    def p_views(self):
        """Subnautica's lab run at the deep spot: P1, P2, P4 (the plan frames block and Seamoth together; P2 carries the
 buried-corner rays); without a lab run: the harness flow (P2 block, P1 and P4 per target)"""
        if self.lab_run("deep", "", ["P1", "P2", "P4"]) is not None:
            return
        if self.at("P2", "deep", "block"):
            self.capture("P2", "block", "deep")
        self.shoot("P1", "deep")
        self.shoot("P4", "deep")

    def p_sun(self):
        """de.time {t: 0.5} (noon) -> P1; de.time {t: 0.0} (midnight) -> P1; back to 0.5 (the plugin's own time
 command, never the console's daynight); lab runs, else the harness flow"""
        for name, f in (("noon", 0.5), ("midnight", 0.0)):
            self.sn_s3("time", f, label="time.%s" % name)
            self.wait(2.0)
            if self.lab_run("deep", name, ["P1"]) is None:
                self.shoot("P1-" + name, "deep", pose="P1", time_=f)
        self.sn_s3("time", 0.5, label="time.restore")

    def p_n2(self):
        """P1 (harness camera), SE GPS-marker pixels (de.s3 gps on) and Subnautica's S1 camera trace (de.trace on: the
 block centre is projected through it): rest 2 s, the 0.5 Hz +-30 deg yaw sine for 10 s (de.motion kind yaw),
 rest 2 s"""
        if not self.at("P1", "deep", "block"):
            return
        self.call("se", "de.s3", {"gps": "on"}, label="gps.on")
        self.sn_s3("trace", True, label="n2.trace.on")
        self.sess.event("n2", phase="rest-begin", qpc=S.qpc())
        self.wait(S3_N2["restS"])
        self.sess.event("n2", phase="motion-begin", qpc=S.qpc())
        rep = self.call("se", "de.motion", {"kind": "yaw", "amplitudeDeg": S3_N2["amplitudeDeg"], "hz": S3_N2["hz"],
                                            "seconds": S3_N2["seconds"], "label": "n2.yaw"}, label="n2.motion")
        if not self.ok(rep):
            self.log.say("  S3.N2: de.motion kind yaw refused: %s" % (json.dumps(rep)[:160] if rep else "no reply"))
        self.wait(S3_N2["seconds"] + 0.3)
        self.sess.event("n2", phase="motion-end", qpc=S.qpc())
        self.wait(S3_N2["restS"])
        self.sess.event("n2", phase="rest-end", qpc=S.qpc())
        self.call("se", "de.s3", {"gps": "off"}, label="gps.off")
        self.sn_s3("trace", False, label="n2.trace.off")

    def n3_pairs(self, look, pitch):
        rep = self.places.get("deep") or {}
        corners = rep.get("cornersSn")
        lo, hi = rep.get("localMin"), rep.get("localMax")
        for i in range(8):
            label = "n3.%s.c%d" % (look, i)
            se = self.call("se", "de.s3", {"pixel": {"corner": i}, "label": label}, label=label)
            if not self.ok(se):
                p = corners[i] if corners else (s3_apply(rep, [hi[k] if (i >> k) & 1 else lo[k] for k in range(3)])
                                                if lo and hi and rep.get("o") else None)
                if p is None:
                    continue
                se = self.call("se", "de.pixel", {"x": p[0], "y": p[1], "z": p[2], "label": label}, label=label)
            px = (se or {}).get("sePx") or (se or {}).get("px")
            tick = (se or {}).get("tick")
            scr = (se or {}).get("screen") or [2560, 1440]
            if not px or tick is None or not (0 <= px[0] <= scr[0] and 0 <= px[1] <= scr[1]):
                self.sess.event("pixel", label=label, look=look, pitchDeg=pitch, se=se, sn=None, s=rep.get("s"),
                                skipped="corner not in view or no SE pixel")
                continue
            pt = (se or {}).get("pointSn") or (corners[i] if corners else None)
            sn = self.call("sn", "de.pixel", {"pointSn": pt, "minSeTick": int(tick), "label": label}, label=label) \
                if pt else None
            self.sess.event("pixel", label=label, look=look, pitchDeg=pitch, se=se, sn=sn, s=rep.get("s"))
            self.wait(0.2)

    def p_n3(self):
        """P1 (level look): SE's pixel of each block corner (de.s3 pixel corner, else de.pixel of the corner's SN point)
 and Subnautica's WorldToScreenPoint of the same SN point at the first frame presenting that SE tick (S1 de.pixel,
 logged DE.SnPixel); then a 30 deg down look from 4 m above (the S1.G1-E5 offset applies)"""
        if self.at("P1", "deep", "block"):
            self.n3_pairs("level", 0.0)
        c = self.centre("deep", "block")
        if c:
            self.teleport(c[0], c[1] + 4.0, c[2] - 6.0, 0.0, "n3.tilt")
            self.look("pitch-30", "n3.tilt")
            self.settle()
            self.n3_pairs("tilt", 30.0)
        self.wait(1.0)

    def p_n1(self):
        """N1 (S3a): the block re-placed 1.5 m higher with the glyph block at grid cell (1, 0, 0) (de.s3 place glyph: the
 4-quadrant colour map and the 8 x 8-cell bump map: up/down and right/left checkers per colour quadrant), a lab run P1 there (the sun direction in its sidecar), the
 harness capture 4.5 m in front of the glyph; then the plain block back at the seabed"""
        x, z = self.a.deep_spot
        for t in ("block", "seamoth"):
            if self.seabeds.get(("deep", t)) is not None:
                self.seabeds[("n1", t)] = self.seabeds[("deep", t)]
        rep = self.place("n1", x, z, lift=1.5, glyph=True)
        if rep is None or not rep.get("glyph"):
            self.log.say("  S3.N1: SE placed no glyph block: N1 rows MISSING")
        else:
            self.lab_run("n1", "", ["P1"])
            if self.at("N1", "n1", "glyph"):
                self.capture("N1", "glyph", "n1")
        self.place("deep", x, z)

    def p_plate(self):
        """S3b: a 3 x 3 wall of LargeBlockArmorBlock in one colour with SE's colour export (de.s3 place plate, export),
 P1-plate (C14 seams) and P1-paint (C13; Subnautica has no neutral-light mode yet: compared under the scene's
 light), harness captures 12 m in front"""
        x, z = self.a.deep_spot
        for t in ("block", "seamoth"):
            if self.seabeds.get(("deep", t)) is not None:
                self.seabeds[("plate", t)] = self.seabeds[("deep", t)]
        rep = self.place("plate", x, z, lift=1.25, plate={"rows": 3, "cols": 3, "orientation": "wall"},
                         colorHsv=list(self.a.paint_hsv), export=True)
        if rep is None:
            return
        c = self.centre("plate", "block")
        self.teleport(c[0], c[1] + 2.5, c[2] - 12.0, 0.0, "plate.P1")
        self.settle()
        self.capture("P1-plate", "block", "plate")
        self.capture("P1-paint", "block", "plate")
        self.place("deep", x, z)

    def p_shallow(self):
        """shallow spot (< 20 m): place, de.time c9 (a 45-degree sun for shadows: --c9-time 0.24), Subnautica's lab run P3, P3+1s (the C10 pair,
 its CPU time sampled at each finished capture) and P3 x burst (C8); without a lab run the harness flow; de.time
 back to 0.5"""
        x, z = self.a.shallow_spot
        self.go_spot("shallow", x, z)
        if self.place("shallow", x, z) is None:
            return
        self.sn_s3("time", self.a.c9_time, label="time.c9")
        self.sess.event("sun", t=self.a.c9_time, elevationDeg=round(s3_sun_elevation_deg(self.a.c9_time), 2),
                        note="Subnautica's sun elevation expected at this de.time (S3.C9)")
        self.wait(2.0)
        n = self.a.burst or (8 if self.q else 60)
        if self.lab_run("shallow", "", ["P3", "P3+1s", "P3*%d" % n]) is None:
            self.shoot("P3", "shallow", time_=self.a.c9_time)
            if self.at("P3", "shallow", "block"):
                self.capture("P3-a", "block", "shallow", self.a.c9_time)
                cpu0 = process_cpu_seconds(self.sn.pid)
                self.wait(1.0)
                cpu1 = process_cpu_seconds(self.sn.pid)
                self.capture("P3-b", "block", "shallow", self.a.c9_time)
                self.sess.event("cpu", label="c10", seconds=(cpu1 - cpu0) if cpu0 is not None and cpu1 is not None
                                else None, pid=self.sn.pid)
            for t in ("block", "seamoth"):
                if self.at("P3", "shallow", t):
                    for _k in range(n):
                        self.capture("P3-burst", t, "shallow", self.a.c9_time)
        self.sn_s3("time", 0.5, label="time.restore")

    def p_teardown(self):
        """C12: Subnautica's de.s3 teardown (mirror objects and the Seamoth destroyed), then de.s3 count (roots, preserved
 roots, our meshes)"""
        rep = self.sn_s3("teardown", label="teardown", timeout=10.0)
        self.sess.event("teardown", reply=rep, qpc=S.qpc())
        self.wait(2.0)
        self.sn_s3("count", label="count.after")

def s3_plan_text(a, parts) -> list:
    out = ["  mode %s; SE: %s" % (a.mode, "the fake producer (tests/standins/fake_se_producer.py --s3a --s2)" if
                                  a.mode == "S3a" else "Space Engineers (Legacy.exe -continue)"),
           "  spots: deep SN (%g, %g) (fallback seabed %g), shallow SN (%g, %g) (fallback seabed %g); heading %g deg; "
           "Seamoth offset %s" % (a.deep_spot[0], a.deep_spot[1], S3_SEABED_FALLBACK["deep"], a.shallow_spot[0],
                                  a.shallow_spot[1], S3_SEABED_FALLBACK["shallow"], a.heading, S3_SEAMOTH_OFFSET),
           "  captures: Subnautica's lab runs (de.s3 {lab: [x, z], captures, camera %s, headingDeg %g, session "
           "<stamp>-<spot>[-<tag>]}: its plan poses, S1 + S3 sidecars); the harness flow where a part needs the SE camera or "
           "no lab run exists: de.capture {path: s3/<stamp>/<spot>-<name>-<target>-NNN.png} (else SE C10) after %d frames; "
           "collected into <out>/captures" % (a.lab_camera, a.lab_heading, S3_SETTLE_FRAMES)]
    for p in parts:
        doc = (getattr(S3Runner, "p_" + p).__doc__ or "").strip().replace("\n", " ")
        out.append("  part %-9s %s" % (p, " ".join(doc.split())))
    out.append("  Subnautica S3 actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in S3Runner.SN_ACTIONS.items()))
    return out

def s3_analyze(log: Log, a, out: str) -> int:
    AN = s3_analyzer()
    try:
        rows = AN.evaluate(out, getattr(a, "s1_results", None), getattr(a, "baseline_logs", None))
    except Exception as e:     # an analyser bug must not hide the session
        import traceback
        rows = [{"id": "S3", "status": "FAIL", "measured": "analyser error %s" % type(e).__name__, "threshold": "-",
                 "what": str(e)[:120] + " | " + traceback.format_exc().strip().splitlines()[-1][:120], "part": "all",
                 "required": True}]
    strict = not getattr(a, "lenient", False)
    ok = AN.write_results(out, rows, strict, {"tool": "launcher s3"})
    print("\nS3 ORACLE TABLE (%s)  (* = not required; INFO = information)" % out)
    print(AN.C.table(rows))
    log.say("S3 ORACLE %s: %d rows, %d FAIL, %d MISSING -> %s" % (
        "PASS" if ok else "FAIL", len(rows), sum(1 for r in rows if r["status"] == "FAIL" and r.get("required", True)),
        sum(1 for r in rows if r["status"] == "MISSING" and r.get("required", True)),
        os.path.join(out, "s3-results.json")), ev="s3_result", ok=ok)
    return 0 if ok else 1

def s3_collect_extra(log: Log, out: str, stamp: str, t_start: float, standin_sn: bool):
    cap = os.path.join(CAPTURES, "s3", stamp)
    if os.path.isdir(cap):
        shutil.copytree(cap, os.path.join(out, "captures"), dirs_exist_ok=True)
    # Subnautica's lab runs write s3\<stamp>-<spot>[-<tag>]\<label>_f<frame>.png (+.json,.s3.json)
    for d in sorted(glob.glob(os.path.join(CAPTURES, "s3", stamp + "-*"))):
        if os.path.isdir(d):
            shutil.copytree(d, os.path.join(out, "captures", os.path.basename(d)), dirs_exist_ok=True)
    if not standin_sn and os.path.exists(PLAYER_LOG) and os.path.getmtime(PLAYER_LOG) >= t_start - 5:
        try:
            shutil.copy2(PLAYER_LOG, os.path.join(out, "Player.log"))
        except OSError as e:
            log.say("  collect: Player.log not copied (%s)" % e)
    n = sum(len(f) for _r, _d, f in os.walk(os.path.join(out, "captures"))) if os.path.isdir(os.path.join(out, "captures")) \
        else 0
    log.say("S3 collected: %d capture files%s" % (n, ", Player.log" if os.path.exists(os.path.join(out, "Player.log"))
                                                  else ""), ev="s3_collected", captures=n)

def s3(log: Log, a) -> int:
    if getattr(a, "list_mutant_groups", False):
        for name, g in S3_MUTANT_GROUPS.items():
            print("  %-17s mutants %-15s parts %-30s red %s%s" % (name, ",".join(g["mutants"]), ",".join(g["parts"]),
                                                               ",".join(g["red"]), "; watch %s" % ",".join(g["watch"])
                                                               if g.get("watch") else ""))
        return 0
    if a.analyze:
        return s3_analyze(log, a, a.analyze)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(REPO, "tests", "out", "s3", stamp)
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    parts = None
    if a.mutant_group:
        g = S3_MUTANT_GROUPS.get(a.mutant_group)
        if g is None:
            log.say("unknown mutant group %s (known: %s)" % (a.mutant_group, ",".join(S3_MUTANT_GROUPS)))
            return 2
        mutants = list(g["mutants"])
        parts = list(g["parts"])
    if a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    parts = parts or list(S3_DEFAULT_PARTS[a.mode])
    unknown = [x for x in parts if x not in S3_PARTS]
    if unknown:
        log.say("unknown parts %s (known: %s)" % (unknown, ",".join(S3_PARTS)))
        return 2
    if parts[0] != "place":
        parts = ["place"] + [p for p in parts if p != "place"]
    bad_m = [m for m in mutants if m not in S3_SN_MUTANTS + ("S3.M4",)]
    if bad_m:
        log.say("unknown mutants %s" % bad_m)
        return 2
    sn_env, se_env = {}, {}
    sn_m = [m for m in mutants if m in S3_SN_MUTANTS]
    if sn_m:
        sn_env["DEEPENGINEERS_MUTANT"] = ",".join(sn_m)
    if getattr(a, "sn_standin", False) and not a.sn_cmd:
        # S3b's SE side alone: the fake SN host validates SE's C3/C4 records as the SN plugin
 # does and logs the C1/C2/C3 lines (fake_sn_host.py "S3"); it renders nothing, so the image rows stay MISSING
        host = [sys.executable, "-I", os.path.join(REPO, "tests", "standins", "fake_sn_host.py"), "--name", a.name, "--s1",
                "--s3", "--quit-on-se-exit", "--quiet"]
        host_m = [m for m in sn_m if m in ("S3.M1", "S3.M2")]
        if len(host_m) == 1:
            host += ["--s3-mutant", host_m[0][3:]]
        a.sn_cmd = json.dumps(host)
    a.cont = not a.no_continue
    if a.mode == "S3a" and not a.se_cmd:
        cmd = [sys.executable, "-I", os.path.join(REPO, "tests", "standins", "fake_se_producer.py"), "--name", a.name,
               "--s3a", "--s2", "--quiet", "--load-after", "1.0", "--load-seconds", "1.0", "--s3a-heading",
               "%g" % a.heading, "--s3a-texture-set", a.texture_set, "--screen", a.screen, "--fov", "%g" % a.fov]
        if "S3.M4" in mutants:
            cmd += ["--s3-mutant", "M4"]
        a.se_cmd = json.dumps(cmd)
    elif "S3.M4" in mutants:
        se_env["DEEPENGINEERS_MUTANTS"] = "S3.M4"
    sn_cmd, se_cmd = session_commands(a)
    log.say("S3 session %s (%s): parts %s%s%s; Subnautica: %s; SE: %s; out %s" % (
        stamp, a.mode, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants else
        "", subprocess.list2cmdline(sn_cmd), subprocess.list2cmdline(se_cmd), out), ev="s3_plan")
    if a.dry_run:
        for line in s3_plan_text(a, parts):
            print(line)
        if mutants:
            print("  mutants: Subnautica DEEPENGINEERS_MUTANT=%s; %s" % (sn_env.get("DEEPENGINEERS_MUTANT", "-"),
                  "S3.M4 at the producer" if "S3.M4" in mutants else "no producer mutant"))
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    sess = S2SessionLog(os.path.join(out, "s3-session.jsonl"))
    standin_sn, standin_se = bool(a.sn_cmd), bool(a.se_cmd)
    sess.event("start", stamp=stamp, mode=a.mode, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:],
               quick=a.quick, standin={"sn": standin_sn, "se": standin_se}, snCmd=sn_cmd, seCmd=se_cmd,
               qpcFrequency=S.QPC_FREQUENCY, deepSpot=list(a.deep_spot), shallowSpot=list(a.shallow_spot),
               headingDeg=a.heading, c9Time=a.c9_time, snEnv=sn_env, seEnv=se_env)
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and wait_in_world(log, sess, a, sn, se):
            runner = S3Runner(log, a, sess, sn, se, out, stamp, standin_sn, standin_se)
            ok, why = runner.attach()
            if not ok:
                log.say("S3: harness not usable: %s" % why, ev="s3_harness_refused")
                rc = 1
            else:
                for p in parts:
                    if not runner.alive():
                        log.say("S3: a game exited before part %s" % p, ev="s3_exit_during_script")
                        break
                    runner.part(p, "begin")
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:
                        log.say("S3 part %s failed: %r" % (p, e), ev="s3_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
                    runner.part(p, "end")
                if a.mode == "S3b" and runner.alive():
                    # nothing of the lab stays in SE: the S3 grids (MyEntity.Save false) and the
 # GPS marker (saved with the session's GPS list by an autosave) are removed at the end of the script
                    runner.call("se", "de.s3", {"remove": True}, label="se.remove")
                log.say("S3 script finished: %s" % json.dumps(runner.stats), ev="s3_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            close_session(log, a, sn, se, standin_se, standin_sn)
        S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None, unsupported=sorted(runner.unsupported) if
                   runner else None)
        sess.close()
        collect(log, out, t_start, sn, standin_sn, standin_se, stamp)
        s3_collect_extra(log, out, stamp, t_start, standin_sn)
    if rc is not None:
        log.say("S3 session aborted (rc %d); analysing what was collected" % rc)
    res = s3_analyze(log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# s4: one scripted S4 "Planet + terrain" session (ROADMAP S4 oracle; tests/s4/analyze_s4.py)
# ----------------------------------------------------------------------------------------------------------------
# Modes: S4se = SE alone (the passive stand-in host keeps the section for C11; Legacy.exe -continue: DE Lab, or with
# --world the throwaway copy the LastSession points at); S4both = both games (the s3 S3b recipe). The SE side is the
# plugin's `de.terrain` command (S4_SE_ACTIONS), the Subnautica side its `de.s4` raycast service (S4_SN_ACTIONS); an
# action a plugin refuses as unknown is not sent again and its rows stay MISSING. The plugin's s4.* JSONL rows are read
# back while the session runs (S4SeTail: the newest se-*.jsonl in %LOCALAPPDATA%\DeepEngineers\logs, read-only, shared)
# so the async actions (at1, drop, walk, perf, edit) are awaited by their rows, and every tailed row of a part goes into
# s4-session.jsonl ("rays", "drops", "walks", "perf", "edit" events), so the analyzer never mixes parts.
# Reference heights for the plan (edit points, the WM-6 flat spot, N1 points) come from tools/worldmap/worldmap_ref.py
# (OctreeColumns, the user's octrees read in place); without them the plan falls back to the constants below.

S4_DIR = os.path.join(REPO, "tests", "s4")
S4_MODES = ("S4se", "S4both")
S4_PARTS_SE = ("gate", "tiles", "n3", "hash", "at1", "bake", "drop", "walk", "n1", "perf", "at0b")
S4_PARTS_BOTH = ("gate", "tiles", "at2", "wm6", "n1", "capture")
# orchestrator-only parts on a throwaway world copy (tools/lab/make_terrain_copy.py; autosave off; ROADMAP rule 10)
S4_PARTS_COPY = ("n2", "g2e2save", "g2e3b", "g2e2noplugin", "g2e5", "m4", "m10")
S4_DEFAULT_PARTS = {"S4se": ("gate", "tiles", "n3", "hash", "at1", "bake", "drop", "walk", "n1", "perf"),
                    "S4both": ("gate", "tiles", "at2", "wm6", "n1", "capture")}
S4_PART_MUTANT = {"m4": "S4.M4", "m10": "S4.M10"}
S4_SHALLOW = (0.0, 0.0)                  # Safe Shallows anchor: the S3 layer-30 seabed -17.212 m (reference -17.208)
S4_DEEP = (-1029.0, -1116.0)             # grandReef: S3 layer-30 -235.138 m (reference -235.133)
S4_SEABED_FALLBACK = {"shallow": -17.2, "deep": -235.1}
S4_AT1 = {"size": 64, "spacing": 2.5, "fromY": 20.0, "toY": -400.0, "random": 1000}
S4_AT1_QUICK = {"size": 16, "spacing": 2.5, "fromY": 20.0, "toY": -400.0, "random": 100}
S4_AT2 = {"shallowSize": 9, "deepSize": 5, "spacing": 2.5}     # odd sizes: the origin is a column in every grid convention
S4_BAKE_BATCH = (12, 18, 12)             # Safe Shallows batch: SN x, z in [-128, 32], y in [-160, 0]
S4_BAKE_GRID = {"origin": (-48.0, -48.0), "size": 16, "spacing": 2.5}
S4_HASH = {"sn": [0.0, -17.2, 0.0], "half": 32}
S4_WALK = {"from": (5.0, -5.0), "headingDeg": 90.0, "length": 50.0}
S4_PERF = {"from": [-600.0, -8.0, 0.0], "to": [600.0, -8.0, 0.0], "speed": 100.0}
S4_EDIT_RADIUS = {"cutout": 4.0, "shipdrill": 4.0, "explosion": 4.0, "voxelhand": 4.0, "writerange": 4.0, "meteor": 8.0}
S4_SN_PROBE = {"holdAbove": 8.0, "descend": 24.0, "minY": -1950.0, "timeoutMs": 6000}
S4_SETTLE_FRAMES = 90
S4_PARK_Y = -8.0            # SN y of the character parked at the session start (open water over the shallow spot)
S4_PARK_DX = 24.0           # parked 3 cells east of the spot: never straight above the S4.M3/M8 mutant cell at the spot's
 # column
S4_FLAT_RELIEF = 0.15                    # m over a 3 m footprint: "flat" for drops, N1 and WM-6
S4_E2B_WAIT_S = 10.0                     # G2-E2b: the save after the reload comes at least this long after in-world
S4_RAY_STALL_S = 120.0
S4_SAVE_WAIT_S = 300.0
S4_TERRAIN_MUTANTS = ("S4.M2", "S4.M3", "S4.M4", "S4.M5", "S4.M6", "S4.M7", "S4.M8", "S4.M8b", "S4.M10") + tuple(
    "S4.M9.%s" % p for p in ("cutout", "shipdrill", "explosion", "meteor", "voxelhand", "writerange"))
# Seeded-failure groups for the runbook (one launch each; tests/s4/analyze_s4.py MUTANT_ROWS judges them; "copy" =
# the throwaway world copy and --world are required; S4.M1a-d are offline only: tests/DeepEngineers.Terrain.Tests)
S4_MUTANT_GROUPS = {
    "tile-shift": {"mutants": ("S4.M2",), "mode": "S4se", "parts": ("gate", "tiles", "at1"), "red": ("S4.A-T1.1",)},
    "intersect-hole": {"mutants": ("S4.M3",), "mode": "S4se", "parts": ("gate", "tiles", "at1"), "red": ("S4.A-T1.1x",),
                       "watch": ("S4.A-T1.1h and S4.A-T1.1 stay green: SE's prefetch never calls Intersect (review finding 1)",)},
    "manifest": {"mutants": ("S4.M5",), "mode": "S4se", "parts": ("gate", "tiles"), "red": ("S4.GATE",),
                 "watch": ("S4.FAILSAFE stays green: REFUSE, no tiles, fail-safe active; also the S4.A-T1.3m baseline",)},
    "half-voxel": {"mutants": ("S4.M6",), "mode": "S4se", "parts": ("gate", "tiles", "at0b", "at1"), "red": ("S4.A-T0b",),
                   "watch": ("S4.A-T1.1 (0.5 m off)",)},
    "no-prefetch": {"mutants": ("S4.M7",), "mode": "S4se", "parts": ("gate", "tiles", "at1"), "red": ("S4.A-T1.1u",),
                    "watch": ("S4.A-T1.1h stays green",)},
    "emptydata": {"mutants": ("S4.M8",), "mode": "S4se", "parts": ("gate", "tiles", "at1"), "red": ("S4.A-T1.1h",)},
    "emptydata-detect": {"mutants": ("S4.M8b",), "mode": "S4se", "parts": ("gate", "tiles", "n3", "at1"),
                         "red": ("S4.N3",), "watch": ("S4.A-T1.1h stays green",)},
    "guards": {"mutants": tuple("S4.M9.%s" % p for p in ("cutout", "shipdrill", "explosion", "meteor", "voxelhand",
                                                         "writerange")), "mode": "S4se", "parts": ("gate", "tiles", "n2"),
               "red": tuple("S4.N2.%s" % p for p in ("cutout", "shipdrill", "explosion", "meteor", "voxelhand",
                                                     "writerange")), "copy": True},
    "save-true": {"mutants": ("S4.M4",), "mode": "S4se", "parts": ("gate", "tiles", "m4"), "red": ("S4.G2-E2a",),
                  "copy": True, "watch": ("delete the copy afterwards: it holds provider storages",)},
    "random-ids": {"mutants": ("S4.M10",), "mode": "S4se", "parts": ("gate", "tiles", "m10"), "red": ("S4.G2-E3b",),
                   "copy": True, "watch": ("S4.IDS red too; needs a copy saved by a normal g2e2save session",)},
}
S4_PARTS_HELP = {
    "gate": "de.terrain status + gate (fresh manifest); the launcher's own read-only manifest measure",
    "tiles": "de.terrain tiles (34 slots, ids, min corners, save=false)",
    "n3": "de.terrain n3 (the in-game provider self-check)",
    "hash": "de.terrain hash at the Safe Shallows anchor (baseline content/material hashes)",
    "at1": "de.terrain at1 (64x64 at 2.5 m around the shallow spot, prefetched, + 1,000 random rays); rows awaited",
    "bake": "de.terrain bake batch 12-18-12 (awaited: s4.bake / DE.Terrain BAKE ready), one at1 16x16 over it (each ray "
            "gives a provider row and a set bake row), bake remove",
    "drop": "de.terrain drop at 10 flat points (1-block large grids), s4.drop rows awaited",
    "walk": "de.terrain walk 50 m from the shallow spot, s4.walk rows awaited",
    "n1": "at 3 flat points: de.terrain walk 1 m (puts the character on the seabed), 3 s, de.terrain n1 (feet vs the "
          "storage); S4both: de.s4 probe under the feet",
    "perf": "de.terrain perf: a 100 m/s fly-over 1,200 m long; SE private bytes before/after; status readRange",
    "at0b": "de.terrain at0b (the in-SE A-T0b fallback: plane at known heights, LOD 0..2)",
    "at2": "SE at1 grids at the shallow (9x9) and deep (5x5) spots, then Subnautica de.s4 probe at the SE columns "
           "(yHint = SE's hit, hold, mesh); results polled",
    "wm6": "the flattest spot near (0, -18, 0): SE at1 size 1 + de.terrain drop there (SE closes the dropped grid when "
           "it reports), S3's static block placed at SE's rest pose (de.s3 place) and mirrored, Subnautica de.s3 "
           "placement + de.s4 probe at the column",
    "capture": "SE camera 3 m from the WM-6 cube (south and east), 90 frames, Subnautica de.capture",
    "n2": "COPY: hash, then per path de.terrain edit (cutout, shipdrill, explosion, voxelhand, writerange) and crater "
          "(meteor) at separate seabed points; s4.edit rows awaited; hash again",
    "g2e2save": "COPY: the owner prepares a resting grid and a gear-locked ship in the footprint and saves (--owner); "
                "the world folder is scanned read-only (no tile in the save)",
    "g2e3b": "COPY: scan before SE starts, reload, tiles, >= 10 s, the owner saves (--owner), scan again (grid moved "
             "< 1 cm, gears still locked to tile ids)",
    "g2e2noplugin": "COPY, OWNER ONLY: load the copy with a Pulsar profile without DeepEngineers.SE (the launcher "
                    "cannot); judge its SpaceEngineers_*.log with --analyze DIR --no-plugin-log FILE",
    "g2e5": "COPY (made with make_terrain_copy.py --without-world-mod): load it, record what SE does (runs alone)",
    "m4": "COPY: g2e2save with S4.M4 (tiles saved: S4.G2-E2a must turn red; delete the copy afterwards)",
    "m10": "COPY: g2e3b with S4.M10 (random tile ids: S4.G2-E3b must turn red)",
}

def s4_analyzer():
    for p in (S4_DIR, S2_DIR):
        if p not in sys.path:
            sys.path.insert(0, p)
    import analyze_s4  # noqa: E402
    return analyze_s4

def s4_oracle():
    """worldmap_ref.OctreeColumns over the user's octrees (read-only), or None."""
    p = os.path.join(TOOLS, "worldmap")
    if p not in sys.path:
        sys.path.insert(0, p)
    try:
        import worldmap_ref  # noqa: E402
        return worldmap_ref.OctreeColumns(worldmap_ref.default_octree_dir(SUBNAUTICA))
    except (OSError, ImportError):
        return None

def s4_octree_dir():
    return os.path.join(SUBNAUTICA, "Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "Build18",
                        "CompiledOctreesCache")

def s4_measure_source() -> dict:
    """The CONTRACT 3.1 source manifest measured by the launcher (directory listing and index.txt only, read-only)."""
    d = s4_octree_dir()
    out = {"path": d, "build": None, "files": 0, "bytes": 0, "rows": None, "manifestHash": None, "index": None,
           "other": [], "error": None}
    acf = os.path.normpath(os.path.join(SUBNAUTICA, "..", "..", "appmanifest_264710.acf"))
    try:
        with open(acf, encoding="utf-8", errors="replace") as f:
            m = re.search(r'"buildid"\s+"(\d+)"', f.read())
        out["build"] = int(m.group(1)) if m else None
    except OSError as e:
        out["error"] = "appmanifest: %s" % e
    try:
        with open(os.path.join(os.path.dirname(d), "index.txt"), encoding="utf-8", errors="replace") as f:
            out["index"] = [ln.strip() for ln in f.read().splitlines()[1:5]]
    except OSError as e:
        out["error"] = "index.txt: %s" % e
    try:
        names = sorted(os.listdir(d))
    except OSError as e:
        out["error"] = "folder: %s" % e
        return out
    rx = re.compile(r"compiled-batch-(\d+)-(\d+)-(\d+)\.optoctrees$")
    h, rows, lines = 0xcbf29ce484222325, [], []
    for n in sorted(names, key=lambda s: s.encode("utf-8")):
        m = rx.match(n)
        if not m:
            out["other"].append(n)
            continue
        size = os.path.getsize(os.path.join(d, n))
        out["files"] += 1
        out["bytes"] += size
        rows.append(int(m.group(2)))
        lines.append("%s\t%d\n" % (n, size))
    for b in "".join(lines).encode("utf-8"):
        h = ((h ^ b) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    out["manifestHash"] = "0x%016x" % h
    out["rows"] = [min(rows), max(rows)] if rows else None
    return out

def s4_world_info(name):
    """(folder, steam_dir, lastSessionName, autosaveMinutes, worldMod) of a world name under %APPDATA%/SpaceEngineers/
 Saves/<steamid>/ (read-only)."""
    hits = glob.glob(os.path.join(SE_APPDATA, "Saves", "*", name))
    hits = [h for h in hits if os.path.isdir(h)]
    if len(hits) != 1:
        return {"folder": None, "error": "%d worlds named %r under %s" % (len(hits), name, os.path.join(SE_APPDATA,
                                                                                                          "Saves"))}
    folder = hits[0]
    info = {"folder": folder, "steamDir": os.path.dirname(folder), "error": None}
    try:
        with open(os.path.join(os.path.dirname(folder), "LastSession.sbl"), encoding="utf-8-sig", errors="replace") as f:
            m = re.search(r"<Path>([^<]*)</Path>", f.read())
        info["lastSession"] = os.path.basename(m.group(1).rstrip("\\/")) if m else None
    except OSError:
        info["lastSession"] = None
    try:
        with open(os.path.join(folder, "Sandbox_config.sbc"), encoding="utf-8-sig", errors="replace") as f:
            txt = f.read()
        m = re.search(r"<AutoSaveInMinutes>(\d+)</AutoSaveInMinutes>", txt)
        info["autosaveMinutes"] = int(m.group(1)) if m else None
        info["worldMod"] = "<Name>DeepEngineers.World</Name>" in txt
    except OSError:
        info["autosaveMinutes"], info["worldMod"] = None, None
    return info

def process_memory(pid):
    """SE's private bytes and working set (GetProcessMemoryInfo; read-only), or None."""
    try:
        import ctypes
        from ctypes import wintypes

        class PMC(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                        ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                        ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
                        ("PrivateUsage", ctypes.c_size_t)]
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.K32GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(PMC), wintypes.DWORD]
        k32.CloseHandle.argtypes = [wintypes.HANDLE]
        h = k32.OpenProcess(0x1000 | 0x0010, False, int(pid)) or k32.OpenProcess(0x1000, False, int(pid))
        if not h:
            return None
        try:
            pmc = PMC()
            pmc.cb = ctypes.sizeof(PMC)
            if not k32.K32GetProcessMemoryInfo(h, ctypes.byref(pmc), pmc.cb):
                return None
            return {"pid": int(pid), "privateBytes": int(pmc.PrivateUsage), "workingSet": int(pmc.WorkingSetSize),
                    "peakWorkingSet": int(pmc.PeakWorkingSetSize)}
        finally:
            k32.CloseHandle(h)
    except (OSError, AttributeError):
        return None

def s4_ref(oracle, x, z, fallback):
    """The reference seabed (first entry crossing below +20 m) at SN (x, z), or the fallback."""
    if oracle is None:
        return fallback
    y = oracle.first_entry(x, z, 20.0, -600.0)
    return y if y is not None else fallback

def s4_flat_spot(oracle, centre, radius=10.0, step=1.0):
    """The footprint nearest `centre` (within `radius`, on a `step` grid) whose 9 reference columns over 3 m differ by at
 most S4_FLAT_RELIEF and whose centre column has exactly one entry crossing (no overhang); without such a point the
 flattest one. (spot, info) or (centre, None) without the octrees."""
    if oracle is None:
        return tuple(centre), None
    best = None
    n = int(radius / step)
    for i in range(-n, n + 1):
        for j in range(-n, n + 1):
            x, z = centre[0] + i * step, centre[1] + j * step
            hs = [oracle.first_entry(x + dx, z + dz, 20.0, -600.0) for dx in (-1.5, 0.0, 1.5) for dz in (-1.5, 0.0, 1.5)]
            if any(h is None for h in hs):
                continue
            if sum(1 for _y, k in oracle.crossings(x, z, 20.0, -600.0) if k > 0) != 1:
                continue
            rel = max(hs) - min(hs)
            flat = rel <= S4_FLAT_RELIEF
            key = (0 if flat else 1, math.hypot(i, j) if flat else rel, math.hypot(i, j))
            if best is None or key < best[0]:
                best = (key, (x, z), hs[4], rel)
    if best is None:
        return tuple(centre), None
    return best[1], {"seabed": round(best[2], 4), "relief": round(best[3], 4)}

def s4_flat_points(oracle, centre, n, spread=30.0):
    """n points on a circle of `spread` m around `centre`, each moved to the flattest footprint within 3 m."""
    pts = []
    for k in range(n):
        ang = 2.0 * math.pi * k / max(1, n)
        c = (centre[0] + spread * math.cos(ang), centre[1] + spread * math.sin(ang))
        p, _info = s4_flat_spot(oracle, c, radius=3.0, step=1.0)
        pts.append((round(p[0], 2), round(p[1], 2)))
    return pts

def s4_edit_points(oracle):
    """N2: one seabed point per edit path, 20 m apart along SN z = 30 (blocked edits change nothing; separate points
 keep a guard-off mutant's edit from touching the next path's box)."""
    pts = {}
    for k, path in enumerate(("cutout", "shipdrill", "explosion", "voxelhand", "writerange", "meteor")):
        x, z = -50.0 + 20.0 * k, 30.0
        pts[path] = [x, round(s4_ref(oracle, x, z, S4_SEABED_FALLBACK["shallow"]), 4), z]
    return pts

def s4_unknown(rep):
    """A refusal that means "this build has no such command/action" (not a failure of a known one)."""
    err = str((rep or {}).get("error", "")).lower()
    return "unknown action" in err or "unknown command" in err or "unknown cmd" in err

def s4_scan(log, sess, a, when):
    """Read-only scan of the throwaway world folder (tests/s4/analyze_s4.py scan_world) into a world_scan event."""
    folder = (getattr(a, "world_info", None) or {}).get("folder")
    if not folder:
        return None
    sc = s4_analyzer().scan_world(folder)
    sess.event("world_scan", when=when, folder=folder, scan=sc, qpc=S.qpc())
    log.say("  S4 scan %s: %d grids, %d gears (%d locked), tile traces: vx2 %s names %d maps %d" % (
        when, len(sc.get("grids") or []), len(sc.get("gears") or []),
        sum(1 for g in sc.get("gears") or [] if g.get("lockMode") == "Locked"), sc.get("tileVx2"),
        len(sc.get("storageNames") or []), len(sc.get("tileVoxelMaps") or [])), ev="s4_scan", when=when)
    return sc

class S4SeTail:
    """Reads the SE plugin's JSONL rows appended since the session start (read-only; the plugin opens the file with
 FileShare.ReadWrite). rows returns every parsed s4.* row seen so far; mark / since(mark) slice them."""

    def __init__(self, t_start, pattern=None):
        self.t_start = t_start
        self.pattern = pattern or os.path.join(LOG_DIR, "se-*.jsonl")
        self.offsets = {}
        self.partial = {}
        self.all = []

    def poll(self):
        for p in sorted(glob.glob(self.pattern)):
            try:
                if os.path.getmtime(p) < self.t_start - 5:
                    continue
                with open(p, "rb") as f:
                    f.seek(self.offsets.get(p, 0))
                    data = f.read()
                    self.offsets[p] = f.tell()
            except OSError:
                continue
            if not data:
                continue
            text = self.partial.pop(p, "") + data.decode("utf-8", errors="replace")
            lines = text.split("\n")
            if not text.endswith("\n"):
                self.partial[p] = lines.pop()
            for ln in lines:
                ln = ln.strip()
                if not ln.startswith("{"):
                    continue
                try:
                    rec = json.loads(ln)
                except ValueError:
                    continue
                ev = rec.get("ev") if isinstance(rec, dict) else None
                if isinstance(ev, str) and ev.startswith("s4."):
                    if isinstance(rec.get("data"), dict):
                        rec = dict(rec["data"], ev=ev, t=rec.get("t"))
                    self.all.append(rec)
                elif ev == "log" and isinstance(rec.get("msg"), str):
                    m = re.match(r"(s4\.[A-Za-z0-9_]+)\s+(\{.*\})\s*$", rec["msg"])
                    if m:
                        try:
                            d = json.loads(m.group(2))
                        except ValueError:
                            d = None
                        if isinstance(d, dict):
                            self.all.append(dict(d, ev=m.group(1), t=rec.get("t")))
                    elif rec["msg"].startswith("DE.Terrain "):
                        # the harness jobs' start/done/aborted lines: the waits end on them
                        self.all.append({"ev": "log", "msg": rec["msg"], "t": rec.get("t")})
        return len(self.all)

    def mark(self):
        self.poll()
        return len(self.all)

    def since(self, mark, name=None):
        return [r for r in self.all[mark:] if name is None or r.get("ev") == name]

    def line_since(self, mark, rx):
        """The first DE.Terrain log line after `mark` matching the compiled regex `rx`, or None."""
        for r in self.all[mark:]:
            if r.get("ev") == "log" and rx.search(r.get("msg", "")):
                return r["msg"]
        return None

class S4Runner:
    """The scripted S4 parts through the C11 harness. SE side: de.terrain {action...} (S4_SE_ACTIONS: the contract's
 action names in one table); Subnautica side: de.s4 (probe/status/cancel), de.s3 placement/place, de.capture.
 Never touches a game window; never writes into a game or save folder (world folders are only scanned)."""

    SE_TERRAIN = "de.terrain"
    last_end, last_line = None, None
    WALK_STALL_S = 10.0          # a walk's rows are done when none came for this long (the selftest lowers it)
    SN_ACTIONS = {
        "probe": ("de.s4", lambda cols, label: dict({"probe": cols, "hold": True, "mesh": True, "id": label},
                                                    **S4_SN_PROBE)),
        "status": ("de.s4", lambda frm, mx: {"status": True, "from": int(frm), "max": int(mx)}),
        "cancel": ("de.s4", lambda: {"cancel": True}),
        "placement": ("de.s3", lambda: {"action": "placement"}),
    }

    def __init__(self, log, a, sess, se, sn, out, stamp, tail, oracle):
        self.log, self.a, self.sess, self.se, self.sn, self.out = log, a, sess, se, sn, out
        self.stamp, self.tail, self.oracle = stamp, tail, oracle
        self.q = a.quick
        self.sec = S.Section.open(a.name)
        self.h = S.Harness(self.sec) if self.sec else None
        self.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
        self.unsupported = set()
        self.cur_part = None
        self.cap_n = 0
        self.wm6 = None
        self.s3_placed = False

    # ------------------------------------------------------------------------------------------------ plumbing
    def alive(self):
        return self.se.poll() is None and (self.sn is None or self.sn.poll() is None)

    def attach(self):
        if self.h is None:
            return False, "section absent"
        ok, why = self.h.attach()
        if not ok:
            return False, why
        for side in ("se",) + (("sn",) if self.sn is not None else ()):
            if self.call(side, "de.ping", {}, label="ping") is None:
                return False, "%s does not answer C11 (dev build needed)" % side.upper()
        return True, ""

    def close(self):
        if self.h:
            self.h.detach()
        if self.sec:
            self.sec.close()

    def wait(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end and self.alive():
            self.h.poll_replies()
            time.sleep(min(0.02, max(0.0, end - time.monotonic())))

    def call(self, side, cmd, args=None, timeout=None, label=None):
        t0 = time.monotonic()
        q0 = S.qpc()
        rid, st = self.h.send(side, cmd, args or {})
        self.stats["sent"] += 1
        rep = self.h.wait_reply(rid, timeout or self.a.cmd_timeout) if st == S.RW_OK else None
        self.sess.event("cmd", side=side, name=cmd, args=args or {}, reply=rep, ms=round((time.monotonic() - t0) * 1000, 1),
                        label=label, part=self.cur_part, qpc=q0, id=rid)
        if rep is None:
            self.stats["noReply"] += 1
        elif rep.get("ok"):
            self.stats["ok"] += 1
        else:
            self.stats["failed"] += 1
            err = str(rep.get("error", ""))
            if "unknown" in err.lower() and cmd not in self.stats["unknown"]:
                self.stats["unknown"].append(cmd)
        return rep

    @staticmethod
    def ok(rep):
        return bool(rep) and bool(rep.get("ok"))

    def terrain(self, action, label=None, timeout=None, **args):
        """One de.terrain action; an action SE refuses as unknown is not sent again (its rows stay MISSING)."""
        if action in self.unsupported:
            return None
        rep = self.call("se", self.SE_TERRAIN, dict({"action": action}, **args), timeout=timeout,
                        label=label or "terrain." + action)
        if rep is not None and not rep.get("ok") and s4_unknown(rep):
            self.unsupported.add(action)
            self.log.say("  S4: SE does not take de.terrain %s (%s): its rows will be MISSING" % (action, rep.get("error")))
        return rep

    def sn_act(self, action, *args, label=None, timeout=None):
        key = "sn." + action
        if key in self.unsupported or self.sn is None:
            return None
        cmd, build = self.SN_ACTIONS[action]
        rep = self.call("sn", cmd, build(*args), timeout=timeout, label=label or key)
        if rep is not None and not rep.get("ok") and s4_unknown(rep):
            self.unsupported.add(key)
            self.log.say("  S4: Subnautica does not take %s (%s): its rows will be MISSING" % (cmd, rep.get("error")))
        return rep

    def part(self, name, phase):
        self.cur_part = name if phase == "begin" else None
        self.sess.event("part_start" if phase == "begin" else "part_end", name=name, qpc=S.qpc())
        self.log.say("  S4 part %s %s" % (name, phase), ev="s4_part", part=name, phase=phase)

    def sample_memory(self, label):
        m = process_memory(self.se.pid)
        self.sess.event("sample", label=label, se=m, qpc=S.qpc())
        return m

    def park(self):
        """Integration: the DE Lab save's character starts ~125 m under the Safe Shallows seabed (MODLOG
 S5a-3, SN y -142.6); with the S4 tiles up it would sit inside the rock, so before any part it is moved to the
 open water column above the shallow spot (de.swim teleportChar: the character, feet, SN metres; de.teleport moves
 only the spectator camera). The walk and n1 parts teleport it again themselves."""
        x, z = self.a.shallow_spot[0] + S4_PARK_DX, self.a.shallow_spot[1]
        rep = self.call("se", "de.swim", {"teleportChar": [x, S4_PARK_Y, z], "yawDeg": 0.0}, label="park")
        if not self.ok(rep):
            self.log.say("  S4 park: de.swim teleportChar not acknowledged (%s); the character stays where the save put "
                         "it" % (json.dumps(rep)[:160] if rep else "no reply"), ev="s4_park_failed")
        self.wait(2.0)

    def status(self, label):
        return self.terrain("status", label=label)

    def ref(self, x, z, fallback):
        return s4_ref(self.oracle, x, z, fallback)

    def wait_rows(self, mark, name, want, timeout, stall=None, label="", until=None):
        """Waits since `mark` until the SE log line `until` (a job's "done"/"aborted" line) or, without it, `want` new rows
 `name`; a stall (no new row for `stall` s) or the timeout also ends it. Returns the rows; self.last_end tells
 why it ended ("line", "count", "stall", "timeout", "exit")."""
        t0 = last_t = time.monotonic()
        n_last, n = -1, 0
        stall = stall or S4_RAY_STALL_S
        next_status = t0 + 15.0
        rx = re.compile(until) if until else None
        self.last_end, self.last_line = "exit", None
        while time.monotonic() - t0 < timeout and self.alive():
            self.wait(1.0)
            self.tail.poll()
            n = len(self.tail.since(mark, name))
            if n != n_last:
                n_last, last_t = n, time.monotonic()
            if rx is not None:
                hit = self.tail.line_since(mark, rx)
                if hit:
                    self.last_end, self.last_line = "line", hit
                    break
            elif want and n >= want:
                self.last_end = "count"
                break
            if n and time.monotonic() - last_t > stall:
                self.last_end = "stall"
                if want or rx is not None:
                    self.log.say("  S4: %s: %d of %s %s rows, none for %.0f s: stop waiting" % (label, n, want or "?",
                                                                                               name, stall))
                break
            if time.monotonic() >= next_status:
                next_status = time.monotonic() + 30.0
                self.log.say("  S4: %s: %d of %s %s rows after %.0f s" % (label, n, want or "?", name,
                                                                         time.monotonic() - t0))
        else:
            if self.alive():
                self.last_end = "timeout"
                self.log.say("  S4: %s: %d of %s %s rows when the wait ended (%.0f s)" % (label, n, want or "?", name,
                                                                                         timeout))
        if rx is not None and self.last_end in ("stall", "timeout") and self.alive():
            # SE runs one harness job at a time: stop the unfinished one so the next part can start
            self.terrain("stop", label=label + ".stop")
        self.sess.event("wait_end", label=label, name=name, rows=n, end=self.last_end, line=self.last_line,
                        seconds=round(time.monotonic() - t0, 1))
        return self.tail.since(mark, name)

    # ------------------------------------------------------------------------------------------------ building blocks
    def at1(self, label, origin, size, spacing, random_n, prefetch=True, part=None, set_hint=None):
        a = S4_AT1_QUICK if self.q else S4_AT1
        args = {"origin": [float(origin[0]), float(origin[1])], "size": int(size), "spacing": float(spacing),
                "fromY": a["fromY"], "toY": a["toY"], "random": int(random_n), "prefetch": bool(prefetch)}
        if set_hint:
            args["set"] = set_hint
        mark = self.tail.mark()
        rep = self.terrain("at1", label=label, timeout=15.0, **args)
        want = int(size) * int(size) + int(random_n)
        rows = []
        if self.ok(rep):
            timeout = max(300.0, min(self.a.at1_timeout, 30.0 + 0.6 * want))
            rows = self.wait_rows(mark, "s4.ray", want, timeout, label=label, until=r"DE\.Terrain AT1 (done|aborted)")
        self.sess.event("rays", part=part or self.cur_part, label=label, args=args, expected=want, got=len(rows),
                        rows=rows, reply=rep)
        self.log.say("  S4 %s: %d s4.ray rows for %d rays (%s)" % (label, len(rows), want, self.last_end if self.ok(rep)
                                                                       else "refused"), ev="s4_rays", label=label,
                     got=len(rows), expected=want)
        return rows

    def sn_probe(self, cols, label):
        """Subnautica's de.s4 probe over [[x, z, yHint],...]; polls de.s4 status until done; returns the results."""
        rep = self.sn_act("probe", cols, label, label="probe." + label, timeout=10.0)
        results, state = [], None
        if self.ok(rep):
            t0 = time.monotonic()
            limit = 30.0 + 9.0 * len(cols)
            fresh = False
            while time.monotonic() - t0 < limit and self.alive():
                if not fresh:
                    self.wait(0.5)
                st = self.sn_act("status", len(results), 64, label="probe.status")
                fresh = False
                if not self.ok(st):
                    continue
                got = st.get("results") or []
                if isinstance(got, list):
                    seen = {q.get("i") for q in results}
                    for r in got:
                        if isinstance(r, dict) and r.get("i") not in seen:
                            results.append(r)
                            seen.add(r.get("i"))
                            fresh = True
                state = st.get("state")
                done = st.get("done") if isinstance(st.get("done"), int) else len(results)
                # a finished batch is read to its end (64 results per status page) before the loop stops
                if state in ("done", "cancelled", "failed") and (len(results) >= done or not fresh):
                    break
            else:
                if self.alive():
                    self.sn_act("cancel", label="probe.cancel")
                    state = "timeout"
        self.sess.event("sn_probe", part=self.cur_part, label=label, request=cols, reply=rep, state=state,
                        results=results)
        self.log.say("  S4 Subnautica probe %s: %d of %d columns (%s)" % (label, len(results), len(cols), state))
        return results

    def drop(self, points, label):
        mark = self.tail.mark()
        rep = self.terrain("drop", label=label, timeout=15.0, points=[[float(x), float(z)] for x, z in points])
        rows = self.wait_rows(mark, "s4.drop", len(points), 90.0 + 15.0 * len(points), stall=60.0, label=label,
                              until=r"DE\.Terrain DROP (done|aborted)") if self.ok(rep) else []
        self.sess.event("drops", part=self.cur_part, label=label, points=[list(p) for p in points], reply=rep, rows=rows)
        return rows

    def walk(self, frm, heading, length, label):
        mark = self.tail.mark()
        rep = self.terrain("walk", label=label, timeout=15.0, **{"from": [float(frm[0]), float(frm[1])],
                                                                  "headingDeg": float(heading), "length": float(length)})
        rows = self.wait_rows(mark, "s4.walk", 0, 120.0 + 2.0 * float(length), stall=max(30.0, self.WALK_STALL_S),
                              label=label, until=r"DE\.Terrain WALK (done|aborted)") if self.ok(rep) else []
        self.sess.event("walks", part=self.cur_part, label=label, reply=rep, rows=rows)
        return rows

    def flat_spot(self, centre, radius=10.0, step=1.0):
        return s4_flat_spot(self.oracle, centre, radius, step)

    def flat_points(self, centre, n, spread=30.0):
        return s4_flat_points(self.oracle, centre, n, spread)

    def settle(self, frames=S4_SETTLE_FRAMES, timeout=20.0):
        if not self.sec:
            return False
        t0 = time.monotonic()
        st, _q, d1 = S.seqlock_read(self.sec, L.REGION["C1"].offset, L.SE_FRAME.size)
        tick = L.SE_FRAME.unpack(d1)["tick"] if d1 else 0
        f0 = None
        while time.monotonic() - t0 < timeout and self.alive():
            st, _q, d7 = S.seqlock_read(self.sec, L.REGION["C7"].offset, L.SN_STATUS.size)
            c7 = L.SN_STATUS.unpack(d7) if d7 else {}
            if c7.get("presentedSeTick", 0) >= tick:
                f0 = c7.get("snFrame", 0) if f0 is None else f0
                if c7.get("snFrame", 0) - f0 >= frames and time.monotonic() - t0 >= 1.5:
                    return True
            self.wait(0.05)
        return False

    def capture(self, name, pose):
        """SE camera at pose (SN x, y, z, yawDeg, look dir), 90 Subnautica frames, Subnautica's de.capture (the S1
 capture service; SE's C10 relay as the fallback); waits for the PNG."""
        self.cap_n += 1
        x, y, z, yaw, look = pose
        self.call("se", "de.teleport", {"x": x, "y": y, "z": z, "yawDeg": yaw, "label": "s4." + name}, label="teleport")
        self.call("se", "de.look", {"dir": list(look), "label": "s4." + name}, label="look")
        self.settle()
        fname = "%s-%02d.png" % (name, self.cap_n)
        rel = "s4/%s/%s" % (self.stamp, fname)
        args = {"path": rel, "checkId": "S4.A-T2", "label": "s4." + name, "kind": 1}
        rep = self.call("sn", "de.capture", args, timeout=10.0, label="capture")
        via = "sn"
        if not self.ok(rep):
            rep = self.call("se", "de.capture", args, timeout=10.0, label="capture")
            via = "se"
        full = os.path.join(CAPTURES, *rel.split("/"))
        t0, last, got = time.monotonic(), -1, False
        while time.monotonic() - t0 < 10.0 and self.alive():
            try:
                n = os.path.getsize(full)
            except OSError:
                n = -1
            if n > 0 and n == last:
                got = True
                break
            last = n
            self.wait(0.1)
        self.sess.event("capture", name=name, pose=list(pose[:4]) + [list(look)], png="captures/" + fname, source=full,
                        written=got, via=via, reply=rep, qpc=S.qpc())
        return got

    def scan(self, when):
        return s4_scan(self.log, self.sess, self.a, when)

    def owner_save(self, step, prompt):
        """A save of the throwaway world: de.terrain save when SE has one, else the project's in-game save;
 waits for the world folder to change and settle, then scans it."""
        folder = self.a.world_info.get("folder")
        sbs = os.path.join(folder, "SANDBOX_0_0_0_.sbs")
        t_before = os.path.getmtime(sbs) if os.path.exists(sbs) else 0.0
        rep = self.terrain("save", label="save." + step)
        if not self.ok(rep):
            if not self.a.owner:
                self.sess.event("owner", step=step, prompt=prompt, done=False, reason="--owner not given")
                self.log.say("  S4 OWNER STEP SKIPPED (%s): %s  -> pass --owner to wait for it; rows MISSING" % (
                    step, prompt), ev="s4_owner_skipped")
                return None
            self.log.say("\n  OWNER (%s): %s\n  The launcher waits up to %.0f s for the save of %s." % (
                step, prompt, S4_SAVE_WAIT_S, os.path.basename(folder)), ev="s4_owner", step=step)
        t0 = time.monotonic()
        done = False
        while time.monotonic() - t0 < S4_SAVE_WAIT_S and self.alive():
            self.wait(1.0)
            try:
                t = os.path.getmtime(sbs)
            except OSError:
                continue
            if t > t_before:
                # wait until the files stop changing (SE writes the sector, then the.sbc files)
                stable = 0
                last = t
                while stable < 3 and self.alive():
                    self.wait(1.0)
                    t2 = max(os.path.getmtime(os.path.join(folder, n)) for n in os.listdir(folder)
                             if os.path.isfile(os.path.join(folder, n)))
                    stable = stable + 1 if t2 == last else 0
                    last = t2
                done = True
                break
        self.sess.event("owner", step=step, prompt=prompt, done=done, via="de.terrain save" if self.ok(rep) else "owner",
                        seconds=round(time.monotonic() - t0, 1))
        if not done:
            self.log.say("  S4 %s: no save seen within %.0f s" % (step, S4_SAVE_WAIT_S), ev="s4_no_save")
            return None
        return self.scan("after-save")

    # ------------------------------------------------------------------------------------------------ parts
    def p_gate(self):
        """de.terrain status + gate (the launcher's own manifest measure is taken at the session start: S4.SOURCE)"""
        self.status("gate.status")
        self.terrain("gate", label="gate.fresh", timeout=30.0)
        self.sample_memory("gate")

    def p_tiles(self):
        """de.terrain tiles"""
        self.terrain("tiles", label="tiles")

    def p_n3(self):
        """de.terrain n3 (in-game provider self-check)"""
        mark = self.tail.mark()
        rep = self.terrain("n3", label="n3", timeout=120.0)
        if self.ok(rep) and rep.get("pending"):
            self.wait_rows(mark, "s4.n3", 1, 180.0, label="n3")

    def p_at0b(self):
        """de.terrain at0b (in-SE A-T0b: plane heights at LOD 0..2)"""
        mark = self.tail.mark()
        rep = self.terrain("at0b", label="at0b", timeout=120.0, lods=[0, 1, 2], heights=[-17.3, -17.0, -16.75, -235.5])
        if self.ok(rep) and rep.get("pending"):
            self.wait_rows(mark, "s4.at0b", 1, 180.0, label="at0b")

    def p_hash(self):
        """de.terrain hash at the Safe Shallows anchor (baseline)"""
        self.terrain("hash", label="hash", timeout=30.0, **S4_HASH)

    def p_at1(self):
        """A-T1(1): the 64x64 grid (2.5 m) at the shallow spot + 1,000 random rays, prefetched"""
        a = S4_AT1_QUICK if self.q else S4_AT1
        self.at1("at1", self.a.shallow_spot, a["size"], a["spacing"], a["random"],
                 prefetch="S4.M7" not in self.a.mutants_list)
        self.status("at1.status")
        self.sample_memory("at1")

    def p_bake(self):
        """bake map of batch 12-18-12 (s4.bake row = ready), one at1 over it (every ray also gives a set "bake" row), bake removed"""
        g = S4_BAKE_GRID
        mark = self.tail.mark()
        rep = self.terrain("bake", label="bake.on", timeout=30.0, batch=list(S4_BAKE_BATCH))
        if not self.ok(rep):
            return
        ready = self.wait_rows(mark, "s4.bake", 1, 300.0, stall=300.0, label="bake.ready",
                               until=r"DE\.Terrain BAKE (ready|failed)")
        if ready:
            self.at1("bake", g["origin"], g["size"], g["spacing"], 0)
        self.terrain("bake", label="bake.remove", timeout=30.0, remove=True)

    def p_drop(self):
        """A-T1(2): 1-block large grids dropped at 10 flat points around the shallow spot"""
        pts = self.flat_points(self.a.shallow_spot, 10, 30.0)
        self.drop(pts, "drop")

    def p_walk(self):
        """A-T1(2): a scripted 50 m character walk from the shallow spot"""
        w = S4_WALK
        self.walk(w["from"], w["headingDeg"], w["length"], "walk")

    def p_n1(self):
        """N1: at 3 flat points the character is put on the seabed (de.terrain walk 1 m), stands 3 s, then de.terrain n1 (feet vs storage); S4both: Subnautica's hit under the feet"""
        pts = self.flat_points(self.a.shallow_spot, 3, 12.0)
        walks, stands, probes = [], [], []
        for x, z in pts:
            if not self.alive():
                break
            rows = self.walk((x, z), 0.0, 1.0, "n1")
            for r in rows:
                r.setdefault("label", "n1")
            walks += rows
            self.wait(3.0)
            mark = self.tail.mark()
            rep = self.terrain("n1", label="n1.stand")
            self.tail.poll()
            row = (self.tail.since(mark, "s4.n1") or [None])[-1]
            stands.append({"point": [x, z], "reply": rep, "row": row})
        if self.sn is not None:
            cols = []
            for st in stands:
                f = (st.get("row") or {}).get("feetSn") or (st.get("reply") or {}).get("feetSn")
                if isinstance(f, list) and len(f) >= 3:
                    cols.append([f[0], f[2], f[1]])
            if cols:
                probes = self.sn_probe(cols, "n1")
        self.sess.event("n1", points=[list(p) for p in pts], walks=walks, stands=stands, probes=probes)

    def p_perf(self):
        """A-T1(3): a 100 m/s fly-over, SE private bytes before/after, status readRange"""
        self.sample_memory("perf.before")
        p = S4_PERF
        mark = self.tail.mark()
        rep = self.terrain("perf", label="perf", timeout=15.0, flyover=dict(p))
        rows = []
        if self.ok(rep):
            secs = math.dist(p["from"], p["to"]) / p["speed"]
            rows = self.wait_rows(mark, "s4.perf", 1, secs + 90.0, stall=60.0, label="perf",
                                  until=r"DE\.Terrain PERF (done|aborted)")
        self.sess.event("perf", part="perf", reply=rep, rows=rows)
        self.status("perf.status")
        self.sample_memory("perf.after")

    def p_at2(self):
        """A-T2: SE rays at the shallow (9x9) and deep (5x5) spots, Subnautica de.s4 probe at the same columns"""
        g = S4_AT2
        for spot, size, name in ((self.a.shallow_spot, g["shallowSize"], "shallow"), (S4_DEEP, g["deepSize"], "deep")):
            if not self.alive():
                return
            rows = self.at1("at2." + name, spot, size, g["spacing"], 0)
            cols = [[r.get("snX"), r.get("snZ"), r.get("castSnY")] for r in rows
                    if isinstance(r.get("snX"), (int, float)) and isinstance(r.get("castSnY"), (int, float))]
            if cols:
                self.sn_probe(cols, "at2." + name)

    def p_wm6(self):
        """WM-6: the flattest spot near SN (0, -18, 0): SE ray + drop there, the mirrored cube, Subnautica's hit"""
        spot, info = self.flat_spot(self.a.shallow_spot, 10.0, 1.0)
        spot = (round(spot[0], 2), round(spot[1], 2))
        ray = self.at1("wm6.ray", spot, 1, 2.5, 0)
        drop = self.drop([spot], "wm6")
        d = drop[-1] if drop else {}
        rs = d.get("restSn")
        if isinstance(rs, list) and len(rs) >= 3 and all(isinstance(v, (int, float)) for v in rs[:3]):
            spot = (round(float(rs[0]), 3), round(float(rs[2]), 3))      # where the cube came to rest
        rest = None
        for k in ("restSnY", "bottomSnY", "restBottomSnY", "snY"):
            if isinstance(d.get(k), (int, float)):
                rest = float(d[k])
                break
        # SE's drop job closes its grids when it reports (S4Harness DropJob): the mirrored cube is S3's static block
 # placed at SE's rest pose (centre = the bottom-face centre + 1.25 m), so the mirror chain is checked at it
        placed = self.sn_act("placement", label="placement")
        via = "drop"
        if (not self.ok(placed) or not placed.get("entities") or d.get("entityId")) and rest is not None:
            # the dropped grid is not mirrored: place S3's static block at SE's rest pose (centre = bottom + 1.25 m)
            rep = self.call("se", "de.s3", {"place": {"sn": [spot[0], rest + 1.25, spot[1]], "yawDeg": 0.0,
                                                      "label": "s4.wm6"}}, timeout=10.0, label="wm6.s3place")
            self.s3_placed = self.ok(rep)
            via = "s3place"
            self.wait(5.0)
            placed = self.sn_act("placement", label="placement.s3")
        hint = rest if rest is not None else self.ref(spot[0], spot[1], S4_SEABED_FALLBACK["shallow"])
        probe = self.sn_probe([[spot[0], spot[1], hint]], "wm6")
        self.wm6 = {"spot": list(spot), "rest": rest}
        self.sess.event("wm6", spot=list(spot), flat=info, seRay=ray[-1] if ray else None, drop=d,
                        probe=probe[-1] if probe else None, placement=placed, mirrorVia=via)

    def p_capture(self):
        """captures of the WM-6 cube from 3 m (south, east), looked at by the project"""
        if self.wm6 is None:
            self.p_wm6()
        spot, rest = self.wm6["spot"], self.wm6.get("rest")
        y = (rest if rest is not None else S4_SEABED_FALLBACK["shallow"]) + 1.25
        self.capture("wm6-south", (spot[0], y + 0.5, spot[1] - 3.0 - 1.25, 0.0, (0.0, -0.15, 1.0)))
        self.capture("wm6-east", (spot[0] + 3.0 + 1.25, y + 0.5, spot[1], -90.0, (-1.0, -0.15, 0.0)))

    def edit_points(self):
        return s4_edit_points(self.oracle)

    def p_n2(self):
        """N2 (copy): every edit path on a tile (de.terrain edit / crater), content hash and EditBlocked per path"""
        pts = self.edit_points()
        self.terrain("hash", label="n2.hash.before", timeout=30.0, **S4_HASH)
        for path, p in pts.items():
            if not self.alive():
                return
            # SE keeps one pending edit: its final witness (s4.edit final: true, "DE.Terrain edit <path> final") comes
 # EditSettleMs (2 s) later and must be in before the next path's edit replaces it
            mark = self.tail.mark()
            if path == "meteor":
                rep = self.terrain("crater", label="n2.meteor", timeout=30.0, sn=p, radius=S4_EDIT_RADIUS["meteor"])
            else:
                args = {"path": path, "sn": p, "radius": S4_EDIT_RADIUS[path]}
                if path == "shipdrill" and self.a.drill_id:
                    args["drill"] = self.a.drill_id
                rep = self.terrain("edit", label="n2." + path, timeout=30.0, **args)
            first, final = None, None
            if self.ok(rep):
                rows = [r for r in self.wait_rows(mark, "s4.edit", 0, 30.0, stall=30.0, label="n2." + path,
                                                  until=r"DE\.Terrain edit %s final" % path) if r.get("path") in (None, path)]
                first = next((r for r in rows if not r.get("final")), None)
                final = next((r for r in reversed(rows) if r.get("final")), None)
            self.sess.event("edit", path=path, sn=p, reply=rep, row=final or first, first=first, final=final)
        self.terrain("hash", label="n2.hash.after", timeout=30.0, **S4_HASH)

    def p_g2e2save(self):
        """G2-E2 (copy): the project prepares a resting grid + a gear-locked ship in the footprint and saves; scan"""
        self.terrain("tiles", label="g2e2save.tiles")
        self.owner_save("g2e2save", "in SE: put a dynamic grid at rest on the seabed in the footprint (e.g. near SN 0, "
                                    "-17, 0) and a small ship with its landing gear LOCKED to the seabed; wait 10 s; then "
                                    "Esc -> Save")

    def p_m4(self):
        """S4.M4 (copy): g2e2save with tiles saved (S4.G2-E2a must turn red)"""
        self.p_g2e2save()

    def p_g2e3b(self):
        """G2-E2b/E3b (copy): reloaded (scanned before SE started), tiles, >= 10 s, the project saves; scan"""
        self.terrain("tiles", label="g2e3b.tiles")
        self.wait(S4_E2B_WAIT_S)
        self.owner_save("g2e3b", "in SE: touch nothing; Esc -> Save now (the launcher compares the grids and gears "
                                 "with the save it scanned before the load)")

    def p_m10(self):
        """S4.M10 (copy): g2e3b with random tile ids (S4.G2-E3b must turn red)"""
        self.p_g2e3b()

    def p_g2e5(self):
        """G2-E5 (copy without the world mod): what SE does; recorded by the session itself"""
        st = self.status("g2e5.status")
        self.sess.event("g2e5", inWorld=True, status=st)

    def p_g2e2noplugin(self):
        """project only (no SE action here)"""
        self.log.say("  S4 g2e2noplugin: owner step, see the dry-run text")

def s4_plan_lines(a, parts, oracle) -> list:
    out = ["  mode %s; SE: %s%s" % (a.mode, "Legacy.exe -continue (%s)" % (a.world or "DE Lab, the LastSession"),
                                    "; Subnautica: Subnautica.exe -monitor 1 (bridge token)" if a.mode == "S4both" else
                                    "; Subnautica: not started (the passive stand-in host keeps the section for C11)")]
    src = s4_measure_source()
    pins = {"build": 20241558, "files": 5416, "bytes": 1203085204, "rows": [7, 19],
            "manifestHash": "0x2cc277ce91763285"}
    diff = ["%s %s (pin %s)" % (k, src.get(k), v) for k, v in pins.items() if src.get(k) != v]
    out.append("  source manifest now (read-only listing of %s): build %s, %s files, %s bytes, rows %s, hash %s, "
               "index %s%s -> %s" % (src["path"], src["build"], src["files"], src["bytes"], src["rows"],
                                      src["manifestHash"], src["index"], ", other files %s" % src["other"][:3] if
                                      src["other"] else "", "EQUAL to the pins" if not diff else "DIFFERS: " +
                                      "; ".join(diff)))
    if oracle is not None:
        sh = oracle.first_entry(a.shallow_spot[0], a.shallow_spot[1], 20.0, -600.0)
        dp = oracle.first_entry(S4_DEEP[0], S4_DEEP[1], 20.0, -600.0)
        out.append("  reference (worldmap_ref.OctreeColumns, offset 0.5): shallow (%g, %g) seabed %s; deep (%g, %g) %s "
                   "(S3 layer-30: -17.212 / -235.138)" % (a.shallow_spot[0], a.shallow_spot[1], "%.3f" % sh if sh is not
                                                         None else "-", S4_DEEP[0], S4_DEEP[1], "%.3f" % dp if dp is not
                                                         None else "-"))
    else:
        out.append("  reference: octrees not readable here; plan points fall back to the constants")
    at = S4_AT1_QUICK if a.quick else S4_AT1
    if oracle is not None and any(p in parts for p in ("drop", "n1", "wm6", "capture", "n2")):
        if "drop" in parts:
            out.append("  drop points (flat, reference): %s" % s4_flat_points(oracle, a.shallow_spot, 10, 30.0))
        if "n1" in parts:
            out.append("  n1 points (flat, reference): %s" % s4_flat_points(oracle, a.shallow_spot, 3, 12.0))
        if "wm6" in parts or "capture" in parts:
            spot, info = s4_flat_spot(oracle, a.shallow_spot, 10.0, 1.0)
            out.append("  wm6 spot (the flattest 3 m footprint within 10 m of the shallow spot): %s %s" % (list(spot), info))
        if "n2" in parts:
            out.append("  n2 edit points: %s" % json.dumps(s4_edit_points(oracle)))
    out.append("  start: the character parked in open water at SN (%g, %g, %g) (de.swim teleportChar; the DE Lab save "
               "puts it ~125 m under the Safe Shallows seabed; 3 cells east of the spot: review finding 7)" % (a.shallow_spot[0] + S4_PARK_DX, S4_PARK_Y, a.shallow_spot[1]))
    for p in parts:
        doc = S4_PARTS_HELP.get(p, "")
        out.append("  part %-12s %s" % (p, doc))
        if p == "at1":
            out.append("               de.terrain %s" % json.dumps({"action": "at1", "origin": list(a.shallow_spot),
                                                                   "size": at["size"], "spacing": at["spacing"],
                                                                   "fromY": at["fromY"], "toY": at["toY"],
                                                                   "random": at["random"],
                                                                   "prefetch": "S4.M7" not in a.mutants_list}))
        elif p == "bake":
            out.append("               de.terrain {action: bake, batch: %s}; at1 origin %s size %d random 0; "
                       "{action: bake, remove: true}" % (list(S4_BAKE_BATCH), list(S4_BAKE_GRID["origin"]),
                                                         S4_BAKE_GRID["size"]))
        elif p == "perf":
            out.append("               de.terrain %s" % json.dumps({"action": "perf", "flyover": S4_PERF}))
        elif p == "walk":
            out.append("               de.terrain %s" % json.dumps({"action": "walk", "from": list(S4_WALK["from"]),
                                                                   "headingDeg": S4_WALK["headingDeg"],
                                                                   "length": S4_WALK["length"]}))
        elif p == "at2":
            out.append("               SN de.s4 %s" % json.dumps(S4Runner.SN_ACTIONS["probe"][1]([["x", "z", "ySE"]],
                                                                                           "at2.shallow")))
        elif p == "hash":
            out.append("               de.terrain %s" % json.dumps(dict({"action": "hash"}, **S4_HASH)))
        elif p == "n2":
            out.append("               de.terrain {action: edit, path, sn, radius 4 (+ drill)} per path; {action: "
                       "crater, sn, radius 8} for meteor; s4.edit rows awaited")
    if any(p in S4_PARTS_COPY for p in parts):
        if not a.world:
            out.append("  COPY PARTS NEED --world <throwaway copy> (refused in a real run): make it with python -I "
                       "tools/lab/make_terrain_copy.py --name \"DE Lab S4\" --point-last-session (SE closed)")
        else:
            wi = s4_world_info(a.world)
            out.append("  world copy %r: %s; LastSession -> %s; AutoSaveInMinutes %s; world mod %s" % (
                a.world, wi.get("folder") or wi.get("error"), wi.get("lastSession"), wi.get("autosaveMinutes"),
                wi.get("worldMod")))
            for why in s4_copy_problems(a, parts, wi):
                out.append("    REFUSE in a real run: %s" % why)
        if not a.owner and any(p in ("g2e2save", "g2e3b", "m4", "m10") for p in parts):
            out.append("  owner saves: pass --owner (the launcher prints the step and waits for the save); without it "
                       "the save rows stay MISSING unless SE takes de.terrain save")
    if "g2e2noplugin" in parts:
        out.append("  OWNER g2e2noplugin: close SE; in Pulsar's launcher pick a profile WITHOUT DeepEngineers.SE; load "
                   "the copy; wait 30 s in world (a grid in the footprint sinks: expected); quit; then: launcher.py s4 "
                   "--analyze <this session's out> --no-plugin-log <that SpaceEngineers_*.log>")
    if a.mutants_list:
        out.append("  mutants %s (DEEPENGINEERS_MUTANTS, SE only)" % ",".join(a.mutants_list))
    if "g2e5" in parts and len(parts) > 1:
        out.append("  REFUSE in a real run: g2e5 runs alone (on a copy made with --without-world-mod)")
    if s4_mutant_part_mix(parts):
        out.append("  REFUSE in a real run: %s" % s4_mutant_part_mix(parts))
    out.append("  SE de.terrain actions used: " + ", ".join(sorted({"status", "gate", "tiles", "n3", "hash", "at1",
                                                                   "bake", "drop", "walk", "perf", "edit", "crater",
                                                                   "at0b", "save (optional)"})))
    out.append("  Subnautica actions used: " + ", ".join("%s=%s" % (k, v[0]) for k, v in S4Runner.SN_ACTIONS.items()) +
               ", de.capture")
    return out

def s4_mutant_part_mix(parts):
    """m4 / m10 carry their mutants (S4.M4 saves the tiles, S4.M10 randomises the ids): only gate and tiles may share
 their launch, or the other parts' rows would turn red for the mutant's sake."""
    for p in ("m4", "m10"):
        if p in parts:
            other = [q for q in parts if q not in ("gate", "tiles", p)]
            if other:
                return "%s (mutant %s) runs only with gate,tiles (not %s)" % (p, S4_PART_MUTANT[p], ",".join(other))
    return None

def s4_copy_problems(a, parts, wi) -> list:
    out = []
    if not wi.get("folder"):
        out.append(wi.get("error") or "world not found")
        return out
    if a.world.strip().lower() == "de lab":
        out.append("--world is the lab world itself; throwaway parts run only on a copy")
    if wi.get("lastSession") != os.path.basename(wi["folder"]):
        out.append("LastSession.sbl points at %r, not the copy (make_terrain_copy.py --point-last-session)" %
                   wi.get("lastSession"))
    if wi.get("autosaveMinutes") not in (0,):
        out.append("autosave is %s min in the copy (must be 0: make_terrain_copy.py sets it)" % wi.get("autosaveMinutes"))
    if "g2e5" in parts and wi.get("worldMod"):
        out.append("g2e5 needs the copy without the world mod (make_terrain_copy.py --without-world-mod)")
    if "g2e5" not in parts and wi.get("worldMod") is False:
        out.append("this copy has no world mod: only g2e5 runs on it")
    return out

def s4_analyze(log: Log, a, out: str) -> int:
    AS = s4_analyzer()
    sn_logs = list(getattr(a, "sn_log", None) or [])
    try:
        rows = AS.evaluate(out, sn_logs, getattr(a, "octrees", None), None, getattr(a, "baseline", None),
                           getattr(a, "no_plugin_log", None))
    except Exception as e:     # an analyser bug must not hide the session
        import traceback
        rows = []
        AS.C.row(rows, "S4", "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120] + " | " +
                 traceback.format_exc().strip().splitlines()[-1][:120], "all")
    strict = not getattr(a, "lenient", False)
    ok = AS.write_results(out, rows, strict, {"tool": "launcher s4", "snLogs": sn_logs})
    print("\nS4 ORACLE TABLE (%s)  (* = not required; INFO/OTHER = information or the owner's)" % out)
    print(AS.C.table(rows))
    log.say("S4 ORACLE %s: %d rows, %d FAIL, %d MISSING -> %s" % (
        "PASS" if ok else "FAIL", len(rows), sum(1 for r in rows if r["status"] == "FAIL" and r.get("required", True)),
        sum(1 for r in rows if r["status"] == "MISSING" and r.get("required", True)),
        os.path.join(out, "s4-results.json")), ev="s4_result", ok=ok)
    return 0 if ok else 1

def s4_collect_extra(log: Log, out: str, stamp: str, t_start: float):
    cap = os.path.join(CAPTURES, "s4", stamp)
    if os.path.isdir(cap):
        shutil.copytree(cap, os.path.join(out, "captures"), dirs_exist_ok=True)
    if os.path.exists(PLAYER_LOG) and os.path.getmtime(PLAYER_LOG) >= t_start - 5:
        try:
            shutil.copy2(PLAYER_LOG, os.path.join(out, "Player.log"))
        except OSError as e:
            log.say("  collect: Player.log not copied (%s)" % e)

# ------------------------------------------------------------------------------------------------ s4 --selftest
class _S4FakePlugins:
    """In-process stand-in for the C11 harness AND both plugins' S4 commands over an analytic terrain
 (worldmap_ref.SyntheticField): de.terrain answers as the contract table says and appends the s4.* rows and the
 DE.Terrain lines to a se-selftest.jsonl that S4SeTail reads; de.s4 answers from the same terrain; de.s3 placement
 writes a DE.Mirror placed line. Used only by `s4 --selftest` (no section, no process, no game)."""

    def __init__(self, field, out):
        import random as _random
        self.f, self.out = field, out
        self.rnd = _random.Random(5)
        self.se_path = os.path.join(out, "se-selftest.jsonl")
        self.sn_path = os.path.join(out, "sn-selftest-0.jsonl")
        self.rid = 0
        self.replies = {}
        self.probe = None
        self.placed = None
        self.bake_on = False
        self.feet = (0.0, -17.2, 0.0)

    # harness interface
    def attach(self):
        return True, ""

    def detach(self):
        pass

    def poll_replies(self):
        pass

    def send(self, side, cmd, args):
        self.rid += 1
        self.replies[self.rid] = self.handle(side, cmd, args or {})
        return self.rid, S.RW_OK

    def wait_reply(self, rid, timeout):
        return self.replies.pop(rid, None)

    # plugin side
    def _w(self, path, rec):
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")

    def row(self, ev, **kw):
        self._w(self.se_path, dict({"t": datetime.datetime.utcnow().isoformat() + "Z", "ev": ev}, **kw))

    def log(self, msg):
        self._w(self.se_path, {"t": datetime.datetime.utcnow().isoformat() + "Z", "ev": "log", "msg": msg})

    def ref(self, x, z):
        return self.f.first_entry(x, z, 20.0, -600.0)

    def handle(self, side, cmd, a):
        if cmd == "de.ping":
            return {"ok": True, "pong": True}
        if side == "se" and cmd == "de.terrain":
            return self.terrain(a)
        if side == "sn" and cmd == "de.s4":
            return self.s4(a)
        if cmd == "de.s3":
            if side == "se" and isinstance(a.get("place"), dict):
                c = a["place"]["sn"]
                self.placed = (c[0], c[1] - 1.25, c[2])
                return {"ok": True, "placed": True}
            if side == "sn" and a.get("action") == "placement":
                if self.placed:
                    x, b, z = self.placed
                    corners = ";".join("%r,%r,%r" % (x + dx, b + dy, z + dz) for dx in (-1.25, 1.25) for dy in (0.0, 2.5)
                                       for dz in (-1.25, 1.25))
                    self._w(self.sn_path, {"t": "x", "event": "DE.Mirror", "text": "DE.Mirror placed entity=0x5e1f "
                            "tick=100 how=c3 cornersSn=%s maxDevVsApplyM=0" % corners})
                return {"ok": True, "entities": 1 if self.placed else 0}
            return {"ok": True}
        if cmd in ("de.teleport", "de.look", "de.capture", "de.swim"):
            return {"ok": True}
        return {"ok": False, "error": "unknown command %s" % cmd}

    def terrain(self, a):
        act = a.get("action")
        site = self.site
        if act == "status":
            return {"ok": True, "gate": "OK", "condition": None, "reason": None, "tiles": 34, "terrainUp": True,
                    "failSafe": {"active": False, "reason": None, "saveBlocked": 0},
                    "provider": {"registered": True, "error": None},
                    "source": {"path": "x", "build": 20241558, "files": 5416, "bytes": 1203085204, "rows": [7, 19],
                               "manifestHash": "0x2cc277ce91763285", "indexOk": True},
                    "readRange": {"calls": 5000, "p50us": 30, "p99us": 400, "maxUs": 900, "physP99us": 500,
                                  "physMaxUs": 950},
                    "ram": {"privateBefore": 4 << 30, "privateAfter": (4 << 30) + (7 << 27)},
                    "harness": {"job": None, "state": "idle"}}
        if act == "gate":
            return {"ok": True, "match": True, "source": {"files": 5416}}
        if act == "tiles":
            AS = s4_analyzer()
            tl = []
            for (i, j, k) in AS.TILE_SLOTS:
                m = site.tile_min(i, j, k)
                mn = [site.TEST_C[0] + m[0], site.TEST_C[1] + m[1], site.TEST_C[2] + m[2]]
                tid = AS.tile_id(i, j, k)
                tl.append({"i": i, "j": j, "k": k, "id": "0x%016x" % tid, "idDec": tid, "min": mn, "exists": True,
                           "closed": False, "save": False, "bodies": 1})
                self.log("DE.Terrain TILE %d,%d,%d id=0x%016x min=(%r,%r,%r)" % (i, j, k, tid, mn[0], mn[1], mn[2]))
            self.log("DE.Terrain OK tiles=34 siteVersion=1")
            return {"ok": True, "tiles": tl}
        if act == "n3":
            self.row("s4.n3", **{"pass": True, "boxes": 600, "violations": 0})
            return {"ok": True, "pass": True, "violations": 0, "firstViolation": None}
        if act == "at0b":
            rows_ = [{"height": h, "lod": lod, "vertices": 30, "meanErr": 0.001, "maxAbsErr": 0.003 * 2 ** lod,
                      "isoMedian": 127.4, "quantization": 0.5 / 127.5 * 2 ** lod} for h in a.get("heights") or [-17.5]
                     for lod in a.get("lods") or [0, 1, 2]]
            for r in rows_:
                self.row("s4.at0b", **r)
            return {"ok": True, "rows": rows_}
        if act == "hash":
            return {"ok": True, "tiles": [{"i": 1, "j": 1, "k": 0, "contentHash": "0x1", "materialHash": "0x2",
                                           "modified": False}]}
        if act == "at1":
            o, n, sp = a["origin"], int(a["size"]), float(a["spacing"])
            cols = [(o[0] + (i - (n - 1) / 2.0) * sp, o[1] + (j - (n - 1) / 2.0) * sp) for i in range(n) for j in range(n)]
            sets = ["grid"] * len(cols)
            cols += [(o[0] + self.rnd.uniform(-60, 60), o[1] + self.rnd.uniform(-60, 60)) for _ in range(int(a["random"]))]
            sets += ["random"] * int(a["random"])
            for (x, z), st in zip(cols, sets):
                y = self.ref(x, z)
                r = {"set": st, "snX": x, "snZ": z, "fromY": a["fromY"], "toY": a["toY"], "prefetch": a["prefetch"],
                     "cells": 53, "cellsBuilt": 53, "waitMs": 2.0, "castHit": y is not None, "err": None}
                if y is not None:
                    r.update(castSnY=y + self.rnd.uniform(-0.05, 0.05), storageHit=True, storageSnY=y + 0.0003)
                self.row("s4.ray", **r)
                if self.bake_on and st == "grid" and -176 <= x <= 80 and -176 <= z <= 80:
                    self.row("s4.ray", **dict(r, set="bake", storageSnY=(r.get("storageSnY") or 0) + 0.0002))
            for c in range(10):
                self.row("s4.cell", i=c, j=0, k=0, x=0, y=0, z=0, built=True, waitedMs=1.0, buildMs=None)
            self.log("DE.Terrain AT1 done n=%d (fake)" % len(cols))
            return {"ok": True, "pending": True, "job": "at1"}
        if act == "bake":
            self.bake_on = not a.get("remove")
            if not a.get("remove"):
                self.row("s4.bake", batch=a.get("batch"), min=[0, 0, 0], entity=1, ms=10.0)
                self.log("DE.Terrain BAKE ready (fake)")
            return {"ok": True}
        if act == "drop":
            for i, (x, z) in enumerate(a["points"]):
                y = self.ref(x, z)
                self.row("s4.drop", i=i, snX=x, snZ=z, restSnY=y + 0.03, restSn=[x, y + 0.03, z], lowestCornerSnY=y + 0.03,
                         entityId=77 + i)
            self.log("DE.Terrain DROP done (fake; grids closed)")
            return {"ok": True, "pending": True, "job": "drop"}
        if act == "walk":
            x0, z0 = a["from"]
            h = math.radians(a["headingDeg"])
            for k in range(int(a["length"]) + 1):
                x, z = x0 + k * math.sin(h), z0 + k * math.cos(h)
                self.row("s4.walk", tick=10 * k, feetSn=[x, self.ref(x, z) + 0.02, z], surfaceSnY=self.ref(x, z))
                self.feet = (x, self.ref(x, z) + 0.02, z)
            self.row("s4.walk", done=True, dist=a["length"], samples=int(a["length"]) + 1, maxBelow=0.0)
            self.log("DE.Terrain WALK done (fake)")
            return {"ok": True, "pending": True, "job": "walk"}
        if act == "n1":
            x, y, z = self.feet
            row = {"feetSe": [0, 0, 0], "feetSn": [x, y, z], "storageHit": True, "storageSnY": y - 0.02, "delta": 0.02}
            self.row("s4.n1", **row)
            return dict(row, ok=True)
        if act == "perf":
            self.row("s4.perf", frames=7000, framesOver5ms=7000, hitchesOver5ms=20, fracHitches=20 / 7000.0)
            self.log("DE.Terrain PERF done (fake)")
            return {"ok": True, "pending": True, "job": "perf"}
        if act == "stop":
            return {"ok": True}
        if act in ("edit", "crater"):
            path = a.get("path") or "meteor"
            hb = [{"tile": "1,1,0", "content": "0xab", "material": "0xcd"}]
            first = {"path": path, "blocked": True, "count": 1, "hashUnchanged": True, "modified": False, "hashBefore": hb,
                     "hashAfter": hb, "ore": {"percent": 0.0, "material": None, "voxels": 0} if path == "cutout" else None}
            self.row("s4.edit", **first)
            self.log("DE.Terrain EditBlocked %s n=1" % path)
            self.row("s4.edit", **dict(first, final=True, ore=None))
            self.log("DE.Terrain edit %s final (+2000 ms) tile=1,1,0: blocked=True" % path)
            return dict(first, ok=True, pending=True)
        return {"ok": False, "error": "unknown action %s" % act}

    def s4(self, a):
        if a.get("probe"):
            res = []
            for k, c in enumerate(a["probe"]):
                y = self.ref(c[0], c[1])
                res.append({"i": k, "x": c[0], "z": c[1], "hit": y is not None, "y": (y - 0.02) if y is not None else None,
                            "collider": "Override", "layer": 30, "camY": (y or 0) + 8, "waitMs": 300,
                            "renderY": (y or 0) + 0.04, "renderDelta": 0.06, "renderNote": None, "error": None})
            self.probe = res
            return {"ok": True, "cmd": "de.s4", "pending": True, "batch": "b1", "columns": len(res)}
        if a.get("status"):
            frm = int(a.get("from") or 0)
            res = self.probe or []
            return {"ok": True, "cmd": "de.s4", "batch": "b1", "state": "done", "done": len(res), "columns": len(res),
                    "from": frm, "results": res[frm:frm + int(a.get("max") or 64)]}
        return {"ok": True}

class _S4Proc:
    def __init__(self):
        self.pid = os.getpid()

    @staticmethod
    def poll():
        return None

def s4_selftest(log: Log) -> int:
    """`s4 --selftest`: S4Runner's parts end to end against _S4FakePlugins (no section, no process, no game): every
 command, every wait on the tailed s4.* rows, the session events, then tests/s4/analyze_s4.py on the folder. Checks
 that the S4se and S4both rows the fake can satisfy are PASS."""
    import tempfile
    import types
    p = os.path.join(TOOLS, "worldmap")
    if p not in sys.path:
        sys.path.insert(0, p)
    import worldmap_ref
    import site_v1
    AS = s4_analyzer()
    field = worldmap_ref.SyntheticField(base=-17.2, sx=0.02, sz=-0.015, overhangs=[(10.0, 14.0, 10.0, 14.0, -6.0, -9.0)])
    checks = []
    tmp = tempfile.mkdtemp(prefix="s4-runner-selftest-")
    S4Runner.WALK_STALL_S = 0.5
    try:
        for mode, parts in (("S4se", ("gate", "tiles", "n3", "hash", "at1", "bake", "drop", "walk", "n1", "perf", "at0b",
                                      "n2")),
                            ("S4both", ("gate", "tiles", "at2", "wm6", "n1", "capture"))):
            out = os.path.join(tmp, mode)
            os.makedirs(out)
            a = types.SimpleNamespace(name="DeepEngineers_s4_selftest_absent", quick=True, cmd_timeout=1.0,
                                      mutants_list=[], shallow_spot=S4_SHALLOW, at1_timeout=60.0, drill_id=None,
                                      owner=False, world=None, world_info={}, mode=mode)
            sess = S2SessionLog(os.path.join(out, "s4-session.jsonl"))
            sess.event("start", mode=mode, parts=list(parts), mutants=[], selftest=True, qpcFrequency=S.QPC_FREQUENCY)
            sess.event("source", measure={"path": "x", "build": 20241558, "files": 5416, "bytes": 1203085204,
                                          "rows": [7, 19], "manifestHash": "0x2cc277ce91763285",
                                          "index": ["4096 3200 4096", "128 100 128", "32", "5 5 5"], "other": []})
            fake = _S4FakePlugins(field, out)
            fake.site = site_v1
            tail = S4SeTail(time.time() - 10, os.path.join(out, "se-*.jsonl"))
            r = S4Runner(log, a, sess, _S4Proc(), _S4Proc() if mode == "S4both" else None, out, "selftest", tail, field)
            if r.sec:
                r.sec.close()
                r.sec = None
            r.h = fake
            ok, why = r.attach()
            checks.append(("%s attach through the fake" % mode, ok, why))
            for part in parts:
                r.part(part, "begin")
                try:
                    if part == "capture":
                        r.capture = lambda name, pose: r.sess.event("capture", name=name, written=True, png=name)
                    getattr(r, "p_" + part)()
                except Exception as e:
                    checks.append(("%s part %s ran" % (mode, part), False, repr(e)))
                r.part(part, "end")
            sess.event("end", rc=None, stats=r.stats)
            sess.close()
            rows = {x["id"]: x for x in AS.evaluate(out, [], oracle=field)}
            want = ["S4.SOURCE", "S4.GATE", "S4.FAILSAFE", "S4.TILES", "S4.IDS"]
            if mode == "S4se":
                want += ["S4.N3", "S4.A-T0b", "S4.A-T1.1", "S4.A-T1.1h", "S4.A-T1.1u", "S4.A-T1.1r", "S4.A-T1.1b",
                         "S4.A-T1.2d", "S4.A-T1.2w", "S4.A-T1.3m", "S4.A-T1.3r", "S4.A-T1.3c", "S4.A-T1.3f", "S4.N1"] + [
                    "S4.N2.%s" % q for q in ("cutout", "shipdrill", "explosion", "meteor", "voxelhand", "writerange")]
            else:
                want += ["S4.A-T2", "S4.WM-6a", "S4.WM-6b", "S4.N1"]
            for cid in want:
                got = rows.get(cid, {})
                checks.append(("%s %s PASS through the runner" % (mode, cid), got.get("status") == "PASS",
                               "%s %s" % (got.get("status"), got.get("measured"))))
            checks.append(("%s no command left unanswered" % mode, r.stats["noReply"] == 0 and not r.unsupported,
                           json.dumps(r.stats)))
    finally:
        S4Runner.WALK_STALL_S = 10.0
        shutil.rmtree(tmp, ignore_errors=True)
    bad = [c for c in checks if not c[1]]
    for name, ok, info in checks:
        print("  %-4s %s%s" % ("ok" if ok else "FAIL", name, "" if ok else "  [%s]" % info))
    print("S4 RUNNER SELFTEST %s (%d checks, %d failed)" % ("PASS" if not bad else "FAIL", len(checks), len(bad)))
    return 0 if not bad else 1

def s4(log: Log, a) -> int:
    if getattr(a, "selftest", False):
        return s4_selftest(log)
    if getattr(a, "list_mutant_groups", False):
        for name, g in S4_MUTANT_GROUPS.items():
            print("  %-17s %-6s mutants %-40s parts %-24s red %s%s%s" % (
                name, g["mode"], ",".join(g["mutants"]), ",".join(g["parts"]), ",".join(g["red"]),
                " (COPY: --world)" if g.get("copy") else "", "; watch %s" % "; ".join(g["watch"]) if g.get("watch")
                else ""))
        return 0
    if a.analyze:
        return s4_analyze(log, a, a.analyze)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(REPO, "tests", "out", "s4", stamp)
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    parts = None
    if a.mutant_group:
        g = S4_MUTANT_GROUPS.get(a.mutant_group)
        if g is None:
            log.say("unknown mutant group %s (known: %s)" % (a.mutant_group, ",".join(S4_MUTANT_GROUPS)))
            return 2
        mutants = list(g["mutants"])
        parts = list(g["parts"])
        a.mode = g["mode"]
    known = (S4_PARTS_SE + S4_PARTS_COPY) if a.mode == "S4se" else S4_PARTS_BOTH
    if a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
        if parts == ["all"]:
            parts = list(dict.fromkeys(known))
    parts = parts or list(S4_DEFAULT_PARTS[a.mode])
    unknown = [p for p in parts if p not in known]
    if unknown:
        log.say("unknown parts %s for %s (known: %s)" % (unknown, a.mode, ",".join(known)))
        return 2
    for p in parts:
        m = S4_PART_MUTANT.get(p)
        if m and m not in mutants:
            mutants.append(m)
    off = [m for m in mutants if m in ("S4.M1", "S4.M1a", "S4.M1b", "S4.M1c", "S4.M1d")]
    if off:
        log.say("%s are offline mutants (tests/DeepEngineers.Terrain.Tests, A-T0); SE refuses them" % ",".join(off))
        return 2
    bad_m = [m for m in mutants if m not in S4_TERRAIN_MUTANTS]
    if bad_m:
        log.say("unknown S4 mutants %s (known: %s)" % (bad_m, ",".join(S4_TERRAIN_MUTANTS)))
        return 2
    copy_parts = [p for p in parts if p in S4_PARTS_COPY]
    copy_mut = [m for m in mutants if m in ("S4.M4", "S4.M10") or m.startswith("S4.M9")]
    a.mutants_list = mutants
    a.cont = not a.no_continue
    se_env = dict(S2B_SE_ENV)
    if mutants:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(mutants)
    if a.mode == "S4se":
        a.sn_cmd = None
    sn_cmd, se_cmd = session_commands(a)
    oracle = s4_oracle()
    log.say("S4 session %s (%s): parts %s%s%s%s; SE: %s%s; out %s" % (
        stamp, a.mode, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants
        else "", " world %s" % a.world if a.world else "", subprocess.list2cmdline(se_cmd),
        "; Subnautica: %s" % subprocess.list2cmdline(sn_cmd) if a.mode == "S4both" else "", out), ev="s4_plan")
    wi = s4_world_info(a.world) if a.world else {}
    a.world_info = wi
    if a.dry_run:
        for line in s4_plan_lines(a, parts, oracle):
            print(line)
        log.say("dry run: nothing started")
        if oracle is not None:
            oracle.close()
        return 0
    # real run: the throwaway-copy rules (ROADMAP rule 10)
    mixed = s4_mutant_part_mix(parts)
    if mixed:
        log.say("REFUSE: %s" % mixed)
        return 2
    if "g2e5" in parts and len(parts) > 1:
        log.say("REFUSE: g2e5 runs alone (its copy has no world mod: no other part can run on it)")
        return 2
    if (copy_parts or copy_mut) and not a.world:
        log.say("REFUSE: parts %s / mutants %s need --world <throwaway copy> (tools/lab/make_terrain_copy.py)" % (
            copy_parts, copy_mut))
        return 2
    if a.world:
        probs = s4_copy_problems(a, parts, wi)
        if probs:
            log.say("REFUSE --world %s: %s" % (a.world, "; ".join(probs)))
            return 2
    os.makedirs(out, exist_ok=True)
    if parts == ["g2e2noplugin"]:
        # an project step: a session folder so that `s4 --analyze <out> --no-plugin-log FILE` judges S4.G2-E2c later
        with open(os.path.join(out, "s4-session.jsonl"), "w", encoding="utf-8") as f:
            for rec in ({"ev": "start", "stamp": stamp, "mode": a.mode, "parts": parts, "mutants": [], "world": a.world,
                         "worldInfo": wi}, {"ev": "plan", "lines": s4_plan_lines(a, parts, oracle)}, {"ev": "end"}):
                f.write(json.dumps(rec, default=str) + "\n")
        for line in s4_plan_lines(a, parts, oracle):
            print(line)
        log.say("g2e2noplugin is an owner step: nothing started; session folder %s" % out)
        return 0
    sess = S2SessionLog(os.path.join(out, "s4-session.jsonl"))
    standin_se, standin_sn = bool(a.se_cmd), bool(a.sn_cmd)
    sess.event("start", stamp=stamp, mode=a.mode, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:],
               quick=a.quick, world=a.world, worldInfo=wi, octrees=s4_octree_dir(), seCmd=se_cmd,
               snCmd=sn_cmd if a.mode == "S4both" else None, standin={"se": standin_se, "sn": standin_sn},
               qpcFrequency=S.QPC_FREQUENCY, spots={"shallow": list(a.shallow_spot), "deep": list(S4_DEEP)},
               at1=S4_AT1_QUICK if a.quick else S4_AT1, owner=a.owner, seEnv=se_env)
    sess.event("plan", lines=s4_plan_lines(a, parts, oracle))
    sess.event("source", measure=s4_measure_source(), qpc=S.qpc())     # S4.SOURCE: read-only listing, every session
    hashed = [] if standin_se else [SE_CFG, WATER_CLIENT_SETTINGS]
    for path in hashed:
        sess.event("file_hash", path=path, when="before", qpc=S.qpc(),
                   sha256=sha256_file(path) if os.path.exists(path) else None)
    if a.world and any(p in parts for p in ("g2e3b", "m10")):
        s4_scan(log, sess, a, "before-load")      # the save as SE will load it (read-only)
    t_start = time.time()
    tail = S4SeTail(t_start)
    rc, host, se, sn, runner = None, None, None, None, None
    try:
        if a.mode == "S4se":
            rc, host, se = start_se_alone(log, a, out, se_env)
            in_world = rc is None and wait_se_in_world(log, a, se, a.world_timeout)
        else:
            S.fine_timer(True)
            rc, sn, se = start_games(log, a, stdout_dir=out, se_extra_env=se_env)
            in_world = rc is None and wait_in_world(log, sess, a, sn, se)
        if "g2e5" in parts:
            sess.event("g2e5", inWorld=bool(in_world))
        if in_world:
            runner = S4Runner(log, a, sess, se, sn, out, stamp, tail, oracle)
            ok, why = runner.attach()
            if not ok:
                log.say("S4: harness not usable: %s" % why, ev="s4_harness_refused")
                rc = 1
            else:
                st = runner.status("session.check")
                if st is None or (not st.get("ok") and s4_unknown(st)):
                    log.say("S4: SE does not take de.terrain (%s): an SE build without S4? Parts not run." % (
                        json.dumps(st)[:200] if st else "no reply"), ev="s4_no_terrain")
                    rc = 1
                else:
                    runner.sample_memory("inworld")
                    runner.park()
                    for p in parts:
                        if not runner.alive():
                            log.say("S4: a game exited before part %s" % p, ev="s4_exit_during_script")
                            break
                        runner.part(p, "begin")
                        try:
                            getattr(runner, "p_" + p)()
                        except Exception as e:
                            log.say("S4 part %s failed: %r" % (p, e), ev="s4_part_error", part=p)
                            sess.event("part_error", name=p, error=repr(e))
                        runner.part(p, "end")
                    if runner.s3_placed and runner.alive():
                        runner.call("se", "de.s3", {"remove": True}, label="se.remove")
                log.say("S4 script finished: %s; unsupported %s" % (json.dumps(runner.stats), sorted(runner.unsupported)),
                        ev="s4_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            close_session(log, a, sn, se, standin_se, a.mode == "S4se" or standin_sn)
        if host is not None and host.poll() is None:
            S.terminate_pid(host.pid, 0)     # the passive stand-in host (our own Python process, never a game)
        if a.mode == "S4both":
            S.fine_timer(False)
        for path in hashed:
            sess.event("file_hash", path=path, when="after", qpc=S.qpc(),
                       sha256=sha256_file(path) if os.path.exists(path) else None)
        sess.event("end", rc=rc, stats=runner.stats if runner else None,
                   unsupported=sorted(runner.unsupported) if runner else None)
        sess.close()
        collect(log, out, t_start, sn if a.mode == "S4both" else host, a.mode == "S4se" or standin_sn, standin_se, stamp)
        s4_collect_extra(log, out, stamp, t_start)
        if oracle is not None:
            oracle.close()
    if rc is not None:
        log.say("S4 session aborted (rc %d); analysing what was collected" % rc)
    res = s4_analyze(log, a, out)
    return rc if rc is not None else res

# S8-integrator: the S0 exit wait that stood here is replaced by launcher_s8.wait_exits (the `run` exit
# path: both handles, the section sampled read-only, CloseMainWindow at 15 s, kill offers only); nothing else used it.

# ----------------------------------------------------------------------------------------------------------------
# stop
# ----------------------------------------------------------------------------------------------------------------

def confirm_kill(log: Log, pid: int, what: str, allow_kill: bool) -> bool:
    if not allow_kill:
        log.say("%s pid %d still running; not killed (pass --kill to be asked)" % (what, pid))
        return False
    if not sys.stdin or not sys.stdin.isatty():
        log.say("%s pid %d still running; a kill needs the owner's typed confirmation (interactive console)" % (
            what, pid))
        return False
    ans = input("Kill %s by exact PID %d? Type the PID to confirm: " % (what, pid)).strip()
    if ans != str(pid):
        log.say("not confirmed; %s pid %d left running" % (what, pid))
        return False
    ok = S.terminate_pid(pid)
    log.say("killed %s pid %d: %s" % (what, pid, ok), ev="kill", pid=pid, ok=ok)
    return ok

def wait_pid(pid: int, timeout: float) -> bool:
    w = S.ProcessWatch(pid)
    try:
        return w.wait(timeout)
    finally:
        w.close()

def send_de_quit(log: Log, name: str):
    """de.quit over C11 (CtlToSn) when devControl allows a harness; returns the reply or None."""
    sec = S.Section.open(name)
    if sec is None:
        log.say("  de.quit: section absent")
        return None
    try:
        if sec.mapped_size < L.SECTION_SIZE or sec.hdr("magic") != L.MAGIC:
            log.say("  de.quit: section not valid")
            return None
        h = S.Harness(sec)
        ok, why = h.attach()
        if not ok:
            log.say("  de.quit not sent: %s" % why)
            return None
        try:
            rid, st = h.send("sn", "de.quit")
            if st != S.RW_OK:
                log.say("  de.quit not written (%s)" % st)
                return None
            rep = h.wait_reply(rid, 5.0)
            log.say("  de.quit reply: %s" % (json.dumps(rep) if rep else "none within 5 s (release build?)"),
                    ev="de_quit", reply=rep)
            return rep
        finally:
            h.detach()
    finally:
        sec.close()

def stop_sn(log: Log, name: str, pid: int, allow_kill: bool, timeout: float) -> bool:
    """de.quit over C11 when devControl allows it, else CloseMainWindow; wait; a kill only when confirmed."""
    if S.process_state(pid) != "alive":
        return True
    rep = send_de_quit(log, name)
    if rep and rep.get("ok") and wait_pid(pid, timeout):
        log.say("Subnautica pid %d quit on de.quit" % pid, ev="sn_closed", how="de.quit")
        return True
    if S.process_state(pid) == "alive":
        posted = S.close_main_window(pid)
        log.say("CloseMainWindow(Subnautica %d): %s" % (pid, "WM_CLOSE posted" if posted else "no main window"))
        if wait_pid(pid, timeout):
            log.say("Subnautica pid %d closed" % pid, ev="sn_closed", how="CloseMainWindow")
            return True
        return confirm_kill(log, pid, "Subnautica", allow_kill)
    return True

def stop(log: Log, a) -> int:
    se_pids = [a.se_pid] if a.se_pid else [p["pid"] for p in processes_named(("Legacy.exe",))]
    sn_pids = [a.sn_pid] if a.sn_pid else [p["pid"] for p in processes_named(("Subnautica.exe",))]
    if not se_pids and not sn_pids:
        log.say("nothing to stop (no Legacy or Subnautica process)")
        return 0
    ok = True
    for pid in se_pids:
        if S.process_state(pid) != "alive":
            continue
        posted = S.close_main_window(pid)
        log.say("CloseMainWindow(SE %d): %s" % (pid, "WM_CLOSE posted" if posted else "no main window"))
        if wait_pid(pid, a.timeout):
            log.say("SE pid %d closed" % pid, ev="se_closed")
        else:
            ok = confirm_kill(log, pid, "SE", a.kill) and ok
    for pid in sn_pids:
        if se_pids and S.process_state(pid) == "alive":
            log.say("waiting up to %.0f s for Subnautica to quit by itself after SE" % L.SN_QUIT_AFTER_SE_EXIT_S)
            if wait_pid(pid, L.SN_QUIT_AFTER_SE_EXIT_S):
                log.say("Subnautica pid %d quit by itself" % pid, ev="sn_closed", how="self")
                continue
        ok = stop_sn(log, a.name, pid, a.kill, a.timeout) and ok
    left = processes_named(CRASH_HANDLERS)
    if left:
        log.say("crash handlers left: %s (close by exact PID with the owner's confirmation)" % ", ".join(
            "%d" % p["pid"] for p in left))
    return 0 if ok else 1

# ----------------------------------------------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------------------------------------------

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="DeepEngineers launcher")
    ap.add_argument("--name", default=L.SECTION_NAME, help="section name (stand-in tests use their own)")
    ap.add_argument("--configuration", default="Release", choices=("Release", "Debug"))
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("preflight")
    p.add_argument("--no-create-backup", action="store_true", help="do not create a missing subnautica-saves backup")
    p.add_argument("--refresh-saves-backup", action="store_true", help="create a new one when it is stale")
    p = sub.add_parser("deploy")
    p.add_argument("--dry-run", action="store_true")
    sub.add_parser("manifest")
    p = sub.add_parser("run")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"),
                   help="snPhase to wait for: ReadyIdle from S1 (default), Menu for the S0 runbook")
    p.add_argument("--se-anyway", action="store_true",
                   help="start SE even when Subnautica never reached the ready phase (bridge offline); default: abort")
    p.add_argument("--timeout", type=float, default=L.READY_WAIT_S)
    p.add_argument("--continue", dest="cont", action="store_true",
                   help="SE loads the last world (Pulsar's -continue option, passed to Legacy.exe)")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in tests only)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in tests only)")
    p.add_argument("--any-sn-pid", action="store_true", help="accept a ready section whose snPid is not the started "
                                                             "process (a wrapper command)")
    p = sub.add_parser("s1", help="one scripted S1 session: run, C11 test commands, logs, oracle table")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="print the steps; start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in tests only)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in tests only)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--quick", action="store_true", help="quick plan (stand-in dry runs: short holds, 2,000 frames)")
    p.add_argument("--parts", default="", help="comma list of wm2,hold,wm3,wm4,yaw,g1e5 (default: all)")
    p.add_argument("--out", default=None, help="output folder (default tests/out/s1/<stamp>)")
    p.add_argument("--keep-running", action="store_true", help="leave both games running after the script")
    p.add_argument("--cmd-timeout", type=float, default=5.0, help="C11 reply wait per command (s)")
    p.add_argument("--baseline", default=None, help="s1-results.json of a baseline run (S1.M2 shift comparison)")
    p.add_argument("--host-slot", default="slot0000", help="Subnautica host slot for S1.N4 (<Subnautica>/SNAppData/SavedGames/<slot>)")
    p.add_argument("--host-slot-dir", default=None, help="explicit folder to hash for S1.N4 (stand-in tests)")
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the oracles on a collected folder")
    p = sub.add_parser("s2", help="one scripted S2 session: run, C11 parts, samplers, tools, the S2 oracle table")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="list the parts; start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in tests only)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in tests only)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--parts", default="", help="comma list of %s (default: all but n5)" % ",".join(S2_PARTS))
    p.add_argument("--owner", action="store_true", help="include the owner steps (prompts printed, effects awaited)")
    p.add_argument("--se-alone", action="store_true", help="SE-alone baseline for S2.N3/N11 (no Subnautica; a passive "
                                                           "stand-in host keeps the section); default parts n3,n11")
    p.add_argument("--quick", action="store_true", help="short holds (stand-in dry runs)")
    p.add_argument("--toggles", type=int, default=50, help="T-D1 see-through toggles")
    p.add_argument("--sample-hz", type=float, default=10.0, help="section sampler rate during parts")
    p.add_argument("--no-video", action="store_true", help="no composed-desktop videos (td1, N0, the n2 zone exit)")
    p.add_argument("--auto-fallback", action="store_true",
                   help="keep the SE plugin's automatic A' fallback on (default: off for a scripted session, so that a "
                        "probe verdict is a row of the table and never switches the design halfway through)")
    p.add_argument("--screen-centre", type=lambda v: tuple(int(x) for x in v.split(",")), default=(1280, 720),
                   help="x,y of the probe pixel of the videos (the primary monitor's centre)")
    p.add_argument("--out", default=None, help="output folder (default tests/out/s2/<stamp>)")
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--baseline", default=None, help="SE-alone session folder or perf-results.json (S2.N3/N11)")
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S2 oracles on a collected folder")
    p = sub.add_parser("s2b", help="one scripted S2b swimming session in SE alone (N1-N4): de.swim scripts, logs, the "
                                   "S2b oracle table")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="list the parts and the scripts; start nothing")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in plumbing test only)")
    p.add_argument("--parts", default="", help="comma list of %s (default: all)" % ",".join(S2B_PARTS))
    p.add_argument("--quick", action="store_true", help="shorter n2/n3/n4 holds (n1 is never shortened)")
    p.add_argument("--mutants", default="", help="DEEPENGINEERS_MUTANTS for SE only (S2b.M1,S2b.M2,S2b.M3,S2b.M4)")
    p.add_argument("--n3-cycles", type=int, default=0, help="surface crossing cycles (default 4, 2 with --quick)")
    p.add_argument("--n4-min-o2", type=float, default=0.08,
                   help="surface early when the O2 fraction falls to this (0: never; the character may suffocate)")
    p.add_argument("--out", default=None, help="output folder (default tests/out/s2b/<stamp>)")
    p.add_argument("--keep-running", action="store_true", help="leave SE running after the script")
    p.add_argument("--cmd-timeout", type=float, default=5.0, help="C11 reply wait per command (s)")
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S2b oracle on a collected folder")
    p = sub.add_parser("s5a", help="one scripted S5a 'Arms and breath' session in SE alone (fins, o2, arms, choke): de.swim "
                                   "scripts, the C1 swim extension sampled, the S5a oracle table (SN log joined with --sn-log)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="list the parts and the scripts; start nothing")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in plumbing test only)")
    p.add_argument("--parts", default="", help="comma list of %s (default: all)" % ",".join(S5A_PARTS))
    p.add_argument("--quick", action="store_true", help="shorter o2/choke holds (the fins scripts are never shortened)")
    p.add_argument("--mutants", default="", help="DEEPENGINEERS_MUTANTS for SE only (S5a.M1,S5a.M2,S5a.M3; S2b ids allowed)")
    p.add_argument("--sn-log", action="append", default=[], metavar="FILE",
                   help="the Subnautica plugin's log of the same session (repeatable; joined by C1 tick by the analyser)")
    p.add_argument("--out", default=None, help="output folder (default tests/out/s5a/<stamp>)")
    p.add_argument("--keep-running", action="store_true", help="leave SE running after the script")
    p.add_argument("--cmd-timeout", type=float, default=5.0, help="C11 reply wait per command (s)")
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S5a oracle on a collected folder")
    p = sub.add_parser("s3", help="one scripted S3 'First block' session (S3a: Subnautica + the fake SE producer; S3b: "
                                   "both games): placement, captures, N2/N3, teardown, the S3 oracle table")
    p.add_argument("--mode", choices=("S3a", "S3b"), default="S3a",
                   help="S3a: SE is tests/standins/fake_se_producer.py --s3a --s2 (unless --se-cmd); S3b: both games")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe (S3b)")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="list the parts and their steps; start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in dry run only)")
    p.add_argument("--sn-standin", action="store_true",
                   help="S3b's SE side alone: tests/standins/fake_sn_host.py --s1 --s3 replaces Subnautica (SE's C2/C3/C4 "
                        "records validated, C1/C2/C3 and the computed N2/N3 rows; --mutants S3.M1 or S3.M2 go to the "
                        "host; no Subnautica launch, the image rows stay MISSING)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (S3a default: the fake producer)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--parts", default="", help="comma list of %s (default per mode; place always first)" %
                   ",".join(S3_PARTS))
    p.add_argument("--mutants", default="", help="S3.M1..S3.M8: Subnautica's DEEPENGINEERS_MUTANT; S3.M4 at the producer "
                                                 "(fake --s3-mutant M4 in S3a, SE's DEEPENGINEERS_MUTANTS in S3b)")
    p.add_argument("--mutant-group", default=None, help="a runbook group (mutants + parts): %s" %
                   ",".join(S3_MUTANT_GROUPS))
    p.add_argument("--list-mutant-groups", action="store_true", help="print the S3 seeded-failure groups and exit")
    p.add_argument("--quick", action="store_true", help="shorter holds and an 8-frame C8 burst (stand-in dry runs)")
    p.add_argument("--burst", type=int, default=0, help="P3 captures per target for S3.C8 (default 60, 8 with --quick)")
    p.add_argument("--deep-spot", type=lambda v: tuple(float(x) for x in v.split(",")), default=S3_DEEP_SPOT,
                   help="x,z (SN) of the deep lab point (default the grandReef spot %g,%g)" % S3_DEEP_SPOT)
    p.add_argument("--shallow-spot", type=lambda v: tuple(float(x) for x in v.split(",")), default=S3_SHALLOW_SPOT,
                   help="x,z (SN) of the shallow point for C8-C10 (< 20 m; default the site anchor %g,%g)" %
                   S3_SHALLOW_SPOT)
    p.add_argument("--heading", type=float, default=S3_HEADING_DEG, help="grid Forward heading (SN degrees)")
    p.add_argument("--lab-heading", type=float, default=0.0, help="Subnautica lab plan heading (its P1/P4 look along it; "
                                                                  "the Seamoth goes 6 m to its right)")
    p.add_argument("--lab-camera", choices=("override", "c1"), default="override",
                   help="Subnautica lab runs: its own camera override at the plan poses (default), or wait for the "
                        "producer's C1 camera there (c1: this launcher does not yet drive SE's camera to the plan "
                        "poses of the DE.S3 LAB plan line, so a c1 run waits and fails per pose)")
    p.add_argument("--c9-time", type=float, default=S3_C9_TIME, help="de.time day fraction of the shallow captures "
                                                                      "(a 45-degree sun for the S3.C9 shadow: "
                                                                      "s3_sun_elevation_deg; 0.35 gave 64 degrees)")
    p.add_argument("--paint-hsv", type=lambda v: tuple(float(x) for x in v.split(",")), default=(0.58, 0.4, 0.1),
                   help="S3b plate colour (ColorMaskHSV h,s,v) for S3.C13 (the owner may pick another)")
    p.add_argument("--texture-set", default="large_edges", help="S3a fake producer armor maps (large_edges | "
                                                                "large_square_plate)")
    p.add_argument("--screen", default="2560x1440", help="S3a fake producer screen (its pixel maths)")
    p.add_argument("--fov", type=float, default=70.0, help="S3a fake producer vertical FOV")
    p.add_argument("--s1-results", default=None, help="s1-results.json whose S1.N2 step p50 is the S3.N2 reference "
                                                      "(default 5.08 ms, the S1 real run)")
    p.add_argument("--baseline-logs", default=None, help="a collected folder whose LogOutput.log / Player.log are the "
                                                         "S3.C11 baseline")
    p.add_argument("--out", default=None, help="output folder (default tests/out/s3/<stamp>)")
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S3 oracles on a collected folder")
    p = sub.add_parser("s4", help="one scripted S4 'Planet + terrain' session (S4se: SE alone, DE Lab or a throwaway "
                                   "copy; S4both: both games): de.terrain / de.s4 over C11, the S4 oracle table")
    p.add_argument("--mode", choices=S4_MODES, default="S4se",
                   help="S4se: SE alone with the passive stand-in host; S4both: both games (A-T2, WM-6, N1, captures)")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=L.READY_WAIT_S, help="Subnautica ready wait (s, S4both)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load (+ hello in S4both) wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="print the plan (parts, commands, source manifest, reference "
                                                          "heights, world-copy checks); start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in plumbing only)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in plumbing only)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--parts", default="", help="comma list, or 'all'; S4se: %s; copy parts (--world): %s; S4both: %s" % (
        ",".join(S4_PARTS_SE), ",".join(S4_PARTS_COPY), ",".join(S4_PARTS_BOTH)))
    p.add_argument("--mutants", default="", help="DEEPENGINEERS_MUTANTS for SE only (%s)" % ",".join(S4_TERRAIN_MUTANTS))
    p.add_argument("--mutant-group", default=None, help="a runbook group (mode + mutants + parts): %s" %
                   ",".join(S4_MUTANT_GROUPS))
    p.add_argument("--list-mutant-groups", action="store_true", help="print the S4 seeded-failure groups and exit")
    p.add_argument("--selftest", action="store_true", help="the S4 runner's parts against in-process fake plugins (no "
                                                           "section, process or game), then the analyzer")
    p.add_argument("--world", "--world-copy", dest="world", default=None,
                   help="the throwaway world copy (tools/lab/make_terrain_copy.py; autosave off; LastSession pointed at "
                        "it): required by %s and the copy mutants" % ",".join(S4_PARTS_COPY))
    p.add_argument("--owner", action="store_true", help="wait for the owner's in-game steps (the saves of g2e2save, "
                                                        "g2e3b, m4, m10); without it those rows stay MISSING")
    p.add_argument("--drill-id", default=None, help="n2 shipdrill: the entity id (0x...) of a ship drill in the copy")
    p.add_argument("--shallow-spot", type=lambda v: tuple(float(x) for x in v.split(",")), default=S4_SHALLOW,
                   help="x,z (SN) of the A-T1 grid / WM-6 / N1 centre (default the Safe Shallows anchor %g,%g)" %
                   S4_SHALLOW)
    p.add_argument("--quick", action="store_true", help="a 16x16 A-T1 grid and 100 random rays (stand-in or smoke runs)")
    p.add_argument("--at1-timeout", type=float, default=1800.0, help="the longest wait for one at1 batch of rays (s)")
    p.add_argument("--sn-log", action="append", default=[], metavar="FILE",
                   help="extra Subnautica logs for the analysis (BepInEx LogOutput.log is collected already)")
    p.add_argument("--octrees", default=None, help="CompiledOctreesCache for the reference heights (default: the "
                                                   "Subnautica install, read in place)")
    p.add_argument("--baseline", default=None, help="a no-tile s4 session folder (the S4.M5 run): the S4.A-T1.3m RAM "
                                                    "delta when the plugin gives no figure")
    p.add_argument("--no-plugin-log", default=None, help="the owner's SpaceEngineers_*.log of the copy loaded without "
                                                         "the plugin (S4.G2-E2c)")
    p.add_argument("--out", default=None, help="output folder (default tests/out/s4/<stamp>)")
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S4 oracles on a collected folder")
    if HERE not in sys.path:
        sys.path.insert(0, HERE)
    if not RELEASE:   # S9-installer: a release folder ships no scripted-session modules (s5, tp1, s6, s7a, s7)
        import launcher_s5   # noqa: E402 (s5 and tp1: src/DeepEngineers.Launcher/launcher_s5.py, ROADMAP S5)
        launcher_s5.add_parsers(sub, sys.modules[__name__])
        import launcher_s6   # noqa: E402 (s6: src/DeepEngineers.Launcher/launcher_s6.py, ROADMAP S6)
        launcher_s6.add_parsers(sub, sys.modules[__name__])
        import launcher_s7a  # noqa: E402 (s7a and tp1 --picture: src/DeepEngineers.Launcher/launcher_s7a.py, ROADMAP S7a)
        launcher_s7a.add_parsers(sub, sys.modules[__name__])
        import launcher_s7   # noqa: E402 (s7: src/DeepEngineers.Launcher/launcher_s7.py, ROADMAP S7)
        launcher_s7.add_parsers(sub, sys.modules[__name__])
        import launcher_s8a  # noqa: E402 (s8a: src/DeepEngineers.Launcher/launcher_s8a.py, ROADMAP "### S8a"; S8a (B4 hook))
        launcher_s8a.add_parsers(sub, sys.modules[__name__])
        import launcher_s11  # noqa: E402 (s11: src/DeepEngineers.Launcher/launcher_s11.py, ROADMAP "### S11"; S11 hook)
        launcher_s11.add_parsers(sub, sys.modules[__name__])
        import launcher_s16  # noqa: E402 (s16: src/DeepEngineers.Launcher/launcher_s16.py, ROADMAP "### S16"; S16 hook)
        launcher_s16.add_parsers(sub, sys.modules[__name__])
        import launcher_s13  # noqa: E402 (s13: src/DeepEngineers.Launcher/launcher_s13.py, ROADMAP "### S13"; S13 hook)
        launcher_s13.add_parsers(sub, sys.modules[__name__])
        import launcher_s14  # noqa: E402 (s14: src/DeepEngineers.Launcher/launcher_s14.py, ROADMAP "### S14"; S14 hook)
        launcher_s14.add_parsers(sub, sys.modules[__name__])
        import launcher_s15  # noqa: E402 (s15: src/DeepEngineers.Launcher/launcher_s15.py, ROADMAP "### S15"; S15 hook)
        launcher_s15.add_parsers(sub, sys.modules[__name__])
        import launcher_s12  # noqa: E402 (s12: src/DeepEngineers.Launcher/launcher_s12.py, ROADMAP "### S12"; S12 hook)
        launcher_s12.add_parsers(sub, sys.modules[__name__])
        import launcher_s17  # noqa: E402 (s17: src/DeepEngineers.Launcher/launcher_s17.py, docs/S17-BRIEF.md; S17 hook)
        launcher_s17.add_parsers(sub, sys.modules[__name__])
        import launcher_s10  # noqa: E402 (s10: src/DeepEngineers.Launcher/launcher_s10.py, ROADMAP "### S10"; S10 hook)
        launcher_s10.add_parsers(sub, sys.modules[__name__])
    import launcher_release  # noqa: E402 (S9-installer: paths, backup; src/DeepEngineers.Launcher/launcher_release.py)
    launcher_release.add_parsers(sub, sys.modules[__name__])
    import launcher_s8  # noqa: E402 (S8-exit: restart-renderer, restore-settings, kill; src/DeepEngineers.Launcher/launcher_s8.py)
    launcher_s8.add_parsers(sub, sys.modules[__name__])
    p = sub.add_parser("stop")
    p.add_argument("--sn-pid", type=int, default=0)
    p.add_argument("--se-pid", type=int, default=0)
    p.add_argument("--timeout", type=float, default=CLOSE_WAIT_S)
    p.add_argument("--kill", action="store_true", help="after the timeout, ask the owner to confirm a kill by PID")
    p = sub.add_parser("restore-registry")
    p.add_argument("--yes", action="store_true")
    a = ap.parse_args(sys.argv[1:] if argv is None else argv)
    if RELEASE and a.cmd in launcher_release.DEV_VERBS:   # S9-installer: these need the repository
        print(launcher_release.dev_verb_refusal(a.cmd), flush=True)
        return 2
    log = Log(a.cmd)
    if a.cmd in launcher_release.VERBS:   # S9-installer
        return launcher_release.run(sys.modules[__name__], log, a)
    if a.cmd in launcher_s8.COMMANDS:   # S8-exit
        return launcher_s8.run(sys.modules[__name__], log, a)
    if a.cmd == "preflight":
        return preflight(log, a)
    if a.cmd == "deploy":
        return deploy(log, a)
    if a.cmd == "manifest":
        return write_manifest(log, a.configuration, deployed_now=False)
    if a.cmd == "run":
        if not hasattr(a, "timeout"):
            a.timeout = L.READY_WAIT_S
        return run(log, a)
    if a.cmd == "s1":
        return s1(log, a)
    if a.cmd == "s2":
        return s2(log, a)
    if a.cmd == "s2b":
        return s2b(log, a)
    if a.cmd == "s5a":
        return s5a(log, a)
    if a.cmd == "s3":
        return s3(log, a)
    if a.cmd == "s4":
        return s4(log, a)
    if a.cmd in ("s7a", "tp1"):
        return launcher_s7a.run(sys.modules[__name__], log, a)   # tp1 without --picture goes on to launcher_s5
    if a.cmd == "s7":
        return launcher_s7.run(sys.modules[__name__], log, a)
    if a.cmd == "s8a":
        return launcher_s8a.run(sys.modules[__name__], log, a)   # S8a (B4 hook)
    if a.cmd == "s5":
        return launcher_s5.run(sys.modules[__name__], log, a)
    if a.cmd == "s6":
        return launcher_s6.run(sys.modules[__name__], log, a)
    if a.cmd == "s11":
        return launcher_s11.run(sys.modules[__name__], log, a)   # S11 hook
    if a.cmd == "s16":
        return launcher_s16.run(sys.modules[__name__], log, a)   # S16 hook
    if a.cmd == "s13":
        return launcher_s13.run(sys.modules[__name__], log, a)   # S13 hook
    if a.cmd == "s14":
        return launcher_s14.run(sys.modules[__name__], log, a)   # S14 hook
    if a.cmd == "s15":
        return launcher_s15.run(sys.modules[__name__], log, a)   # S15 hook
    if a.cmd == "s17":
        return launcher_s17.run(sys.modules[__name__], log, a)   # S17 hook
    if a.cmd == "s12":
        return launcher_s12.run(sys.modules[__name__], log, a)   # S12 hook
    if a.cmd == "s10":
        return launcher_s10.run(sys.modules[__name__], log, a)   # S10 hook
    if a.cmd == "stop":
        return stop(log, a)
    if a.cmd == "restore-registry":
        return restore_registry(log, a.yes)
    return 2

if __name__ == "__main__":
    sys.exit(main())
