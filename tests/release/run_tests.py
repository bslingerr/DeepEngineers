"""S9 installer tests: python -I tests/release/run_tests.py [-v] [pattern]

Local fixtures only (fake Steam libraries, fake game folders with version files, fake Workshop folders, a fake
Pulsar/BepInEx layout, zips built in a temporary folder and "downloaded" through file:// URLs). No network, no game,
nothing written outside the temporary folders except the read-only checks of the repository itself (pins against the
C# sources, the committed template world, a trial release folder under a temporary directory).
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
for p in (HERE, os.path.join(REPO, "src", "DeepEngineers.Launcher"), os.path.join(REPO, "tools", "release"),
          os.path.join(REPO, "tests", "wire")):
    if p not in sys.path:
        sys.path.insert(0, p)

def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    verbosity = 2 if "-v" in sys.argv else 1
    loader = unittest.TestLoader()
    suite = loader.discover(HERE, pattern=args[0] if args else "test_*.py", top_level_dir=HERE)
    result = unittest.TextTestRunner(verbosity=verbosity).run(suite)
    return 0 if result.wasSuccessful() else 1

if __name__ == "__main__":
    sys.exit(main())
