"""The roadmap's name for the sculpt tool (ROADMAP "### S8a": `tools/planet/sculpt_planet.py`): runs
tools/planet/sculpt_de4546b.py with the same arguments. Since Stage C the planet is authored once in
`tools/planet/layout_4546b.json` (it replaced the noise rules `sculpt_rules.json`); --seed varies the detail layer only.

 python -I tools/planet/sculpt_planet.
 python -I tools/planet/sculpt_planet.py --verify <modDir> S8a.C2 on the written files
 python -I tools/planet/sculpt_planet.py --depth-at X Z [--files <modDir>] SE's seabed at an SN column (the c2g expectation)
 python -I tools/planet/sculpt_planet.py --native-check [<modDir>] S8a.C2.native: SE's own voxel generator on the files
 python -I tools/planet/sculpt_planet.py --check-world <save> read-only: grids the new seabed would bury
 python -I tools/planet/sculpt_planet.py --dry-run compute and report, write nothing
 python -I tools/planet/sculpt_planet.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sculpt_de4546b  # noqa: E402

if __name__ == "__main__":
    sys.exit(sculpt_de4546b.main())
