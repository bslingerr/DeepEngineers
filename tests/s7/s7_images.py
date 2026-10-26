"""S7 image checks under the name tests/s7/analyze_s7.py imports (``import s7_images``; ``probe_rgb(path, x, y, radius)``).

The implementation is tests/s7/s7_interior_check.py; this module only re-exports it so the analyzer finds it by name. Stdlib only.
"""

import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from s7_interior_check import (  # noqa: E402,F401
    FOG_A_MIN, FOG_B_MAX, RATIO_TOL, fog_check, light_ratio, luminance_srgb, mad, probe_rgb, read_png, suggest_scale, write_png,
)

if __name__ == "__main__":
    import s7_interior_check
    sys.exit(s7_interior_check.main())
