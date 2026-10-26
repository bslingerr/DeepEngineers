"""S8a.C2.native (Stage C step 1): SE's own voxel generator on our heightmaps, offline, against the sculpt tool's surface.

SE builds a planet's voxels in native code: MyPlanetShapeProvider creates `VrPlanetShape(translation, radius, hillMin,
hillMax, Mapset, DetailMapData, useLegacyAtan: true)`
 and fills the storage with `VrPlanetShape.ReadContentRange`; the P/Invoke
signatures are VRage.NativeWrapper's (VRage.Voxels.VrPlanetShape: VrPlanetShape_Create(Vector3, float, float, float,
Mapset, DetailMapData, bool), VrPlanetShape_GetValue(IntPtr, Vector2, int, out Vector3), VrPlanetShape_ReadContentRange(
IntPtr, byte*, int, int, Vector3I, Vector3I, Vector3I, float, int)).Native.dll
read-only (nothing is written anywhere), hands it the six heightmaps of a mod folder exactly as MyHeightmapFace holds them
(a one-pixel border, here the edge replicated: the site is far from every face seam) and measures:

 1. GetValue (the normalised bicubic) against sculpt_de4546b.catmull_rom_at at 400 texcoords around the site;
 2. the LOD-0 voxel isosurface (content 127.5 crossing, VRage's content = (clamp(-sdf / lod, -1, 1) * 0.5 + 0.5) * 255) on vertical columns at the c2g points and a ring of points, against
 sculpt_de4546b.surface_y_sn (the tool's --depth-at). The column runs along the storage axis nearest to the radial
 direction, so steep walls read a little off: the row's bound is 1 m on slopes below 0.5, reported otherwise.

 python -I tools/planet/sculpt_native_check.py [<modDir>] (default: the repository's world mod)
Exit 0: PASS or SKIP (no SE install / not 64-bit Windows); 1: FAIL.
"""
import ctypes as C
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import sculpt_de4546b as S  # noqa: E402

SE_DEFAULT = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers"
STORAGE_HALF = 131072.0             # FindBestOctreeSize(2 * 67,200) / 2: the shape's translation
TOL_BICUBIC_M = 0.1                 # float32 texcoords: about 2e-4 px x 46 m x the slope (5 on the crater wall)
TOL_COLUMN_M = 1.0
POINTS = S.C2G_POINTS               # the c2g points (sculpt_de4546b.C2G_POINTS)

class V3(C.Structure):
    _fields_ = [("x", C.c_float), ("y", C.c_float), ("z", C.c_float)]

class V2(C.Structure):
    _fields_ = [("x", C.c_float), ("y", C.c_float)]

class V3I(C.Structure):
    _fields_ = [("x", C.c_int), ("y", C.c_int), ("z", C.c_int)]

P16 = C.POINTER(C.c_uint16)

class Mapset(C.Structure):
    _fields_ = [(n, P16) for n in ("Front", "Back", "Left", "Right", "Up", "Down")] + [("Resolution", C.c_int)]

class Detail(C.Structure):
    _fields_ = [("Data", C.c_void_p), ("Resolution", C.c_int)] + [(n, C.c_float) for n in (
        "Factor", "Size", "Scale", "m_min", "m_max", "m_in", "m_out", "m_inRecip", "m_outRecip", "m_mid")]

def load_native():
    if os.name != "nt" or C.sizeof(C.c_void_p) != 8:
        return None, "not 64-bit Windows"
    se = os.environ.get("DEEPENG_SE") or SE_DEFAULT
    b64 = os.path.join(se, "Bin64")
    dll = os.path.join(b64, "VRage.Native.dll")
    if not os.path.isfile(dll):
        return None, "no %s" % dll
    try:
        os.add_dll_directory(b64)
        n = C.CDLL(dll)
    except OSError as e:
        return None, "load %s: %s" % (dll, e)
    n.VrPlanetShape_Create.restype = C.c_void_p
    n.VrPlanetShape_Create.argtypes = [V3, C.c_float, C.c_float, C.c_float, Mapset, Detail, C.c_bool]
    n.VrPlanetShape_GetValue.restype = C.c_float
    n.VrPlanetShape_GetValue.argtypes = [C.c_void_p, V2, C.c_int, C.POINTER(V3)]
    n.VrPlanetShape_ReadContentRange.restype = None
    n.VrPlanetShape_ReadContentRange.argtypes = [C.c_void_p, C.POINTER(C.c_uint8), C.c_int, C.c_int, V3I, V3I, V3I, C.c_float, C.c_int]
    n.VrPlanetShape_Release.restype = None
    n.VrPlanetShape_Release.argtypes = [C.c_void_p]
    return n, dll

def column_surface(n, inst, sx, sz, y_guess, span=200):
    """The LOD-0 isosurface along the storage axis nearest to the column's radial direction near SN y_guess: (SN y, slope hint)."""
    d = S.sn_to_dir(sx, sz)
    r0 = S.RS + y_guess
    p = [d[0] * r0, d[1] * r0, d[2] * r0]
    ax = int(np.argmax(np.abs(d)))
    sign = 1.0 if d[ax] > 0 else -1.0
    idx = [int(round(p[k] + STORAGE_HALF)) for k in range(3)]
    a0 = int(math.floor(p[ax] + STORAGE_HALF)) - span // 2
    start, end = list(idx), list(idx)
    start[ax], end[ax] = a0, a0 + span - 1
    buf = (C.c_uint8 * span)()
    n.VrPlanetShape_ReadContentRange(inst, buf, span, 0, V3I(1, 1, 1), V3I(*start), V3I(*end), 1.0, -1)
    c = np.frombuffer(buf, np.uint8).astype(np.float64)
    order = range(span - 1) if sign > 0 else range(span - 1, 0, -1)
    for k in order:
        k2 = k + 1 if sign > 0 else k - 1
        if c[k] >= 127.5 > c[k2]:
            t = (c[k] - 127.5) / (c[k] - c[k2])
            q = list(idx)
            loc = [q[0] - STORAGE_HALF, q[1] - STORAGE_HALF, q[2] - STORAGE_HALF]
            loc[ax] = a0 + k + (t if sign > 0 else -t) - STORAGE_HALF
            return math.sqrt(loc[0] ** 2 + loc[1] ** 2 + loc[2] ** 2) - S.RS
    return float("nan")

def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    mod = argv[0] if argv else S.REPO_MOD_DIR
    n, why = load_native()
    if n is None:
        print("SKIP S8a.C2.native: %s" % why)
        return 0
    hm = S.read_heightmaps(mod)
    if hm is None:
        print("MISSING heightmaps in %s" % S.planet_dir(mod))
        return 1
    res = hm[1].shape[0]
    bufs = [np.ascontiguousarray(np.pad(hm[f], 1, mode="edge").astype(np.uint16)) for f in range(6)]
    ms = Mapset(*[b.ctypes.data_as(P16) for b in bufs], res)
    inst = n.VrPlanetShape_Create(V3(STORAGE_HALF, STORAGE_HALF, STORAGE_HALF), S.R, S.HILL_MIN, S.HILL_MAX, ms, Detail(), True)
    if not inst:
        print("FAIL S8a.C2.native: VrPlanetShape_Create returned null")
        return 1
    ok = True
    try:
        rng = np.random.default_rng(4546)
        worst = 0.0
        for _ in range(400):
            tx, ty = rng.uniform(0.38, 0.62, 2)
            nrm = V3()
            v = n.VrPlanetShape_GetValue(inst, V2(tx, ty), 1, C.byref(nrm))
            ref = (S.catmull_rom_at(hm[1], tx * (res - 1), ty * (res - 1)) + S.RS - S.INNER) / S.HEIGHT_RATIO
            worst = max(worst, abs(v - ref) * S.HEIGHT_RATIO)
        good = worst <= TOL_BICUBIC_M
        ok &= good
        print("%-4s S8a.C2.native.bicubic VrPlanetShape_GetValue = the tool's Catmull-Rom at 400 texcoords: max |dy| %.4f m (bound %.2f)" % (
            "OK" if good else "FAIL", worst, TOL_BICUBIC_M))
        pts = list(POINTS) + [(r * math.cos(a), r * math.sin(a)) for r in (4320.0, 5280.0, 8000.0) for a in np.linspace(0, 2 * math.pi, 24, endpoint=False)]
        worst_flat, worst_all, rows = 0.0, 0.0, []
        for sx, sz in pts:
            yt = S.surface_y_sn(hm, sx, sz)
            yn = column_surface(n, inst, sx, sz, yt)
            dd = abs(yn - yt) if not math.isnan(yn) else float("inf")
            e = 2.0
            slope = max(abs(S.surface_y_sn(hm, sx + e, sz) - S.surface_y_sn(hm, sx - e, sz)),
                        abs(S.surface_y_sn(hm, sx, sz + e) - S.surface_y_sn(hm, sx, sz - e))) / (2 * e)
            worst_all = max(worst_all, dd)
            if slope < 0.5:
                worst_flat = max(worst_flat, dd)
            rows.append((sx, sz, yt, yn, dd, slope))
        good = worst_flat <= TOL_COLUMN_M
        ok &= good
        for sx, sz, yt, yn, dd, slope in rows[:len(POINTS)]:
            print("     SN (%8.1f, %8.1f): tool %9.2f  native voxels %9.2f  |d| %6.2f m  slope %.2f" % (sx, sz, yt, yn, dd, slope))
        print("%-4s S8a.C2.native.voxels LOD-0 isosurface of SE's VrPlanetShape = the tool's --depth-at on %d columns: max |dy| %.2f m on slopes < 0.5 "
              "(bound %.1f), %.2f m on all" % ("OK" if good else "FAIL", len(rows), worst_flat, TOL_COLUMN_M, worst_all))
    finally:
        n.VrPlanetShape_Release(inst)
    print("SCULPT NATIVE %s (%s, files %s)" % ("PASS" if ok else "FAIL", why, os.path.abspath(mod)))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
