"""S7t screenshot-pair oracle (ROADMAP "### S7t Seamless transition": T2, T5, T6 measure SE's picture against
One entry point, one mode per item
each mode lives in its own module next to this file and is dispatched by name. Run with python -I.

 python -I tests/s7t/compare.py patch SE.png SN.png --patch suit=X,Y,W,H/X,Y,W,H [--patch hull=...] [--json]
 python -I tests/s7t/compare.py patch --selftest
 python -I tests/s7t/compare.py shadow SE.png SN.png --shadow hull=X,Y,W,H/X,Y,W,H --lit hull=X,Y,W,H/X,Y,W,H [--tol 0.25]
 python -I tests/s7t/compare.py bands SE.png SN.png [--horizon-se F] [--horizon-sn F] [--band NAME=RECT/RECT] [--tol 24]
 python -I tests/s7t/compare.py bands SN.png --targets sn # one screenshot against the measured table (S7t-sn-altitude)
 python -I tests/s7t/compare.py --selftest # every mode's self-test

Modes: patch (T6, colour_patch.py: the mean colour of a user-given rectangle in both screenshots; SE's paint must keep
its hue in Subnautica). The T2/T5 band modes (sky, horizon, water bands; shadowed vs lit hull face) register in MODES.
"""
import importlib
import os
import sys

# mode name -> (module next to this file, one-line description)
MODES = {
    "patch": ("colour_patch", "S7t T6: mean colour of one rectangle per screenshot (paint hue SE vs Subnautica)"),
    "shadow": ("shadow_ratio", "S7t T5: shadow contrast, a shadowed vs a lit face per screenshot (SE vs Subnautica)"),  # S7t-look-match
    "bands": ("band_compare", "S7t T2: sky / horizon / far and near water bands, SE vs Subnautica (or one vs the measured table)"),  # S7t-sn-altitude
}

def _load(module):
    here = os.path.dirname(os.path.abspath(__file__))
    if here not in sys.path:
        sys.path.insert(0, here)  # python -I does not add the script's folder
    return importlib.import_module(module)

def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv and argv[0] == "--selftest":
        worst = 0
        for name in sorted(MODES):
            worst = max(worst, _load(MODES[name][0]).main(["--selftest"]))
        return worst
    if not argv or argv[0] not in MODES:
        print("usage: python -I tests/s7t/compare.py MODE ...  (or --selftest)")
        for name in sorted(MODES):
            print("  %-8s %s" % (name, MODES[name][1]))
        return 2
    return _load(MODES[argv[0]][0]).main(argv[1:])

if __name__ == "__main__":
    sys.exit(main())
