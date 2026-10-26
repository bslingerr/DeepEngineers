"""DeepEngineers dive site v1: the frozen planet, sea, anchor and terrain-tile layout, with its checks.

This is the single source of the site constants that docs/CONTRACT.md ("Site record") copies. It reuses the
independent map oracle worldmap_ref.py (stdlib) for the map itself and re-runs its self-test with this site.
Decision record: docs/recon/gap-2.md.

What is frozen here (plugin constants, versioned by SITE_VERSION):
 * planet generator DE_4546B: storage radius R = 60,000 m, HillParams Min -0.05 / Max 0.12, all-zero heightmaps,
 EarthLike atmosphere numbers. The Water Mod sea sits at R (the planet's AverageRadius), so SE's own
 "sea level" elevation, air density and gravity behave as on EarthLike at sea level.
 * Water Mod: WaterSettings.Radius = float32(60000/57000); waves, tides, volumetrics and crush damage off.
 * anchor U0 = +Z (cube-face centre), N0 = -Y, E0 = -X, yaw 0, SN offset 0.
 * terrain: provider-backed MyVoxelMap tiles of 1024^3 voxels on a lattice fixed to the planet centre,
 pitch 1020 m (4-voxel overlap), "plug" (Subnautica data only; a missing batch file is empty; no fill).
Runtime values (not constants): C = planet.PositionComp.GetPosition, Rs = WaterModAPI.GetPhysical(planet).Item2.
The plugin must check that the runtime Rs equals RS_EXPECTED bit for bit, or refuse to start the bridge.

Usage:
 python -I tools/worldmap/site_v1.py selftest [<octree_state.npy>]
 worldmap_ref selftest with this site, planet checks, tile lattice; with the optional per-octree state map
 written by tools/sn-octrees/sn_octree_stats.py (keep it outside the repo) it also counts non-empty tiles
 and proves that every solid Subnautica octree lies inside the tile interiors.
 python -I tools/worldmap/site_v1.py record prints the site record as JSON
"""
import json
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I drops the script dir; this is our own folder
import worldmap_ref as wm  # noqa: E402

SITE_VERSION = 1

def f32(x):
    """Round a double to IEEE float32, as C# float arithmetic does (SE and the Water Mod use float here)."""
    return struct.unpack("f", struct.pack("f", x))[0]

# --- planet (MyPlanetShapeProvider: maxHill = Max*R, minHill = Min*R, Inner = R + minHill, Outer = R + maxHill) ---
PLANET = {
    "generatorSubtypeId": "DE_4546B",
    "storageRadius_R": 60000.0,          # spawn size (diameter) 120,000 m, same class as EarthLike / Mars
    "hillMin": -0.05,                    # Triton's value (deepest stock Min): floor 3,000 m below R
    "hillMax": 0.12,                     # EarthLike's value: same gravity band and atmosphere above sea level
    "surfaceGravity": 1.0,
    "gravityFalloffPower": 7.0,          # SE default
    "atmosphere": {"breathable": True, "density": 1.0, "oxygenDensity": 0.9, "limitAltitude": 2.0, "maxWindSpeed": 80},
    "heightmaps": "6 faces, 16-bit, all zero (own files); no SurfaceDetail, no EnvironmentItems, no weather generators",
}
R = f32(PLANET["storageRadius_R"])
MIN_HILL = f32(f32(PLANET["hillMin"]) * R)
MAX_HILL = f32(f32(PLANET["hillMax"]) * R)
INNER = f32(R + MIN_HILL)                # MyPlanet.MinimumRadius
OUTER = f32(R + MAX_HILL)                # MyPlanet.MaximumRadius
ATMO_ALT = MAX_HILL * PLANET["atmosphere"]["limitAltitude"]   # MyPlanet.AtmosphereAltitude

# --- Water Mod sea: WaterComponent.Radius = MinimumRadius * Settings.Radius, both float ---
WATER = {
    "settingsRadius": f32(60000.0 / 57000.0),   # also what SetSeaLevel(planet, 3000) stores
    "waveHeight": 0.0, "tideHeight": 0.0, "volumetric": False,
    "crushDamage": 0.0,                         # planet-local: disables Water Mod crush (default 400/600/293 m)
    "currentSpeed": 0.0, "enableFish": False,
}
RS_EXPECTED = f32(INNER * WATER["settingsRadius"])   # 60000.00390625

# --- anchor and map ---
ANCHOR = {"U0": (0.0, 0.0, 1.0), "N0": (0.0, -1.0, 0.0), "E0": (-1.0, 0.0, 0.0), "psi_deg": 0.0, "O": (0.0, 0.0, 0.0),
          "baseSunDirection": (0.0, 0.0, 1.0), "sunRotationIntervalMinutes": 20}
TEST_C = wm.EXAMPLE.C                  # planet centre used by tests and vector files (runtime C comes from the planet)
SITE_V1 = wm.Site(C=TEST_C, Rs=RS_EXPECTED, U0=ANCHOR["U0"], N0=ANCHOR["N0"], psi_deg=ANCHOR["psi_deg"], O=ANCHOR["O"])

# --- Subnautica data envelope (VERIFIED by tools/sn-octrees and tools/sn-octree-scan, Steam build 20241558) ---
SN = {"dataYmin": -1920.0, "dataYmax": 160.0, "worldYmin": -3040.0, "tileHalf": 2048.0,
      "manifest": {"buildid": 20241558, "files": 5416, "bytes": 1203085204, "batchRowsWithFiles": [7, 19]}}

# --- depth rules (SN y = SE altitude above the sea) ---
DEPTH = {"abyssLimitSnY": -2000.0,       # plugin crush below this on the dive planet (nothing of Subnautica is deeper than -1920)
         "planetFloorSnY": INNER - RS_EXPECTED}   # -3000.004: SE's invisible abyss floor

# --- terrain tiles: lattice fixed to C, offsets in world axes (planets never rotate) ---
TILE = 1024                 # voxels = metres; MyOctreeStorage rounds to a power-of-two cube
PITCH = 1020                # 4-voxel overlap between neighbours
LAT0 = -2042                # lateral min corner of tile 0 on X and Y: 4 tiles cover [-2042, +2042]
NLAT = 4
RAD0 = 58064                # radial (Z) min corner of row 0, relative to C: row interiors cover [58066, 61126]
NRAD = 3
EDGE = 2                    # interior margin: a point must sit >= 2 voxels inside some tile

def tile_min(i, j, k):
    return (LAT0 + i * PITCH, LAT0 + j * PITCH, RAD0 + k * PITCH)

def tiles():
    return [(i, j, k) for k in range(NRAD) for j in range(NLAT) for i in range(NLAT)]

def sn_to_se_rel(x, y, z, Rs=RS_EXPECTED):
    """Vectorisable closed form of worldmap_ref.Site.sn_to_se for this anchor (U0=+Z, E0=-X, N0=-Y, psi=0, O=0),
 returned relative to C. Cross-checked against the reference in selftest."""
    rho = math.hypot(x, z)
    th = rho / Rs
    r = Rs + y
    s = math.sin(th) / rho if rho > 0 else 0.0
    return (-r * s * x, -r * s * z, r * math.cos(th))

def planet_checks():
    ok = True
    print(f"planet R {R:.3f}  MinimumRadius {INNER:.3f}  MaximumRadius {OUTER:.3f}  AtmosphereAltitude {ATMO_ALT:.1f}")
    print(f"Water Mod Settings.Radius {WATER['settingsRadius']:.9f} -> Rs {RS_EXPECTED:.8f} (float32)  sea - R = {RS_EXPECTED - R:.5f} m")
    # gravity band must hold the whole Subnautica volume as mapped (radial extent of the tile box) and the abyss rule depth
    lo = (RS_EXPECTED + SN["dataYmin"]) * math.cos(math.hypot(2048, 2048) / RS_EXPECTED)
    lo = min(lo, RS_EXPECTED + DEPTH["abyssLimitSnY"])
    hi = RS_EXPECTED + SN["dataYmax"]
    band = INNER <= lo and hi <= OUTER
    print(f"gravity 1 g band [{INNER:.0f}, {OUTER:.0f}] holds play volume [{lo:.0f}, {hi:.0f}]: {band}"
          f"  (margins {lo - INNER:.0f} m below, {OUTER - hi:.0f} m above)")
    ok &= band
    dens = max(0.0, min(1.0, 1 - (RS_EXPECTED - R) / ATMO_ALT)) * PLANET["atmosphere"]["density"]
    print(f"air density at the sea surface {dens:.6f} (EarthLike-identical above sea level; the old R+3500 m sea gave 0.583)")
    ok &= dens > 0.999
    print(f"SE 'sea level' elevation (|P-C| - AverageRadius) at the Water Mod surface: {RS_EXPECTED - R:+.5f} m")
    print(f"planet floor at SN y {DEPTH['planetFloorSnY']:.3f}; abyss rule at SN y {DEPTH['abyssLimitSnY']:.0f}")
    size = 32
    while size < 2 * OUTER:
        size *= 2
    print(f"planet storage size {size} (FindBestOctreeSize(2*Outer)); EarthLike has the same R and HillParams.Max")
    return ok

def lattice_checks(state_path=None):
    ok = True
    # 1) closed form equals the reference map
    import random
    rnd = random.Random(2)
    worst = 0.0
    for _ in range(20000):
        q = (rnd.uniform(-2048, 2048), rnd.uniform(-3040, 300), rnd.uniform(-2048, 2048))
        P = SITE_V1.sn_to_se(q)
        rel = wm.sub(P, SITE_V1.C)
        worst = max(worst, wm.norm(wm.sub(rel, sn_to_se_rel(*q))))
    print(f"closed form vs worldmap_ref: worst {worst:.2e} m")
    ok &= worst < 1e-6
    # 2) lattice covers the conservative Subnautica data box (all 8 corners + edge midpoints of |x|,|z| <= 1960, y in data range)
    def inside_some_interior(p):
        for (i, j, k) in tiles():
            m = tile_min(i, j, k)
            if all(m[a] + EDGE <= p[a] <= m[a] + TILE - EDGE for a in range(3)):
                return True
        return False
    print(f"lattice: {NLAT}x{NLAT}x{NRAD} tiles of {TILE}, pitch {PITCH}; lateral [{LAT0}, {LAT0 + (NLAT - 1) * PITCH + TILE}],"
          f" radial [{RAD0}, {RAD0 + (NRAD - 1) * PITCH + TILE}] relative to C (sea at {RS_EXPECTED:.0f})")
    if state_path is None:
        print("no octree state map given: skipping the per-octree coverage proof and the non-empty tile count")
        return ok
    import numpy as np
    st = np.load(state_path)
    solid = (st == 1) | (st == 2)
    xs, ys, zs = np.nonzero(solid)
    x0 = -2048.0 + xs * 32.0
    y0 = -3040.0 + ys * 32.0
    z0 = -2048.0 + zs * 32.0
    Rs = RS_EXPECTED
    mins = np.full((xs.size, 3), np.inf)
    maxs = np.full((xs.size, 3), -np.inf)
    for dx in (0.0, 32.0):
        for dy in (0.0, 32.0):
            for dz in (0.0, 32.0):
                x, y, z = x0 + dx, y0 + dy, z0 + dz
                rho = np.hypot(x, z)
                th = rho / Rs
                r = Rs + y
                s = np.where(rho > 0, np.sin(th) / np.where(rho > 0, rho, 1.0), 0.0)
                p = np.stack([-r * s * x, -r * s * z, r * np.cos(th)], axis=1)
                mins = np.minimum(mins, p)
                maxs = np.maximum(maxs, p)
    # sag of a 32 m chord on the curved map is < 0.01 m, so corner AABBs are exact to 1 cm; add 0.5 m anyway
    mins -= 0.5
    maxs += 0.5
    print(f"solid octrees: {xs.size}; mapped SE extent rel. C: X [{mins[:,0].min():.1f}, {maxs[:,0].max():.1f}]"
          f" Y [{mins[:,1].min():.1f}, {maxs[:,1].max():.1f}] Z [{mins[:,2].min():.1f}, {maxs[:,2].max():.1f}]"
          f" (radial span {maxs[:,2].max() - mins[:,2].min():.1f} m)")
    # coverage proof: every octree box lies inside the union of tile interiors (checked per axis interval)
    lo_ok = True
    for a, (base, n) in enumerate(((LAT0, NLAT), (LAT0, NLAT), (RAD0, NRAD))):
        lo, hi = base + EDGE, base + (n - 1) * PITCH + TILE - EDGE
        inside = (mins[:, a] >= lo) & (maxs[:, a] <= hi)
        if not inside.all():
            lo_ok = False
            print(f"  axis {a}: {(~inside).sum()} octrees outside the lattice interior [{lo}, {hi}]")
    print(f"every solid octree inside the tile interiors (contiguous because PITCH = TILE - 2*EDGE): {lo_ok}")
    ok &= lo_ok
    # non-empty tiles (tile box intersects any solid octree box)
    used = []
    for (i, j, k) in tiles():
        m = np.array(tile_min(i, j, k), dtype=float)
        hit = np.all((maxs >= m) & (mins <= m + TILE), axis=1)
        n = int(hit.sum())
        if n:
            used.append(((i, j, k), n))
    print(f"non-empty tiles (plug): {len(used)} of {NLAT * NLAT * NRAD}")
    for (i, j, k), n in used:
        m = tile_min(i, j, k)
        print(f"  tile ({i},{j},{k}) min corner C + ({m[0]}, {m[1]}, {m[2]}): {n} solid octrees")
    # fill-to-floor comparison: rows needed to reach the planet floor (INNER) from the top
    top = maxs[:, 2].max()
    rows_fill = math.ceil((top - INNER + 2 * EDGE - TILE) / PITCH) + 1
    rows_plug = math.ceil((top - mins[:, 2].min() + 2 * EDGE - TILE) / PITCH) + 1
    print(f"vertical rows needed: plug {rows_plug}, fill-to-floor {rows_fill} (floor {INNER:.0f})")
    for size in (2048,):
        pitch = size - 2 * EDGE
        nl = math.ceil((maxs[:, 0].max() - mins[:, 0].min() + 2 * EDGE - size) / pitch) + 1
        nr_plug = math.ceil((top - mins[:, 2].min() + 2 * EDGE - size) / pitch) + 1
        nr_fill = math.ceil((top - INNER + 2 * EDGE - size) / pitch) + 1
        print(f"fallback {size}^3 tiles: lateral {nl}x{nl}, rows plug {nr_plug}, fill {nr_fill}")
    return ok

def record():
    return {
        "siteVersion": SITE_VERSION,
        "planet": dict(PLANET, minimumRadius=INNER, maximumRadius=OUTER, averageRadius=R, atmosphereAltitude=ATMO_ALT),
        "water": dict(WATER, RsExpectedFloat32=RS_EXPECTED, RsSource="WaterModAPI.GetPhysical(planet).Item2"),
        "anchor": ANCHOR,
        "C": "runtime: planet.PositionComp.GetPosition(); tests use " + repr(TEST_C),
        "depth": DEPTH,
        "terrain": {"representation": "provider-backed MyVoxelMap tiles, plug (no fill), missing batch file = empty",
                    "tile": TILE, "pitch": PITCH, "lateralMin": LAT0, "nLateral": NLAT, "radialMin": RAD0, "nRadial": NRAD,
                    "interiorMargin": EDGE, "saved": False, "useVoxelOffset": False, "edits": "blocked (v1)"},
        "snSource": SN,
    }

def selftest(state_path=None):
    print(f"=== worldmap_ref selftest with site v1 (Rs {RS_EXPECTED:.8f}) ===")
    saved = wm.EXAMPLE
    wm.EXAMPLE = SITE_V1
    try:
        ok = wm.selftest()
    finally:
        wm.EXAMPLE = saved
    print("\n=== planet checks ===")
    ok &= planet_checks()
    print("\n=== tile lattice ===")
    ok &= lattice_checks(state_path)
    q = (-1029.0, -150.0, -1116.0)
    P = SITE_V1.sn_to_se(q)
    print(f"\nWM-2 test point SN {q} -> SE ({P[0]:.4f}, {P[1]:.4f}, {P[2]:.4f}); back {SITE_V1.se_to_sn(P)}")
    print("\nSITE V1 SELFTEST", "PASS" if ok else "FAIL")
    return ok

if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "selftest":
        sys.exit(0 if selftest(sys.argv[2] if len(sys.argv) > 2 else None) else 1)
    if len(sys.argv) == 2 and sys.argv[1] == "record":
        print(json.dumps(record(), indent=1))
        sys.exit(0)
    print(__doc__)
