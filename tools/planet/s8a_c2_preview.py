"""S8a Stage C2: previews of the design-4 layout (tools/planet/layout_4546b_d5.json: no moat, the Crater Edge's
trough rising to the shelf, a planet mostly 40-120 m deep, the crater's inner floor continuous across the Subnautica map's
border, island materials on all land) against the deployed design 3 (tools/planet/layout_4546b.json), for the project's look
before the sculpt is regenerated and deployed.

Writes into --out (default tests/out/s8a/stageC2-preview/):
 c2-depth-face1.png face 1 around the site (design 5 left, design 3 right): depth colours and hillshade
 c2-depth-globe.png the whole planet, four orthographic views, design 5
 c2-orbit-globe.png from orbit: the seabed materials (their far textures' mean colours) under the water, tinted by depth;
 design 5 with the proposed palette (top) against design 3 as deployed (bottom)
 c2-orbit-face1.png the same over 60 km around the site, the Subnautica tiles composited (their depth-band materials)
 c2-seam.png the crater from 8 km up (8 m pixels): the tiles over the planet, design 5 left, design 3 right
 c2-histogram.png the seabed's depth histogram (area-weighted; the ring excluded), design 5 against design 3
 c2-sections.png three sections from the site out to 30 km (design 5 solid, design 3 grey) + two through the tile seam
 c2-planet-stamped.png the stamped 3D field (Stage F2's brushes and 3D rules) on two planet biomes outside the ring
 c2-numbers.json the numbers of the report (histograms, sections, seam, islands, densities)
Inputs read in place (read-only): the brush store (s8a_brushes.py), and with --sn-scan DIR the 4 m column maps of
tools/sn-octree-scan (top.i16, lowfloor.i16: derived from the install, kept outside the repository) for the tiles and the
cap; --sn-density FILE: the per-biome census of Subnautica's baked entities (scratch). Nothing is written into the world mod.

Usage: python tools/planet/s8a_c2_preview.py [--out DIR] [--globe-res 512] [--sn-scan DIR] [--sn-density FILE]
"""
import argparse
import json
import math
import os
import sys
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
import sculpt_de4546b as SC  # noqa: E402
import s8a_ring3d as R3  # noqa: E402
import s8a_brushes as BR  # noqa: E402
import sn_rim as SR  # noqa: E402

D3 = os.path.join(HERE, "layout_4546b.json")
D4 = os.path.join(HERE, "layout_4546b_d5.json")   # the new design
BANDS = ((-1e9, 0.0, "land"), (0.0, 40.0, "0-40 m"), (40.0, 120.0, "40-120 m (shelf)"), (120.0, 150.0, "120-150 m"), (150.0, 400.0, "150-400 m (reefs, slopes)"),
         (400.0, 550.0, "400-550 m"), (550.0, 1100.0, "550-1,100 m (deep basins, trough)"), (1100.0, 2000.0, "1,100-2,000 m (trenches)"),
         (2000.0, 1e9, "> 2,000 m (void floor)"))
AZIMUTHS = (35.0, 160.0, 280.0)              # SN headings (0 = north = +z, 90 = east = +x) of the three sections
SEAM_AZ = (0.0, 45.0)                        # the tile-seam sections: across an edge's middle, through a corner
OWNER_SPOT = (12683.0, 12285.0)              # SN (site v1) under the project's camera at 17.6 km, free play
SN_NONE = -32768
CAP_MARGIN_M = 30.0                          # layout_4546b_d5.json crater.capMarginM
RIM_FILL_M = 300.0                           # design 5: the open columns of the rim's last 300 m may be buried by the continued floor
CAP_FOOTPRINT_M = 96.0                       # the min-filter radius: the Catmull-Rom 4 x 4 patch of 46 m pixels (a pixel weighs on columns 2 pixels away)

# Mean colours of the materials' distance / far colour textures:
# what SE draws from far away. The asteroid stones (Stone_01..05, VoxelMaterials_asteroids.sbc) have no Far textures; their
# "_distance" variants are pale grey and blue-grey: the project's "ice, clouds".
FAR = {
    "Sand_02": (185, 148, 100), "Stone_01": (141, 138, 136), "Stone_02": (114, 128, 143), "Stone_03": (56, 56, 60), "Stone_04": (71, 71, 83),
    "Stone_05": (104, 104, 116), "Grass": (86, 102, 45), "Stone": (94, 93, 92), "Soil": (118, 103, 84), "DesertRocks": (173, 130, 96),
    "AlienRockyMountain": (105, 86, 81), "AlienSoil": (33, 25, 17), "MoonRocks": (99, 85, 74), "Snow": (228, 236, 244),
    "Iron_02": (108, 79, 77), "Nickel_01": (126, 123, 109), "Silicon_01": (93, 93, 91),
}
# Design 5's palette: the table's R values keep their roles (the biome rules and the window's Subnautica types are keyed on
# them); the SE material behind each value becomes a planetary one with a far texture that reads as seabed rock, so nothing
# reads as ice from orbit (the.sbc CustomMaterialTable rows, the tiles' MaterialBands names and S8aMaterialMap.Subtypes follow
# at regeneration: ext s8a terrain bump).
D4_PALETTE = {"Sand_02": "Sand_02", "Stone_01": "Stone", "Stone_02": "Soil", "Stone_03": "DesertRocks", "Stone_04": "AlienRockyMountain",
              "Stone_05": "AlienSoil", "Grass": "Grass"}
WATER = np.array([13.0, 46.0, 64.0])         # the Water Mod's fog colour (0.05, 0.18, 0.25)
VIS_M = 60.0                                 # the seabed fades into the water colour over this depth (exp)
NAME_OF_VALUE = {v: n for n, v in SC.MATERIALS}
ISLAND_OK = {"Grass", "Stone_01"}
ISLAND_ROCK_SLOPE = 0.55                     # layout island.rockSlopeMin # design 5's island materials (grass, the island rock: layout island.rockMaterial)

def design_of(path):
    return SC.Design(SC.load_layout(path), SC.DEFAULT_SEED)

def area_weights(res):
    a = 2.0 * np.arange(res) / (res - 1) - 1.0
    A, B = np.meshgrid(a, a)
    return 1.0 / (1.0 + A * A + B * B) ** 1.5

def histogram(faces, ring_out=True):
    """{band: share of the seabed area} over all faces (the site's crater box excluded: tiles), optionally without the ring."""
    res = faces[0].heights.shape[0]
    w = area_weights(res)
    acc = np.zeros(len(BANDS))
    for fr in faces:
        y = SC.decode_height(fr.heights)
        d = -y
        m = np.ones(d.shape, bool)
        if fr.face == 1:
            x, yy, z = SC.face_dirs(1, res)
            rho = SC.dirs_rho(x, yy, z)
            m &= rho > (13050.0 if ring_out else 2900.0)
        for k, (lo, hi, _n) in enumerate(BANDS):
            acc[k] += float(w[m & (d >= lo) & (d < hi)].sum())
    acc /= acc.sum()
    return {name: float(acc[k]) for k, (_lo, _hi, name) in enumerate(BANDS)}

def section(base, az, r1=30000.0, step=20.0, r0=0.0):
    """(rho, seabed y, rule key) along an SN heading from the site (face-1 Catmull-Rom over the sculpt's heights)."""
    rs = np.arange(r0, r1, step)
    x = rs * math.sin(math.radians(az))
    z = rs * math.cos(math.radians(az))
    H, K = base.column(x, z)
    return rs, H, K

# ------------------------------------------------------------------------------------------------- the Subnautica tiles
class SnScan(SR.SnCap):
    """The install's column maps with design 5's cap (tools/planet/sn_rim.py SnCap), plus the slope of the top surface."""

    def __init__(self, folder):
        super().__init__(folder)
        t = np.where(np.isfinite(self.top), self.top, -3000.0)
        gz, gx = np.gradient(t, 4.0)
        self.slope = np.hypot(gx, gz)

    def slope_at(self, x, z):
        i, j, ok = self.index(x, z)
        return np.where(ok, self.slope[j, i], 0.0)

def tile_band_name(y, design4, slope=None):
    """The tiles' depth band material (TileSampler MaterialBands); design 5: land (y >= 0) is grass, or the island rock where the
 slope is >= the layout's rockSlopeMin (the island rule)."""
    out = np.full(np.shape(y), SC.TILE_BANDS[-1][1], object)
    for lo, name in reversed(SC.TILE_BANDS[:-1]):
        out = np.where(y >= lo, name, out)
    if design4:
        land = np.where(slope >= ISLAND_ROCK_SLOPE, "Stone_01", "Grass") if slope is not None else "Grass"
        out = np.where(y >= 0.0, land, out)
    return out

def orbit_colour(names, y, design4, shade=None):
    """The colour seen from orbit: the material's far colour (design 5: through the palette), faded into the water by depth."""
    flat = np.asarray(names, object).ravel()
    uniq, inv = np.unique(flat.astype(str), return_inverse=True)
    lut = np.array([FAR[D4_PALETTE.get(n, n) if design4 else n] for n in uniq], np.float64)
    cols = lut[inv].reshape(np.shape(names) + (3,))
    if shade is not None:
        cols = cols * shade[..., None]
    depth = np.maximum(0.0, -np.asarray(y))
    a = (1.0 - np.exp(-depth / VIS_M))[..., None]
    return cols * (1.0 - a) + WATER * a

def names_of(mats):
    lut = np.array([NAME_OF_VALUE.get(v, "Stone_05") for v in range(256)], object)
    return lut[mats.astype(np.intp)]

# ------------------------------------------------------------------------------------------------- the stamped planet sheet
def place_patch(R, x0, z0, size):
    """Ring3D.place's rule on the cells of one square patch (any rho): the planet beyond the ring, by its biome map's G."""
    cs = R3.STAMP_CELL_M
    a0, a1 = int(math.floor(x0 / cs)), int(math.ceil((x0 + size) / cs))
    b0, b1 = int(math.floor(z0 / cs)), int(math.ceil((z0 + size) / cs))
    cells = [(a, b) for a in range(a0, a1) for b in range(b0, b1)]
    X = np.array([(a + 0.2 + 0.6 * R3.hash_u(R.seed, a, b, 1)) * cs for a, b in cells])
    Z = np.array([(b + 0.2 + 0.6 * R3.hash_u(R.seed, a, b, 2)) * cs for a, b in cells])
    H, K = R.base.column(X, Z)
    by_class = {}
    for br in R.lib.brushes:
        by_class.setdefault(br["class"], []).append(br)
    placed, grid = [], {}
    cell_r = int(math.ceil(R3.REPEAT_M / cs))
    for k, (cx, cz) in enumerate(cells):
        key = R3.KEYS[K[k]] if K[k] >= 0 else None
        dens, classes, _r = R3.RULE_OF.get(key, (0, (), ()))
        if not classes or R3.hash_u(R.seed, cx, cz, 3) >= dens / 1000.0 or H[k] > R3.STAMP_ABOVE_Y:
            continue
        own = [b for c in classes for b in by_class.get(c, []) if b["biome"] == key]
        pool = own if len(own) >= 3 else own + [b for c in classes for b in by_class.get(c, []) if b["biome"] != key]
        if not pool:
            continue
        pool = sorted(pool, key=lambda b: b["id"])
        off = int(R3.hash_u(R.seed, cx, cz, 7) * len(pool))
        pool = pool[off:] + pool[:off]
        xk, zk = float(X[k]), float(Z[k])
        near = set()
        for dx in range(-cell_r, cell_r + 1):
            for dz in range(-cell_r, cell_r + 1):
                for j in grid.get((cx + dx, cz + dz), ()):
                    p = placed[j]
                    if math.hypot(p["x"] - xk, p["z"] - zk) < R3.REPEAT_M:
                        near.add(p["brush"])
        choice = next((b for b in pool if b["id"] not in near), None)
        if choice is None:
            continue
        lo, hi = R3.SCALE_64 if choice["size"] >= 64 else R3.SCALE_32
        s = lo / 1000.0 + (hi / 1000.0 - lo / 1000.0) * R3.hash_u(R.seed, cx, cz, 4)
        ya, yb = 2.0 * R3.hash_u(R.seed, cx, cz, 5) - 1.0, 2.0 * R3.hash_u(R.seed, cx, cz, 8) - 1.0
        yl = math.sqrt(ya * ya + yb * yb)
        cy, sy = (ya / yl, yb / yl) if yl >= 1e-3 else (1.0, 0.0)
        g = R.lib.ground[choice["id"]]
        p = {"i": len(placed), "x": xk, "z": zk, "h": float(H[k]), "y0": float(H[k]) - g * s - R3.SINK_M, "cos": cy, "sin": sy,
             "mirror": R3.hash_u(R.seed, cx, cz, 6) < 0.5, "scale": s, "brush": choice["id"], "size": choice["size"], "ground": g,
             "class": choice["class"], "biome": key}
        grid.setdefault((cx, cz), []).append(len(placed))
        placed.append(p)
    return placed

def find_patch(base, key, size, rmin=14000.0, rmax=40000.0, step=400.0):
    """The first patch (scan by rings, then heading) whose 9 probe columns all carry the rule key and lie below -6 m."""
    k = R3.KEYS.index(key)
    for r in np.arange(rmin, rmax, step):
        for az in np.arange(0.0, 360.0, 5.0):
            cx, cz = r * math.sin(math.radians(az)), r * math.cos(math.radians(az))
            px = np.array([cx - size / 2 + size * u for u in (0.1, 0.5, 0.9) for _v in range(3)])
            pz = np.array([cz - size / 2 + size * v for _u in range(3) for v in (0.1, 0.5, 0.9)])
            H, K = base.column(px, pz)
            if np.all(K == k) and np.all(H < -10.0):
                return cx - size / 2, cz - size / 2
    return None

def stamped_sheet(base, out, log=print):
    """The F2 field over two planet biomes outside the ring: per biome a 480 m relief (4 m) with its placements, and four 96 m tiles."""
    from PIL import Image, ImageDraw  # noqa: E402
    lib = R3.Library.load()
    if lib is None:
        return None, {}
    R = R3.Ring3D(base=base, library=lib)
    fade = R3.FADE
    R3.FADE = (0.0, 1.0, 1e12, 1e12 + 1.0)       # the 3D rules everywhere (the planet-wide plan), not only the ring
    info = {}
    rows = []
    try:
        for planet_key, rule_key in (("planetBasin", "grandReef"), ("planetPlains", "sparseReef"), ("planetTrench", "bloodKelp")):
            size = 480.0
            at = find_patch(base, rule_key, size)
            if at is None:
                log("stamped sheet: no %s patch found" % planet_key)
                continue
            x0, z0 = at
            pl = place_patch(R, x0 - 64.0, z0 - 64.0, size + 128.0)
            R.placements = pl
            R.grid = {}
            for p in pl:
                R.grid.setdefault((int(math.floor(p["x"] / R3.GRID_M)), int(math.floor(p["z"] / R3.GRID_M))), []).append(p["i"])
            n = int(size / 4.0)
            Hc, _k = base.column(np.array([x0 + size / 2]), np.array([z0 + size / 2]))
            Hall, _k2 = base.column(np.repeat(x0 + (np.arange(n) + 0.5) * 4.0, n), np.tile(z0 + (np.arange(n) + 0.5) * 4.0, n))
            ybot = float(np.min(Hall)) - 60.0
            ny = int(math.ceil((float(np.max(Hall)) + 80.0 - ybot) / 4.0))
            xs, zs, ys, f, Hb, _kk, wout = R.field_box(x0, ybot, z0, n, ny, n, 4.0, weight=True)
            s_ = R.surface(f, ys)
            sh = R3._shade(s_, 4.0)
            hcol = SC._depth_colour(-np.nan_to_num(s_, nan=-3000.0)) * sh[..., None]
            wm = wout.max(axis=2)
            hcol = hcol * (1.0 - 0.3 * wm[..., None]) + np.array([255, 160, 60]) * 0.3 * wm[..., None]
            relief = np.transpose(hcol, (1, 0, 2))[::-1]
            relief = np.repeat(np.repeat(relief, 4, axis=0), 4, axis=1)            # 480 px
            tiles = []
            used = set()
            inside = [p for p in pl if x0 + 48 <= p["x"] <= x0 + size - 48 and z0 + 48 <= p["z"] <= z0 + size - 48]
            inside.sort(key=lambda p: BR._hash(R.seed + 7, int(p["x"]), int(p["z"])))
            for c in inside:
                if c["brush"] in used or len(tiles) >= 4:
                    continue
                used.add(c["brush"])
                bx0, bz0 = c["x"] - 48.0, c["z"] - 48.0
                xs2, zs2, ys2, f2, _H2, _k2, w2 = R.field_box(bx0, c["h"] - 70.0, bz0, 96, 150, 96, 1.0, weight=True)
                s2 = R.surface(f2, ys2)
                tcol = SC._depth_colour(-np.nan_to_num(s2, nan=-3000.0)) * R3._shade(s2, 1.0)[..., None]
                wm2 = w2.max(axis=2)
                tcol = tcol * (1.0 - 0.25 * wm2[..., None]) + np.array([255, 160, 60]) * 0.25 * wm2[..., None]
                top = np.transpose(tcol, (1, 0, 2))[::-1]
                sl = f2[:, 48, :] > 0
                slc = np.where(sl[..., None], np.array([120, 105, 90]), np.array([40, 90, 150])).astype(np.float64)
                slice_img = np.transpose(slc, (1, 0, 2))[::-1]
                tile = np.concatenate([top, np.full((4, 96, 3), 255.0), slice_img], axis=0)      # 96 x 250
                tiles.append((c, np.repeat(np.repeat(tile, 1, axis=0), 1, axis=1)))
            classes = {}
            for p in pl:
                if x0 <= p["x"] <= x0 + size and z0 <= p["z"] <= z0 + size:
                    classes[p["class"]] = classes.get(p["class"], 0) + 1
            rock_share = float((f > 0).mean())
            info[planet_key] = {"rule": rule_key, "patchSW": [round(x0), round(z0)], "sizeM": size, "placements": int(sum(classes.values())),
                                "perKm2": round(sum(classes.values()) / (size * size / 1e6), 1), "classes": classes,
                                "seabedY": round(float(Hc[0]), 1), "rho": round(math.hypot(x0 + size / 2, z0 + size / 2))}
            # one row: relief (240 x 240), then the tiles (96 x 250 each, scaled x 2.5 -> 240 x 625? keep x1: 96 wide)
            row_h = 500
            row = np.full((row_h, 480 + 8 + 4 * (96 * 2 + 8), 3), 245.0)
            row[:480, :480] = relief
            for k, (c, t) in enumerate(tiles):
                t2 = np.repeat(np.repeat(t, 2, axis=0), 2, axis=1)[:row_h, :]
                xo = 488 + k * (96 * 2 + 8)
                row[:t2.shape[0], xo:xo + t2.shape[1]] = t2
            rows.append((planet_key, rule_key, info[planet_key], tiles, row))
    finally:
        R3.FADE = fade
    if not rows:
        return None, info
    W = max(r[4].shape[1] for r in rows)
    canvas = np.full((sum(r[4].shape[0] + 40 for r in rows) + 30, W, 3), 245.0)
    yo = 30
    labels = []
    for planet_key, rule_key, inf, tiles, row in rows:
        canvas[yo + 20:yo + 20 + row.shape[0], :row.shape[1]] = row
        labels.append((yo, "%s (Subnautica rules: %s) at SN (%d, %d), %.1f km from the site, seabed %.0f m: %d placements on 480 m (%s); relief (4 m voxels), then up to 4 brushes (96 m, 1 m voxels: top view over a vertical slice; brown = rock)" % (
            planet_key, rule_key, inf["patchSW"][0] + 240, inf["patchSW"][1] + 240, inf["rho"] / 1000.0, inf["seabedY"], inf["placements"],
            ", ".join("%s %d" % kv for kv in sorted(inf["classes"].items())))))
        yo += row.shape[0] + 40
    im = Image.fromarray(np.clip(canvas, 0, 255).astype(np.uint8))
    dr = ImageDraw.Draw(im)
    dr.text((8, 8), "The stamped 3D field (Stage F2: the ring's brushes and 3D rules) on planet biomes outside the ring; orange tint = a stamped brush", fill=(0, 0, 0))
    for yo, text in labels:
        dr.text((8, yo + 4), text, fill=(0, 0, 0))
    im.save(out)
    return out, info

# ------------------------------------------------------------------------------------------------- drawing helpers
def draw_sections(secs3, secs4, seam3, seam4, sn, out):
    from PIL import Image, ImageDraw  # noqa: E402
    W, Hh, pad = 1500, 330, 60
    n = len(secs4) + len(seam4)
    img = Image.new("RGB", (W, Hh * n + 40), (250, 250, 250))
    dr = ImageDraw.Draw(img)
    panels = [(s3, s4, False) for s3, s4 in zip(secs3, secs4)] + [(s3, s4, True) for s3, s4 in zip(seam3, seam4)]
    for k, ((_az3, rs, h3, _k3), (az, _rs, h4, _k4), seam) in enumerate(panels):
        y0 = 20 + k * Hh
        ymax, ymin = (60.0, -1300.0) if seam else (30.0, -1300.0)
        r0, r1 = rs[0], rs[-1]
        sx = lambda r: pad + (W - 2 * pad) * (r - r0) / (r1 - r0)   # noqa: E731
        sy = lambda v: y0 + 20 + (Hh - 60) * (ymax - v) / (ymax - ymin)   # noqa: E731
        dr.rectangle([pad, sy(0.0), W - pad, sy(ymin)], fill=(205, 225, 240))
        for gv in (0.0, -40.0, -120.0, -400.0, -600.0, -1000.0):
            dr.line([pad, sy(gv), W - pad, sy(gv)], fill=(170, 190, 210))
            dr.text((4, sy(gv) - 6), "%d m" % gv, fill=(60, 60, 60))
        stepk = 500.0 if seam else 2000.0
        for rk in np.arange(math.ceil(r0 / stepk) * stepk, r1 + 1, stepk):
            dr.line([sx(rk), sy(ymax), sx(rk), sy(ymin)], fill=(225, 225, 225))
            dr.text((sx(rk) - 8, sy(ymin) + 4), "%.1f km" % (rk / 1000.0) if seam else "%d km" % (rk / 1000), fill=(60, 60, 60))
        p3 = [(sx(r), sy(max(ymin, v))) for r, v in zip(rs, h3) if np.isfinite(v)]
        p4 = [(sx(r), sy(max(ymin, v))) for r, v in zip(rs, h4) if np.isfinite(v)]
        poly = p4 + [(sx(rs[-1]), sy(ymin)), (sx(rs[0]), sy(ymin))]
        dr.polygon(poly, fill=(120, 100, 80))
        dr.line(p4, fill=(40, 30, 20), width=2)
        dr.line(p3, fill=(235, 235, 235), width=3)
        dr.line(p3, fill=(110, 110, 110), width=1)
        if seam and sn is not None:
            x = rs * math.sin(math.radians(az))
            z = rs * math.cos(math.radians(az))
            t = sn.top_at(x, z)
            pts = [(sx(r), sy(max(ymin, v))) for r, v in zip(rs, t) if np.isfinite(v)]
            for (u, v) in pts:
                dr.line([u, v, u, sy(ymin)], fill=(80, 150, 90))
            edge = 2048.0 / max(abs(math.sin(math.radians(az))), abs(math.cos(math.radians(az))))
            dr.line([sx(edge), sy(ymax), sx(edge), sy(ymin)], fill=(200, 30, 30), width=2)
            dr.text((sx(edge) + 4, sy(ymax) + 2), "the Subnautica map's border (tiles end)", fill=(200, 30, 30))
            dr.text((pad, y0 - 14), "the tile seam, heading %.0f deg: Subnautica's terrain on the tiles (green: the top solid voxel per 4 m column), "
                    "the planet design 5 (brown) and design 3 (grey: -3,000 m inside, below the frame)" % az, fill=(0, 0, 0))
        else:
            for x_, lab in ((300.0, "crater (SN map on tiles)"), (8000.0, "ring"), (11300.0, "Crater Edge"), (12900.0, "trough"), (14700.0, "rise"),
                            (20000.0, "planet shelf")):
                dr.text((sx(x_) + 3, y0 + 4), lab, fill=(20, 20, 120))
            dr.text((pad, y0 - 14), "heading %.0f deg: design 5 (brown) against the deployed design 3 (grey line; its floors at -3,000 m are below the frame); depth 0 = the sea surface" % az, fill=(0, 0, 0))
    img.save(out)

def crater_view(fr, design4, sn, half=4000.0, pitch=8.0):
    """The crater from above (SN x east right, z north up): the visible surface (the tiles over the planet) in orbit colours."""
    base = R3.Base(fr.heights, fr.biomes)
    xs = np.arange(-half, half, pitch) + pitch / 2.0
    X, Z = np.meshgrid(xs, xs[::-1])
    H, _K = base.column(X.ravel(), Z.ravel())
    H = H.reshape(X.shape)
    px, py = R3.texel(X, Z)
    mats = fr.materials[(py + 0.5).astype(np.int64), (px + 0.5).astype(np.int64)]
    names = names_of(mats)
    vis_y = H.copy()
    if sn is not None:
        t = sn.top_at(X, Z)
        tile = np.isfinite(t) & (t > H)
        vis_y = np.where(tile, t, H)
        names = np.where(tile, tile_band_name(np.where(tile, t, 0.0), design4, sn.slope_at(X, Z)), names)
    shade = np.clip(0.55 + 0.45 * SC._hillshade(vis_y, pitch), 0.2, 1.2)
    return orbit_colour(names, vis_y, design4, shade), vis_y, H

def continuity(base, sn, r0=2000.0, r1=30000.0, dr=50.0, dth=1.0):
    """Design 5's continuity rule on face 1, over the visible seabed (the tiles over the planet inside the map): the azimuthal mean
 depth per 250 m annulus (a ring = an annulus 100 m deeper than the shallowest annulus within 3 km on each side), the km-scale
 radial gradient (the depth averaged over 500 m along each heading; |d depth / dr| per km)."""
    rs = np.arange(r0, r1, dr)
    th = np.radians(np.arange(0.0, 360.0, dth))
    R, T = np.meshgrid(rs, th)
    X, Z = R * np.sin(T), R * np.cos(T)
    H, _k = base.column(X.ravel(), Z.ravel())
    H = H.reshape(R.shape)
    if sn is not None:
        t = sn.top_at(X, Z)
        H = np.where(np.isfinite(t) & (t > H), t, H)
    D = -H
    ann = {}
    step = int(250.0 / dr)
    means = np.array([float(np.mean(D[:, k:k + step])) for k in range(0, rs.size, step)])
    centres = rs[::step][:means.size] + 125.0
    rings = []
    # a ring: an annulus 100 m deeper than the shallowest annulus within 3 km on its inner side AND on its outer side, in at
 # least 10 of 12 sectors of 30 deg (continuous all round: a dark ring; a deep biome or two in the mosaic is not one)
    nsec = 12
    per = th.size // nsec
    sec = np.array([[float(np.mean(D[q * per:(q + 1) * per, k:k + step])) for k in range(0, rs.size, step)] for q in range(nsec)])[:, :means.size]
    for k in range(means.size):
        hits = 0
        for q in range(nsec):
            lo = sec[q, max(0, k - 12):k]
            hi = sec[q, k + 1:k + 13]
            if lo.size and hi.size and sec[q, k] > lo.min() + 100.0 and sec[q, k] > hi.min() + 100.0:
                hits += 1
        if hits >= 10:
            rings.append({"rhoM": float(centres[k]), "meanDepthM": round(float(means[k]), 1), "sectors": hits})
    win = int(500.0 / dr)
    kern = np.ones(win) / win
    sm = np.array([np.convolve(row, kern, mode="valid") for row in D])
    grad = np.abs(np.diff(sm, axis=1)) / dr * 1000.0                      # m per km
    beyond = rs[:grad.shape[1]] >= 2500.0
    g = grad[:, beyond]
    return {"annulusMeans": [[round(float(c)), round(float(m), 1)] for c, m in zip(centres, means)], "rings": rings,
            "gradientPerKm": {"p50": round(float(np.percentile(g, 50)), 1), "p99": round(float(np.percentile(g, 99)), 1), "max": round(float(g.max()), 1),
                              "shareOver300": round(float((g > 300.0).mean()), 5), "shareOver500": round(float((g > 500.0).mean()), 5)}}

def draw_continuity(c3, c5, out):
    from PIL import Image, ImageDraw  # noqa: E402
    W, H, pad = 1500, 420, 60
    img = Image.new("RGB", (W, H), (250, 250, 250))
    dr = ImageDraw.Draw(img)
    a3, a5 = np.array(c3["annulusMeans"]), np.array(c5["annulusMeans"])
    rmax = float(a5[-1, 0])
    dmax = 3100.0
    sx = lambda r: pad + (W - 2 * pad) * r / rmax   # noqa: E731
    sy = lambda d: 40 + (H - 100) * d / dmax   # noqa: E731
    for gv in (0, 100, 300, 600, 1000, 2000, 3000):
        dr.line([pad, sy(gv), W - pad, sy(gv)], fill=(220, 225, 230))
        dr.text((4, sy(gv) - 6), "%d m" % gv, fill=(60, 60, 60))
    for rk in range(0, int(rmax / 1000) + 1, 2):
        dr.text((sx(rk * 1000.0) - 8, H - 50), "%d km" % rk, fill=(60, 60, 60))
    dr.line([(sx(r), sy(d)) for r, d in a3], fill=(150, 150, 150), width=2)
    dr.line([(sx(r), sy(d)) for r, d in a5], fill=(40, 90, 160), width=3)
    for rg in c3["rings"]:
        dr.ellipse([sx(rg["rhoM"]) - 4, sy(rg["meanDepthM"]) - 4, sx(rg["rhoM"]) + 4, sy(rg["meanDepthM"]) + 4], outline=(200, 30, 30))
    for rg in c5["rings"]:
        dr.ellipse([sx(rg["rhoM"]) - 5, sy(rg["meanDepthM"]) - 5, sx(rg["rhoM"]) + 5, sy(rg["meanDepthM"]) + 5], outline=(255, 0, 0), width=2)
    dr.text((pad, 10), "Mean seabed depth around the site per 250 m annulus (the visible seabed: tiles over the planet), design 5 (blue) against design 3 (grey); "
            "red circles: rings (an annulus 100 m deeper than the shallowest annulus within 3 km on both sides, in 10 of 12 sectors)", fill=(0, 0, 0))
    img.save(out)

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--out", default=os.path.join(REPO, "tests", "out", "s8a", "stageC2-preview"))
    ap.add_argument("--globe-res", type=int, default=512)
    ap.add_argument("--sn-scan", help="folder of tools/sn-octree-scan's top.i16 and lowfloor.i16 (derived from the install; outside the repo)")
    ap.add_argument("--sn-density", help="the per-biome census JSON of Subnautica's baked entities (scratch)")
    ap.add_argument("--skip-stamped", action="store_true")
    a = ap.parse_args(argv)
    os.makedirs(a.out, exist_ok=True)
    from PIL import Image, ImageDraw  # noqa: E402
    t0 = time.time()
    sn = SnScan(a.sn_scan) if a.sn_scan else None
    numbers = {}
    d3, d5 = design_of(D3), design_of(D4)
    if sn is not None:
        d5.sn_cap = sn.cap
        d5.sn_rim = sn.rim
        numbers["snScanHash"] = sn.hash
    # face 1 at full resolution (sections, the face view); all faces at globe-res (histogram, globe)
    f1 = {}
    for name, d in (("d3", d3), ("d5", d5)):
        f1[name] = SC.sculpt_face(1, d, SC.RESOLUTION, keep=True)
        print("face 1 %s %.0f s" % (name, time.time() - t0))
    glob = {}
    for name, d in (("d3", d3), ("d5", d5)):
        glob[name] = SC.sculpt_all(d, a.globe_res, keep=False, log=None)
        print("all faces %s at %d: %.0f s" % (name, a.globe_res, time.time() - t0))
    written = []
    stops = [("land", (160, 180, 115)), ("0 m", (150, 235, 230)), ("60 m", (70, 200, 215)), ("200 m", (30, 130, 190)), ("500 m", (20, 70, 150)),
             ("1,000 m", (15, 35, 100)), ("1,500 m", (10, 20, 60)), ("3,000 m (void floor)", (4, 6, 18))]
    # 1. face 1 around the site (site +-30 km), design 5 left, design 3 right
    res = SC.RESOLUTION
    half = int(30000.0 / (SC.RS * 2.0 / (res - 1)))
    c = res // 2
    tiles = []
    for name in ("d5", "d3"):
        y = SC.decode_height(f1[name].heights[c - half:c + half, c - half:c + half])
        img = SC._depth_colour(-y) * (0.45 + 0.55 * SC._hillshade(y, SC.RS * 2.0 / (res - 1))[..., None])
        tiles.append(img[::-1, ::-1])
    canvas = np.concatenate([tiles[0], np.full((tiles[0].shape[0], 8, 3), 250.0), tiles[1]], axis=1)
    p = os.path.join(a.out, "c2-depth-face1.png")
    SC._legend(canvas, stops, "60 km around the site, north up: design 5 left, design 3 right", p)
    written.append(p)
    # 2. the globe, design 5 (depth)
    gres = a.globe_res
    size = 560
    views = [((0, 0, 1), (0, -1, 0)), ((0, 0, -1), (0, -1, 0)), ((-1, 0, 0), (0, -1, 0)), ((1, 0, 0), (0, -1, 0))]
    imgs = {}
    for fr in glob["d5"]:
        y = SC.decode_height(fr.heights)
        imgs[fr.face] = SC._depth_colour(-y) * (0.45 + 0.55 * SC._hillshade(y, SC.RS * 2.0 / (gres - 1))[..., None])
    gl = np.zeros((2 * size, 2 * size, 3), np.float64)
    for k, (cc, u) in enumerate(views):
        gl[(k // 2) * size:(k // 2 + 1) * size, (k % 2) * size:(k % 2 + 1) * size] = SC.globe_view(imgs, cc, u, size)
    p = os.path.join(a.out, "c2-depth-globe.png")
    SC._legend(gl, stops, "design 5, the whole planet: the site (top left, north up), its antipode, the east and west limbs", p)
    written.append(p)
    # 3. from orbit: materials under the water (design 5 with its palette on top, design 3 as deployed below)
    orb = {}
    island_rows = {}
    for name, d5flag in (("d5", True), ("d3", False)):
        oi = {}
        bad = 0.0
        land = 0.0
        for fr in glob[name]:
            y = SC.decode_height(fr.heights)
            nm = names_of(fr.materials)
            oi[fr.face] = orbit_colour(nm, y, d5flag, np.clip(0.6 + 0.4 * SC._hillshade(y, SC.RS * 2.0 / (gres - 1)), 0.2, 1.2))
            above = y > 0.0
            land += float(above.sum())
            bad += float((above & ~np.isin(nm, list(ISLAND_OK))).sum())
        island_rows[name] = {"planetLandPixels": int(land), "planetLandNonIsland": int(bad)}
        g = np.zeros((size, 2 * size, 3), np.float64)
        for k, (cc, u) in enumerate(views[:2]):
            g[:, k * size:(k + 1) * size] = SC.globe_view(oi, cc, u, size)
        orb[name] = g
    gl = np.concatenate([orb["d5"], np.full((8, 2 * size, 3), 250.0), orb["d3"]], axis=0)
    entries = [("%s -> %s" % (k, v), FAR[v]) for k, v in D4_PALETTE.items()] + [("water (fog colour)", tuple(int(c_) for c_ in WATER))]
    p = os.path.join(a.out, "c2-orbit-globe.png")
    SC._legend(gl, entries, "from orbit, seabed materials under %d m of visibility: design 5 + palette (top: site, antipode) vs design 3 as deployed (bottom)" % VIS_M, p)
    written.append(p)
    # 3b. 60 km around the site from orbit with the tiles composited
    tl = []
    for name, d5flag in (("d5", True), ("d3", False)):
        fr = f1[name]
        img, vis, _H = crater_view(fr, d5flag, sn, half=30000.0, pitch=40.0)
        tl.append(img)
    canvas = np.concatenate([tl[0], np.full((tl[0].shape[0], 8, 3), 250.0), tl[1]], axis=1)
    p = os.path.join(a.out, "c2-orbit-face1.png")
    SC._legend(canvas, entries, "from orbit, 60 km around the site (40 m pixels, the tiles composited): design 5 + palette left, design 3 right", p)
    written.append(p)
    # 4. the crater and the tile seam from 8 km up (8 m pixels)
    seam = {}
    cv = []
    for name, d5flag in (("d5", True), ("d3", False)):
        img, vis, H = crater_view(f1[name], d5flag, sn, half=4000.0, pitch=8.0)
        cv.append(img)
        xs = np.arange(-4000.0, 4000.0, 8.0) + 4.0
        X, Z = np.meshgrid(xs, xs[::-1])
        rinf = np.maximum(np.abs(X), np.abs(Z))
        band = (rinf >= 1900.0) & (rinf <= 2300.0)
        gy, gx = np.gradient(vis, 8.0)
        step = np.hypot(gx, gy) * 8.0
        seam[name] = {"deeperThan1500mShareInsideRhoInf3700": float((vis[rinf <= 3700.0] < -1500.0).mean()),
                      "maxStepPer8mNearBorder": float(np.nanmax(step[band])), "p99StepPer8mNearBorder": float(np.nanpercentile(step[band], 99)),
                      "voidFloorKm2InsideRhoInf3700": float((vis[rinf <= 3700.0] < -1500.0).sum() * 64.0 / 1e6)}
        if sn is not None and name == "d5":
            t = sn.top_at(X, Z)
            lfv = sn.lowfloor_at(X, Z)
            m = np.isfinite(lfv) & sn.protected_at(X, Z)
            rf = sn.rim_fill_at(X, Z) & np.isfinite(t)
            buried = H - t
            seam[name]["rimFillColumns8m"] = int(rf.sum())
            seam[name]["rimFillBuried"] = int((rf & (buried > 0.0)).sum())
            seam[name]["rimFillBuriedMaxM"] = float(np.max(buried[rf])) if rf.any() else 0.0
            seam[name]["rimFillBuriedP50M"] = float(np.median(buried[rf & (buried > 0.0)])) if (rf & (buried > 0.0)).any() else 0.0
            above = H - lfv                     # > 0: the planet's surface pokes into Subnautica's terrain (above its lowest floor)
            seam[name]["snTerrainColumns8m"] = int(m.sum())
            seam[name]["planetAboveSnLowestFloor"] = int((above[m] > 0.0).sum())
            seam[name]["planetWithinMarginOfSnLowestFloor"] = int((above[m] > -CAP_MARGIN_M).sum())
            seam[name]["planetAboveSnLowestFloorMaxM"] = float(np.max(above[m])) if m.any() else 0.0
            seam[name]["tileLandColumns8m"] = int((np.isfinite(t) & (t > 0)).sum())
    canvas = np.concatenate([cv[0], np.full((cv[0].shape[0], 8, 3), 250.0), cv[1]], axis=1)
    p = os.path.join(a.out, "c2-seam.png")
    SC._legend(canvas, entries, "the crater from 8 km up (8 m pixels, the Subnautica map's 4 km square in the middle): design 5 + palette left, design 3 right", p)
    written.append(p)
    numbers["seam"] = seam
    # islands (the row: no voxel above water with a non-island material)
    if sn is not None:
        t = sn.top
        landc = np.isfinite(t) & (t > 0)
        for name, d5flag in (("d5", True), ("d3", False)):
            nm = tile_band_name(np.where(landc, t, -1.0), d5flag, sn.slope)
            island_rows[name]["tileLandColumns4m"] = int(landc.sum())
            island_rows[name]["tileLandNonIsland"] = int((landc & ~np.isin(nm, list(ISLAND_OK))).sum())
    numbers["islands"] = island_rows
    numbers["voidGPixels"] = {name: int(sum(int(np.count_nonzero(fr.biomes == SC.VOID_G)) for fr in glob[name])) for name in ("d3", "d5")}
    # 5. histograms
    for name in ("d3", "d5"):
        numbers["histogram_%s_planet" % name] = histogram(glob[name], ring_out=True)
        numbers["histogram_%s_all" % name] = histogram(glob[name], ring_out=False)
    Wd, Hd = 1100, 520
    him = Image.new("RGB", (Wd, Hd), (250, 250, 250))
    dr = ImageDraw.Draw(him)
    names = [b[2] for b in BANDS]
    bw = (Wd - 80) / len(names)
    vmax = max(max(numbers["histogram_d5_planet"].values()), max(numbers["histogram_d3_planet"].values()))
    for k, nm in enumerate(names):
        x0 = 40 + k * bw
        for j, (key, col) in enumerate((("histogram_d5_planet", (60, 110, 170)), ("histogram_d3_planet", (170, 170, 170)))):
            v = numbers[key][nm]
            h = (Hd - 180) * v / vmax
            dr.rectangle([x0 + j * bw * 0.42, Hd - 80 - h, x0 + (j + 1) * bw * 0.42, Hd - 80], fill=col)
            dr.text((x0 + j * bw * 0.42, Hd - 96 - h), "%.0f%%" % (100 * v), fill=(0, 0, 0))
        dr.text((x0, Hd - 74), nm.replace(" (", "\n(").replace(", ", ",\n"), fill=(0, 0, 0))
    dr.text((20, 20), "Seabed depth beyond the ring (area-weighted, all faces): design 5 (blue) against the deployed design 3 (grey)", fill=(0, 0, 0))
    p = os.path.join(a.out, "c2-histogram.png")
    him.save(p)
    written.append(p)
    # 6. sections through the edge and the tile seam
    secs, seams = {}, {}
    bases = {}
    for name in ("d3", "d5"):
        fr = f1[name]
        base = R3.Base(fr.heights, fr.biomes)
        bases[name] = base
        secs[name] = [(az,) + section(base, az) for az in AZIMUTHS]
        seams[name] = [(az,) + section(base, az, r1=4500.0, step=4.0, r0=1000.0) for az in SEAM_AZ]
        Ho, _k = base.column(np.array([OWNER_SPOT[0]]), np.array([OWNER_SPOT[1]]))
        numbers["owner_spot_%s_y" % name] = float(Ho[0])
        prof = {}
        for az, rs, h, kk in secs[name]:
            sel = (rs >= 12000.0) & (rs <= 20000.0)
            prof["%.0f" % az] = {"edgeMinY": float(np.nanmin(h[sel])), "atRho": float(rs[sel][int(np.nanargmin(h[sel]))]),
                                 "y3km": float(h[np.searchsorted(rs, 3000.0)]),
                                 "y14km": float(h[np.searchsorted(rs, 14000.0)]), "y16km": float(h[np.searchsorted(rs, 16000.0)]),
                                 "y20km": float(h[np.searchsorted(rs, 20000.0)]), "y25km": float(h[np.searchsorted(rs, 25000.0)])}
        numbers["sections_%s" % name] = prof
    p = os.path.join(a.out, "c2-sections.png")
    draw_sections(secs["d3"], secs["d5"], seams["d3"], seams["d5"], sn, p)
    written.append(p)
    # 6b. continuity (design 5's rule): rings around the site and the km-scale gradient
    cont = {name: continuity(bases[name], sn) for name in ("d3", "d5")}
    numbers["continuity"] = {k: {kk: vv for kk, vv in v.items() if kk != "annulusMeans"} for k, v in cont.items()}
    p = os.path.join(a.out, "c2-continuity.png")
    draw_continuity(cont["d3"], cont["d5"], p)
    written.append(p)
    # 7. the stamped field on planet biomes
    if not a.skip_stamped:
        p, info = stamped_sheet(bases["d5"], os.path.join(a.out, "c2-planet-stamped.png"))
        if p:
            written.append(p)
        numbers["stamped"] = info
    # 8. densities: Subnautica's census against the spawn table, per planet biome
    if a.sn_density and os.path.exists(a.sn_density):
        cen = json.load(open(a.sn_density, encoding="utf-8"))["biomes"]
        table = {0: 21.0, 1: 20.0, 2: 11.0, 3: 5.0}             # S8aSpawnTable per band (Wire S8aBiomes.cs), no row deeper than 1,000 m
        lay = SC.load_layout(D4)
        dens = {}
        for b in lay["biomes"]:
            if b.get("zone") != "planet" and b["key"] not in ("safeShallows", "grassyPlateaus", "kelpForest", "sparseReef", "grandReef", "bloodKelp", "dunes", "mushroomForest"):
                continue
            sn_name = b["snName"]
            mid = 0.5 * (b["band"][0] + b["band"][1])
            band = 0 if mid <= 60 else 1 if mid <= 200 else 2 if mid <= 500 else 3 if mid <= 1000 else None
            ours = table.get(band, 0.0)
            ref = cen.get(sn_name) or cen.get({"lostRiver_Canyon": "bloodKelp"}.get(sn_name, ""), {})
            snd = (ref.get("bakedPerHa", 0.0) + ref.get("slotsPerHa", 0.0)) if ref else None
            dens[b["key"]] = {"snCounterpart": sn_name, "bandM": b["band"], "spawnTablePerHa": ours, "subnauticaPerHa": snd,
                              "ratio": round(snd / ours, 1) if snd and ours else None}
        numbers["densities"] = dens
    p = os.path.join(a.out, "c2-numbers.json")
    json.dump(numbers, open(p, "w", encoding="utf-8"), indent=1)
    written.append(p)
    for w in written:
        print("wrote %s" % w)
    print(json.dumps(numbers, indent=1))
    print("%.0f s" % (time.time() - t0))
    return 0

if __name__ == "__main__":
    sys.exit(main())
