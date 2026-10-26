"""DeepEngineers wire conventions: independent reference implementation (double precision, stdlib only).

Companion of worldmap_ref.py (the SE <-> SN position map) and of docs/recon/gap-1.md (the decision record).
It fixes, as executable arithmetic, what crosses the bridge between the Space Engineers plugin (SE, VRage,
right-handed, doubles) and the Subnautica plugin (SN, Unity 2019.4, left-handed, floats):

 * world-level data (entity poses, camera, velocities, world points and directions) crosses in SN space,
 computed by the SE plugin in doubles: pose = (o, q, s)
 o float3 SN position of the entity origin under the map linearised at a pivot P* (see below)
 q float4 Unity quaternion (x, y, z, w), w >= 0, = LookRotation(mapdir(Forward), mapdir(Up)) at P*
 s float horizontal Jacobian scale Rs / |P* - C| (SN metres per SE metre); vertical scale is 1
 SN applies it as world = o + S * (q * mirror(l)) with S = diag(s, 1, s), for an SE-local point l.
 * entity-local data (mesh vertices, normals, part and bone matrices, collision boxes, local hit points)
 crosses in SE-local, SE-handed space in both directions; SN applies the one mirror (x, y, z) -> (x, y, -z)
 and, for triangles, emits (I0, I2, I1) of MyModel.Triangles.
 * colliders are never children of the scaled frame: each section is a unit-scale root posed by the same
 affine, and box centres/sizes are pre-scaled on the SN side with K = q^-1 S q.
 * the pivot P* is chosen by the SE plugin: the point of the entity group's box nearest to the camera
 (the camera itself when it is inside). The wire does not carry P*; it only changes o, q and s.

Usage:
 python wire_ref.py selftest [--site v1|example|all]
 checks + the error tables quoted in gap-1.md / CONTRACT; default: all (EXAMPLE, then SITE_V1)
 python wire_ref.py vectors OUT.jsonl [N] [--site v1|example]
 writes the WM-1 conformance vector file (deterministic); default site: v1 (site_v1.SITE_V1, the contract site)
 python wire_ref.py s3vectors OUT.jsonl
 writes the S3 vectors (poses with k and corners, the affine-vs-F bound, the cube model payload, C3/C4 payloads,
 model/material keys, tangent w, armor UVs) for tests/DeepEngineers.WireMath.Tests (deterministic, SITE_V1)
 python wire_ref.py s5vectors OUT|-
 writes the S5 vectors (conventions text and hash, skeleton, model/part/skin keys, flags, SE matrix algebra, the
 subgrid relative pose, the Unity conjugation, interpolation, bone map, weights, binds, C1 bone locals, skinning,
 split by material, chunk cells, colour keys, character records,.derec) for tests/DeepEngineers.Wire.Tests/
 S5VectorTests.cs (deterministic; '-' = stdout)
 python wire_ref.py s5selftest
 the S5 pins (conventions 0x31dfd6f9befdef5d, skeleton canon1 0x995765282e7d4f1d), the CONTRACT 5.4 example
 vectors and the Python-only consistency checks (skinning conjugation, relative pose, weights, cells,.derec)
 python wire_ref.py s5skeleton-cs [OUT]
 writes (or prints) src/DeepEngineers.Wire/MirrorSkeleton.Canon1.cs, generated from SeSkeleton.canon1.tsv
 python wire_ref.py s6conventions
 prints the S6 combat conventions text (CONTRACT 5.4) and its FNV-1a 64 (pin 0xb6ee880712f8253b)
 python wire_ref.py s6selftest
 the S6 pins, the CONTRACT 2.6/4.1/5.4 examples, the Python-only consistency checks (coverage, sections, payload
 round trip, refresh, subgrid placement, hit point frames, budget, dedup, factors) and the S6.G1-E3 / S6.M5 table
 python wire_ref.py s6vectors OUT|-
 writes the S6 vectors (conventions, lattice, splits, cell boxes, OBBs, segmentation stand-in, sections and SkinSet
 payloads, bad payloads, refresh, placement, capsule, budget, corner errors, the S6.M5 family, dedup, C8 records,
 checks, factors, hit cells) for tests/DeepEngineers.Wire.Tests/S6VectorTests.cs (deterministic; '-' = stdout)
 python wire_ref.py s6bound depth=<m> tilt=<deg> [h=4,4,4] [offset=8] [site=v1] [samples=400] [seed=1] [axes=random|level] [m5=0|1]
 one JSON line {depth, tilt, s,..., worstM, boundM = max(1.02*worst, 0.001)}: the S6.G1-E3 bound (CONTRACT 2.6);
 m5=1 adds the S6.M5 family's worst deviations (m5LevelWorstM, m5RotatedWorstM)

S3 (ROADMAP S3, CONTRACT 2.5): mesh_to_unity, winding_measure, tangent_w, aabb_corners, sn_apply_k, rigid_draw_error,
affine_vs_f_bound(_at), armor_uv, pattern_offset(_packed), model_key, material_key, model_payload, model_messages,
material_def_payload, part_add_payload, cube_model, lab_block. tests/s3/check_placement.py imports this file.

S5 (ROADMAP S5, CONTRACT 5.4 "C1-C4 conventions for S5"; layout 1.0 unchanged): s5_conv_text, s5_skeleton_rows,
s5_model_key, s5_cube_key, s5_edge_key, s5_fat_key_text, s5_subpart_key_text, s5_skin_id, s5_version, s5_draw_kind, se_mul,
se_inverse, s5_relative, s5_to_unity, s5_interpolate, s5_bone_map, s5_remap_weights, s5_bind_rows, s5_bone_locals,
s5_skin_point, s5_split, s5_cell, s5_colour_key, s5_character_*_payload, derec_header, derec_record, derec_read.

Sites (CONTRACT section 2.9 and open item O1): "v1" = site_v1.SITE_V1 (Rs 60,000.0039 m, test centre C), the site
of the contract, the in-game subsets and the dotnet test; "example" = worldmap_ref.EXAMPLE (Rs 63.5 km), the site of
the numbers in gap-1.md. Both files are generated by the test runner and never committed.
"""
import hashlib
import json
import math
import os
import random
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # our own folder (python -I drops it)
import worldmap_ref as wm  # noqa: E402
from worldmap_ref import add, sub, mul, dot, cross, norm, unit  # noqa: E402
import site_v1  # noqa: E402 (imports the same worldmap_ref module object)

WIRE_VERSION = 1

SITES = {"v1": "SITE_V1", "example": "EXAMPLE"}

def site_by_name(name):
    """Site selector (CONTRACT O1): 'v1' -> site_v1.SITE_V1, 'example' -> worldmap_ref.EXAMPLE."""
    if name == "v1":
        return site_v1.SITE_V1
    if name == "example":
        return wm.EXAMPLE
    raise ValueError(f"unknown site {name!r} (expected one of {sorted(SITES)})")

# ---------------------------------------------------------------- small helpers
def f32(x):
    return struct.unpack("f", struct.pack("f", x))[0]

def f32v(v):
    return tuple(f32(c) for c in v)

def mirror(v):
    """The only handedness change on entity-local data: SE local (x, y, z) <-> Unity local (x, y, -z)."""
    return (v[0], v[1], -v[2])

def tri_to_unity(t):
    """MyModel.Triangles (I0, I1, I2) -> Unity triangle (I0, I2, I1). See gap-1.md section 3.3."""
    return (t[0], t[2], t[1])

# ---------------------------------------------------------------- SE matrices (VRage row-vector convention)
# An SE world matrix is kept as its rows: Right (M11..M13), Up (M21..M23), Backward (M31..M33),
# Translation (M41..M43). Forward = -Backward. Vector3D.Transform(l, M) = l.x*Right + l.y*Up + l.z*Backward + T.
def se_transform(m, l):
    R, U, B, T = m
    return add(add(add(mul(R, l[0]), mul(U, l[1])), mul(B, l[2])), T)

def se_forward(m):
    return mul(m[2], -1.0)

def se_inverse_point(m, p):
    R, U, B, T = m
    d = sub(p, T)
    return (dot(d, R), dot(d, U), dot(d, B))

def se_rotate_rows_world(m, axis, angle):
    """Apply VRage Matrix.CreateFromAxisAngle(axis, angle) in world space: rows r -> r * Rot (row vector).
 VRage's formula is the right-hand-rule Rodrigues rotation."""
    a = unit(axis)
    c, s = math.cos(angle), math.sin(angle)

    def rot(v):  # Rodrigues, right-hand rule: v cos + (a x v) sin + a (a.v)(1 - cos)
        return add(add(mul(v, c), mul(cross(a, v), s)), mul(a, dot(a, v) * (1 - c)))
    R, U, B, T = m
    return (rot(R), rot(U), rot(B), T)

# ---------------------------------------------------------------- Unity quaternion conventions
def quat_from_cols(X, Y, Z):
    """Rotation whose columns are the images of x, y, z -> (x, y, z, w), Hamilton, w >= 0."""
    m00, m01, m02 = X[0], Y[0], Z[0]
    m10, m11, m12 = X[1], Y[1], Z[1]
    m20, m21, m22 = X[2], Y[2], Z[2]
    tr = m00 + m11 + m22
    if tr > 0:
        S = math.sqrt(tr + 1.0) * 2
        q = ((m21 - m12) / S, (m02 - m20) / S, (m10 - m01) / S, 0.25 * S)
    elif m00 > m11 and m00 > m22:
        S = math.sqrt(1.0 + m00 - m11 - m22) * 2
        q = (0.25 * S, (m01 + m10) / S, (m02 + m20) / S, (m21 - m12) / S)
    elif m11 > m22:
        S = math.sqrt(1.0 + m11 - m00 - m22) * 2
        q = ((m01 + m10) / S, 0.25 * S, (m12 + m21) / S, (m02 - m20) / S)
    else:
        S = math.sqrt(1.0 + m22 - m00 - m11) * 2
        q = ((m02 + m20) / S, (m12 + m21) / S, 0.25 * S, (m10 - m01) / S)
    n = math.sqrt(sum(c * c for c in q))
    q = tuple(c / n for c in q)
    return tuple(-c for c in q) if q[3] < 0 else q

def look_rotation(f, u):
    """Unity Quaternion.LookRotation(forward, upwards): local +Z -> forward, local +Y -> up (orthogonalised),
 local +X -> Cross(up, forward) (so LookRotation(+Z, +Y) is the identity)."""
    Z = unit(f)
    X = unit(cross(u, Z))
    Y = cross(Z, X)
    return quat_from_cols(X, Y, Z)

def qrot(q, v):
    """Unity `q * v` (rotate vector v by unit quaternion q)."""
    u = (q[0], q[1], q[2])
    t = mul(cross(u, v), 2.0)
    return add(add(v, mul(t, q[3])), cross(u, t))

def qdist(a, b):
    """Distance between two unit quaternions as rotations: min(|a - b|, |a + b|) over all four components."""
    d1 = math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))
    d2 = math.sqrt(sum((x + y) ** 2 for x, y in zip(a, b)))
    return min(d1, d2)

def qconj(q):
    return (-q[0], -q[1], -q[2], q[3])

def qmul(a, b):
    """Unity `a * b` (Hamilton product: apply b, then a)."""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)

def scale_h(v, s):
    return (v[0] * s, v[1], v[2] * s)

# ---------------------------------------------------------------- the wire pose (computed by the SE plugin)
def wire_pose(site, m, pivot):
    """SE world matrix m (orthonormal rows) + pivot P* (SE world, doubles) -> (o, q, s) in SN space."""
    s = site.Rs / norm(sub(pivot, site.C))

    def J(w):  # differential of the map at the pivot: horizontal scale s after mapdir
        return scale_h(site.dir_se_to_sn(pivot, w), s)
    o = add(site.se_to_sn(pivot), J(sub(m[3], pivot)))
    q = look_rotation(site.dir_se_to_sn(pivot, se_forward(m)), site.dir_se_to_sn(pivot, m[1]))
    return o, q, s

def camera_pose(site, m):
    """The camera is mapped exactly and never scaled: position F(P), rotation from mapdir at P."""
    P = m[3]
    return site.se_to_sn(P), look_rotation(site.dir_se_to_sn(P, se_forward(m)), site.dir_se_to_sn(P, m[1]))

def pivot_nearest(m, bmin, bmax, cam):
    """P* = point of the entity (group) box nearest to the camera; the camera itself when inside."""
    l = se_inverse_point(m, cam)
    c = tuple(min(max(l[i], bmin[i]), bmax[i]) for i in range(3))
    return se_transform(m, c)

# ---------------------------------------------------------------- what the SN plugin does with it
def sn_apply(o, q, s, l_se):
    """SE-local point -> SN world: the Unity hierarchy Frame(pos o, rot I, scale (s,1,s)) / Entity(rot q)."""
    return add(o, scale_h(qrot(q, mirror(l_se)), s))

def sn_apply_dir(q, s, d_se):
    return scale_h(qrot(q, mirror(d_se)), s)

def sn_to_local_se(o, q, s, w):
    """SN world point -> SE-local point (what a damage record carries), inverse of sn_apply."""
    d = sub(w, o)
    return mirror(qrot(qconj(q), (d[0] / s, d[1], d[2] / s)))

def k_apply(q, s, v):
    """K = q^-1 S q in entity-local Unity coordinates: scales the part of v perpendicular to world up."""
    return qrot(qconj(q), scale_h(qrot(q, v), s))

def box_prescale(q, s, c_sec_se, b_se, h, axes_se):
    """Collider pre-scaling for a unit-scale section root (rotation q) placed at sn_apply(c_sec_se).
 Returns the box centre in section-local Unity coordinates, its half extents and its axes (Unity local)."""
    centre = k_apply(q, s, mirror(sub(b_se, c_sec_se)))
    axes_u = [mirror(a) for a in axes_se]
    h_u = tuple(h[i] * norm(k_apply(q, s, axes_u[i])) for i in range(3))
    return centre, h_u, axes_u

def angvel_to_sn(site, pivot, w_se):
    """Angular velocity is a pseudovector: under the improper coordinate map it changes sign."""
    return mul(site.dir_se_to_sn(pivot, w_se), -1.0)

def vel_to_sn(site, pivot, v_se):
    s = site.Rs / norm(sub(pivot, site.C))
    return scale_h(site.dir_se_to_sn(pivot, v_se), s)

# ---------------------------------------------------------------- 3x3 helpers (matrices kept as 3 column tuples)
def det_cols(c):
    return dot(c[0], cross(c[1], c[2]))

def inverse_transpose_cols(c):
    """Columns of M^-T for M with columns c (cofactor columns / det)."""
    d = det_cols(c)
    return (mul(cross(c[1], c[2]), 1.0 / d), mul(cross(c[2], c[0]), 1.0 / d), mul(cross(c[0], c[1]), 1.0 / d))

def polar_rotation(c, tol=1e-15, max_iter=60):
    """Orthogonal factor R of the polar decomposition M = R P (P symmetric positive definite), for M with columns c
 and det M > 0. Newton iteration X <- (X + X^-T) / 2 (quadratic convergence; a handful of steps here)."""
    x = tuple(tuple(v) for v in c)
    for _ in range(max_iter):
        y = inverse_transpose_cols(x)
        xn = tuple(tuple(0.5 * (x[i][j] + y[i][j]) for j in range(3)) for i in range(3))
        d = max(abs(xn[i][j] - x[i][j]) for i in range(3) for j in range(3))
        x = xn
        if d <= tol:
            break
    return x

def box_collider(q, s, c_sec_se, b_se, h, axes_se):
    """CONTRACT 2.6 for one box of a section posed at sn_apply(c_sec) with rotation q (unit-scale root):
 centre = K*Mirror(b - c_sec); half extent_i = h_i*|K*Mirror(e_i)|; child rotation (section-local Unity) = the
 polar (orthonormalised) frame of the columns (K*Mirror(e0), K*Mirror(e1), -K*Mirror(e2)). The third column is
 negated because Mirror turns the right-handed SE box frame into a left-handed one and a box is symmetric under an
 axis flip. Since K = q^-1 S q is symmetric positive definite, this polar factor equals the rigid mirrored frame
 (Mirror(e0), Mirror(e1), -Mirror(e2)) to rounding: K changes the sizes only. Level/vertical (grid-aligned) axes
 give the identity, i.e. the box sits on the section root itself."""
    centre, h_u, axes_u = box_prescale(q, s, c_sec_se, b_se, h, axes_se)
    cols = (k_apply(q, s, axes_u[0]), k_apply(q, s, axes_u[1]), mul(k_apply(q, s, axes_u[2]), -1.0))
    R = polar_rotation(cols)
    return centre, h_u, axes_u, quat_from_cols(R[0], R[1], R[2])

def static_to_se(site, c_sn, q_sn, h_sn):
    """CONTRACT 2.7 for a Subnautica static box primitive (centre, Unity rotation, half extents; SN-world floats):
 centre_se = F^-1(c); s = Rs/|centre_se - C|; SE axes rows (Right, Up, Backward) = staticAxes(q) =
 (mapdir^-1(q*x), mapdir^-1(q*y), mapdir^-1(q*(-z))) at centre_se, determinant +1; SE half extent_i =
 h_i*|S^-1*(q*e_i)| (horizontal extents divided by s, shear dropped). q is normalised first (float32 wire)."""
    n = math.sqrt(sum(c * c for c in q_sn))
    q = tuple(c / n for c in q_sn)
    P = site.sn_to_se(c_sn)
    s = site.Rs / norm(sub(P, site.C))
    right = site.dir_sn_to_se(P, qrot(q, (1.0, 0.0, 0.0)))
    up = site.dir_sn_to_se(P, qrot(q, (0.0, 1.0, 0.0)))
    backward = site.dir_sn_to_se(P, qrot(q, (0.0, 0.0, -1.0)))
    he = tuple(h_sn[i] * norm(scale_h(qrot(q, e), 1.0 / s))
               for i, e in enumerate(((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))))
    return P, s, (right, up, backward), det_cols((right, up, backward)), he

# ---------------------------------------------------------------- random test objects
def random_unit(rnd):
    while True:
        v = (rnd.uniform(-1, 1), rnd.uniform(-1, 1), rnd.uniform(-1, 1))
        n = norm(v)
        if 0.1 < n <= 1:
            return mul(v, 1 / n)

def random_se_matrix(rnd, T, tilt_max_deg=180.0, site=None):
    """Orthonormal SE world matrix at T. With site and tilt_max_deg < 180 the Up axis stays within that
 angle of the local vertical (gravity up), like a ship."""
    if site is not None and tilt_max_deg < 180:
        E, U, N = site.frame_at(T)
        tilt = math.radians(rnd.uniform(0, tilt_max_deg))
        az = rnd.uniform(0, 2 * math.pi)
        up = add(mul(U, math.cos(tilt)), mul(add(mul(E, math.cos(az)), mul(N, math.sin(az))), math.sin(tilt)))
    else:
        up = random_unit(rnd)
    f = random_unit(rnd)
    f = unit(sub(f, mul(up, dot(f, up))))
    right = cross(f, up)  # VRage: Right = Forward x Up
    return (right, up, mul(f, -1.0), T)

def random_quat(rnd):
    """Uniform random rotation (Shoemake), Unity (x, y, z, w), w >= 0."""
    u1, u2, u3 = rnd.random(), rnd.random(), rnd.random()
    a, b = math.sqrt(1.0 - u1), math.sqrt(u1)
    q = (a * math.sin(2 * math.pi * u2), a * math.cos(2 * math.pi * u2), b * math.sin(2 * math.pi * u3), b * math.cos(2 * math.pi * u3))
    return tuple(-c for c in q) if q[3] < 0 else q

def cube_mesh(h=1.25):
    """A closed box in SE-local coordinates with triangles in the MyModel.Triangles convention:
 cross(V1 - V0, V2 - V0) is the outward (front) normal."""
    verts, tris, normals = [], [], []
    for axis in range(3):
        for sign in (-1.0, 1.0):
            n = [0.0, 0.0, 0.0]
            n[axis] = sign
            a1 = [0.0, 0.0, 0.0]
            a2 = [0.0, 0.0, 0.0]
            a1[(axis + 1) % 3] = 1.0
            a2[(axis + 2) % 3] = 1.0
            base = len(verts)
            for (i, j) in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                verts.append(tuple(h * (n[k] + i * a1[k] + j * a2[k]) for k in range(3)))
            for t in ((0, 1, 2), (0, 2, 3)):
                a, b, c = (verts[base + t[0]], verts[base + t[1]], verts[base + t[2]])
                tt = (base + t[0], base + t[1], base + t[2])
                if dot(cross(sub(b, a), sub(c, a)), tuple(n)) < 0:
                    tt = (tt[0], tt[2], tt[1])
                tris.append(tt)
                normals.append(tuple(n))
    return verts, tris, normals

def screen_area_unity(cam_pos, cam_q, pts):
    """Signed area of a triangle on a Unity camera's screen (x right, y up); > 0 is counter-clockwise."""
    proj = []
    for p in pts:
        c = qrot(qconj(cam_q), sub(p, cam_pos))
        proj.append((c[0] / c[2], c[1] / c[2]))
    (x0, y0), (x1, y1), (x2, y2) = proj
    return 0.5 * ((x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0))

# ---------------------------------------------------------------- S3: mesh conventions, entity scale, C3/C4 payloads
# Independent of the C# code (src/DeepEngineers.Wire/MeshConventions.cs, PoseMath.cs, ModelCodec.cs); compared with it
# by tests/DeepEngineers.WireMath.Tests/S3VectorTests.cs through `python -I wire_ref.py s3vectors OUT.jsonl`.
WINDING_FLIP_BELOW = 0.01      # CONTRACT 2.5: a model with < 1 % agreeing triangles is flipped
WINDING_PASS_ABOVE = 0.99      # ROADMAP S3.C3
DEGENERATE_AREA, DEGENERATE_NORMAL, DEGENERATE_COS = 1e-9, 1e-6, 0.05   # census rule (tools/mwm-winding)
FNV64_OFFSET = 0xCBF29CE484222325
FNV64_PRIME = 0x100000001B3
MODEL_FLAG_SKINNED, MODEL_FLAG_HAS_TANGENTS = 1, 2
C3_MODEL_BEGIN, C3_MODEL_CHUNK, C3_MODEL_END, C3_MATERIAL_DEF = 1, 2, 3, 4
MAX_CHUNK_BYTES = 4 << 20

def mesh_to_unity(verts_se, normals_se, tris_se):
    """Entity-local mesh -> Unity: positions and normals mirrored, triangles (I0, I2, I1) of MyModel.Triangles."""
    return [mirror(v) for v in verts_se], [mirror(n) for n in normals_se], [tri_to_unity(t) for t in tris_se]

def classify_triangle(a, b, c, na, nb, nc):
    """+1 when cross(b - a, c - a) agrees with na + nb + nc, -1 against, 0 degenerate (the census rule)."""
    g = cross(sub(b, a), sub(c, a))
    avg = add(add(na, nb), nc)
    d = dot(g, avg)
    gl, al = norm(g), norm(avg)
    if gl < DEGENERATE_AREA or al < DEGENERATE_NORMAL or abs(d) < DEGENERATE_COS * gl * al:
        return 0
    return 1 if d > 0 else -1

def winding_measure(verts, normals, tris):
    """(agree, disagree, degenerate) of a mesh given per-vertex normals (one normal per vertex index)."""
    counts = [0, 0, 0]
    for t in tris:
        k = classify_triangle(verts[t[0]], verts[t[1]], verts[t[2]], normals[t[0]], normals[t[1]], normals[t[2]])
        counts[0 if k > 0 else (1 if k < 0 else 2)] += 1
    return tuple(counts)

def winding_fraction(stats):
    a, d, _ = stats
    return 1.0 if a + d == 0 else a / (a + d)

def tangent_w(n_u, t_u, b_u):
    """Unity tangent w on mirrored vectors: sign(dot(cross(n', t'), b')), +1 when zero (CONTRACT 2.5)."""
    return -1.0 if dot(cross(n_u, t_u), b_u) < 0 else 1.0

def se_tangent_sign(n, t, b):
    """SE's own sign in SE space (VRage.Render11 MyMeshes): t.Cross(n).Dot(b) < 0 ? -1: 1."""
    return -1.0 if dot(cross(t, n), b) < 0 else 1.0

def aabb_corner(bmin, bmax, i):
    """Corner i of [bmin, bmax]: bit 0 -> max x, bit 1 -> max y, bit 2 -> max z (the S3.C2 corner order)."""
    return (bmax[0] if i & 1 else bmin[0], bmax[1] if i & 2 else bmin[1], bmax[2] if i & 4 else bmin[2])

def aabb_corners(bmin, bmax):
    return [aabb_corner(bmin, bmax, i) for i in range(8)]

def sn_apply_k(o, q, s, k, l_se):
    """SE-local point -> SN world with the entity scale k: o + S*(q*(k*mirror(l))) (Frame (s,1,s) / Entity (q, k))."""
    return sn_apply(o, q, s, mul(l_se, k))

def frame_entity_matrix(o, q, s, k):
    """Columns and translation of T(o) * diag(s,1,s) * R(q) * k (the Entity's localToWorldMatrix), doubles."""
    cols = tuple(scale_h(mul(qrot(q, e), k), s) for e in ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)))
    return cols, o

def affine_point(cols, t, p):
    return add(add(add(mul(cols[0], p[0]), mul(cols[1], p[1])), mul(cols[2], p[2])), t)

def rigid_draw_error(q, s, k, l_se):
    """|(S - I)*(q*(k*mirror(l)))|: where a rigid child (no scaled Frame) draws l, relative to Apply (S3.M7)."""
    r = qrot(q, mirror(mul(l_se, k)))
    return abs(s - 1.0) * math.hypot(r[0], r[2])

def anchor_angle(site, P):
    """theta of an SE point from the anchor U0: atan2(sqrt(le^2 + ln^2), lu)."""
    v = sub(P, site.C)
    return math.atan2(math.hypot(dot(v, site.E0), dot(v, site.N0)), dot(v, site.U0))

def affine_vs_f_bound(d, r, s, theta):
    """Bound of |affine - F| at distance d from the pivot: max(s, 1) d^2/(sqrt(3) (r - d)) + (theta/sin theta - 1) s d.
 The selftest's table bound (section 3) has no max(s, 1) and is used there with a 2 % slack; with the pivot away
 from the entity origin the curvature term's horizontal part is scaled by s, which the factor makes strict
 (worst ratio 0.9999 over SITE_V1 and EXAMPLE, 1-200 m boxes, y in [-3040, 350]; ROADMAP S3.C2)."""
    aniso = theta / math.sin(theta) - 1.0 if theta > 1e-8 else theta * theta / 6.0
    return max(s, 1.0) * d * d / (math.sqrt(3.0) * (r - d)) + aniso * s * d

def affine_vs_f_bound_at(site, pivot, P):
    """The bound for SE point P under a pose linearised at pivot (theta: the larger of the two anchor angles)."""
    r = norm(sub(pivot, site.C))
    return affine_vs_f_bound(norm(sub(P, pivot)), r, site.Rs / r, max(anchor_angle(site, pivot), anchor_angle(site, P)))

def armor_uv(uv, pattern_scale, offset):
    """uv / PatternScale + offset (SE's MyMeshes texcoord division + the cube-instancing pattern offset); V unflipped."""
    if not (pattern_scale > 0) or math.isinf(pattern_scale) or pattern_scale == 1.0:
        return (f32(uv[0] + offset[0]), f32(uv[1] + offset[1]))
    return (f32(f32(uv[0] / pattern_scale) + offset[0]), f32(f32(uv[1] / pattern_scale) + offset[1]))

def pattern_offset(ox, oy, px, py):
    """(offsetX / patternX, offsetY / patternY) of a part (GetCubeParts' Vector4UByte; the shader's division)."""
    return (f32(ox / max(1, px)), f32(oy / max(1, py)))

def pattern_offset_packed(w5, w6, w7):
    """The shader's decoding (VertexTemplateBase.hlsli): patternU = 1 + (w7 >> 4 & 0xF), patternV = 1 + (w7 & 0xF)."""
    return (f32(w5 / (1 + ((w7 >> 4) & 0xF))), f32(w6 / (1 + (w7 & 0xF))))

def fnv1a64(data, h=FNV64_OFFSET):
    for b in data:
        h = ((h ^ b) * FNV64_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h

def normalize_model_path(path):
    return path.strip().replace("/", "\\").lower()

def net_float_r(x):
    """.NET Framework float.ToString("R", InvariantCulture): "G7" when it round-trips through float32, else "G9"
 (the G rule: fixed notation for exponents -5 < e < precision, else d.dddE+NN / E-NN)."""
    x = f32(x)
    s = ""
    for p in (7, 9):
        s = format(x, ".%dg" % p).replace("e", "E")
        if f32(float(s)) == x:
            return s
    return s

def model_key(path, lod=0, scale=1.0):
    """C3 modelKey: FNV-1a 64 of UTF-8 'normalised path|lod<n>', plus '|scale=<R>' when the model is drawn rescaled
 (CONTRACT 5.4; ModelCodec.ModelKey; the SE plugin's S3Keys)."""
    text = normalize_model_path(path) + "|lod" + str(int(lod))
    if f32(scale) != 1.0:
        text += "|scale=" + net_float_r(scale)
    return fnv1a64(text.encode("utf-8"))

def material_key(technique, flags, cm="", ng="", add_maps="", alpha=""):
    """C3 materialKey as a content key: FNV-1a 64 of 'cm|ng|add|alpha|technique|flags' (paths normalised, numbers in
 decimal; ModelCodec.MaterialKey; the SE plugin's S3Keys)."""
    text = "%s|%s|%s|%s|%d|%d" % (normalize_model_path(cm or ""), normalize_model_path(ng or ""),
                                  normalize_model_path(add_maps or ""), normalize_model_path(alpha or ""), technique, flags)
    return fnv1a64(text.encode("utf-8"))

def cube_part_key(x, y, z, index):
    """Part key of a cube part: x, y, z (i16) and the part index in bits 0-15, 16-31, 32-47, 48-63."""
    return (x & 0xFFFF) | ((y & 0xFFFF) << 16) | ((z & 0xFFFF) << 32) | ((index & 0xFFFF) << 48)

def model_payload(m):
    """C3 model payload (CONTRACT 5.4): vertices of 56 B (pos, normal, tangent, binormal, uv; 76 B skinned with
 u8 boneIndex[4], f32 weight[4]), u32 indices (MyModel.Triangles order), 24 B submeshes. m: dict of lists."""
    skinned = bool(m.get("flags", 0) & MODEL_FLAG_SKINNED)
    out = bytearray()
    for i, p in enumerate(m["positions"]):
        out += struct.pack("<14f", *p, *m["normals"][i], *m["tangents"][i], *m["binormals"][i], *m["uvs"][i])
        if skinned:
            out += struct.pack("<4B4f", *m["bone_indices"][i], *m["bone_weights"][i])
    out += struct.pack("<%dI" % len(m["indices"]), *m["indices"])
    for (key, start, count, tech) in m["submeshes"]:
        out += struct.pack("<QIIII", key, start, count, tech, 0)
    return bytes(out)

def model_begin_payload(m, chunk_bytes):
    payload_len = len(model_payload(m))
    chunks = (payload_len + chunk_bytes - 1) // chunk_bytes
    return struct.pack("<QIIIII6ffII", m["key"], m.get("lod", 0), len(m["positions"]), len(m["indices"]),
                       len(m["submeshes"]), m.get("flags", 0), *m["bounds"], m.get("pattern_scale", 1.0), chunks, payload_len)

def model_messages(m, chunk_bytes):
    """[(type, payload bytes)] of one model: ModelBegin, ModelChunk 0..n-1, ModelEnd (hash over the whole payload)."""
    data = model_payload(m)
    msgs = [(C3_MODEL_BEGIN, model_begin_payload(m, chunk_bytes))]
    for i, off in enumerate(range(0, len(data), chunk_bytes)):
        part = data[off:off + chunk_bytes]
        msgs.append((C3_MODEL_CHUNK, struct.pack("<QII", m["key"], i, len(part)) + part))
    msgs.append((C3_MODEL_END, struct.pack("<QQ", m["key"], fnv1a64(data))))
    return msgs

def decode_model_payload(begin, payload):
    """Inverse of model_payload for a ModelBegin payload (bytes) -> dict (positions... submeshes); raises on errors."""
    key, lod, nv, ni, ns, flags = struct.unpack_from("<QIIIII", begin, 0)
    bounds = struct.unpack_from("<6f", begin, 0x1C)
    pattern, chunks, total = struct.unpack_from("<fII", begin, 0x34)
    skinned = bool(flags & MODEL_FLAG_SKINNED)
    stride = 76 if skinned else 56
    if total != len(payload) or total != nv * stride + ni * 4 + ns * 24:
        raise ValueError("payload size %d, totalBytes %d, counts imply %d" % (len(payload), total, nv * stride + ni * 4 + ns * 24))
    m = {"key": key, "lod": lod, "flags": flags, "bounds": bounds, "pattern_scale": pattern, "positions": [], "normals": [],
         "tangents": [], "binormals": [], "uvs": [], "indices": [], "submeshes": []}
    if skinned:
        m["bone_indices"], m["bone_weights"] = [], []
    o = 0
    for _ in range(nv):
        f = struct.unpack_from("<14f", payload, o)
        m["positions"].append(f[0:3]); m["normals"].append(f[3:6]); m["tangents"].append(f[6:9])
        m["binormals"].append(f[9:12]); m["uvs"].append(f[12:14])
        if skinned:
            m["bone_indices"].append(struct.unpack_from("<4B", payload, o + 0x38))
            m["bone_weights"].append(struct.unpack_from("<4f", payload, o + 0x3C))
        o += stride
    m["indices"] = list(struct.unpack_from("<%dI" % ni, payload, o))
    o += 4 * ni
    for _ in range(ns):
        k, st, ct, te, pad = struct.unpack_from("<QIIII", payload, o)
        if pad:
            raise ValueError("submesh pad not zero")
        m["submeshes"].append((k, st, ct, te))
        o += 24
    if any(i >= nv for i in m["indices"]) or ni % 3:
        raise ValueError("index out of range or not whole triangles")
    return m

def material_def_payload(key, technique, flags, cm="", ng="", add_maps="", alpha=""):
    """C3 MaterialDef: u64 materialKey, u32 drawTechnique, u32 flags, then four u16-prefixed UTF-8 strings."""
    out = bytearray(struct.pack("<QII", key, technique, flags))
    for s in (cm, ng, add_maps, alpha):
        b = (s or "").encode("utf-8")
        out += struct.pack("<H", len(b)) + b
    return bytes(out)

def part_add_payload(entity_id, part_key, mkey, local_rows, hsv, uv_offset, skin_id=0, flags=0):
    """C4 PartAdd (100 B): ids, local[12] (rows Right, Up, Backward, Translation), colorMaskHSV[3], uvOffset[2], skinId, flags."""
    return struct.pack("<QQQ12f3f2fII", entity_id, part_key, mkey, *local_rows, *hsv, *uv_offset, skin_id, flags)

def cube_model(h=1.25, mat_key=0, technique=0, pattern_scale=1.0, path="de/s3/cube_2.5m.mwm"):
    """A closed box of half extent h in SE model space with UVs, normals, tangents (= dP/du) and binormals (= dP/dv),
 triangles in the MyModel.Triangles convention (cross(V1 - V0, V2 - V0) outward), one submesh. The vertex order is
 cube_mesh's (4 per face; per face u along a1, v along a2, uv = ((i + 1)/2, (j + 1)/2))."""
    pos, nrm, tan, bin_, uvs, idx = [], [], [], [], [], []
    for axis in range(3):
        for sign in (-1.0, 1.0):
            n = [0.0, 0.0, 0.0]
            n[axis] = sign
            a1 = [0.0, 0.0, 0.0]
            a2 = [0.0, 0.0, 0.0]
            a1[(axis + 1) % 3] = 1.0
            a2[(axis + 2) % 3] = 1.0
            base = len(pos)
            for (i, j) in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                pos.append(tuple(h * (n[k] + i * a1[k] + j * a2[k]) for k in range(3)))
                nrm.append(tuple(n))
                tan.append(tuple(a1))
                bin_.append(tuple(a2))
                uvs.append(((i + 1) / 2.0, (j + 1) / 2.0))
            for t in ((0, 1, 2), (0, 2, 3)):
                a, b, c = (pos[base + t[0]], pos[base + t[1]], pos[base + t[2]])
                tt = (base + t[0], base + t[1], base + t[2])
                if dot(cross(sub(b, a), sub(c, a)), tuple(n)) < 0:
                    tt = (tt[0], tt[2], tt[1])
                idx.extend(tt)
    return {"key": model_key(path, 0), "path": path, "lod": 0, "flags": MODEL_FLAG_HAS_TANGENTS,
            "bounds": (-h, -h, -h, h, h, h), "pattern_scale": pattern_scale, "positions": pos, "normals": nrm,
            "tangents": tan, "binormals": bin_, "uvs": uvs, "indices": idx, "submeshes": [(mat_key, 0, len(idx) // 3, technique)]}

def tris_of(m):
    ix = m["indices"]
    return [(ix[i], ix[i + 1], ix[i + 2]) for i in range(0, len(ix), 3)]

def lab_block(site, sn_point=(-1029.0, -150.0, -1116.0), yaw_deg=25.0, tilt_deg=0.0, tilt_az_deg=0.0):
    """SE world matrix of a block (grid) centred at F^-1(sn_point): level (or tilted by tilt_deg towards azimuth
 tilt_az_deg) and turned by yaw_deg about the local vertical. Rows Right, Up, Backward, Translation."""
    T = site.sn_to_se(sn_point)
    E, U, N = site.frame_at(T)
    t, az = math.radians(tilt_deg), math.radians(tilt_az_deg)
    up = add(mul(U, math.cos(t)), mul(add(mul(E, math.cos(az)), mul(N, math.sin(az))), math.sin(t)))
    yaw = math.radians(yaw_deg)
    f = add(mul(N, math.cos(yaw)), mul(E, math.sin(yaw)))
    f = unit(sub(f, mul(up, dot(f, up))))
    return (cross(f, up), up, mul(f, -1.0), T)

def selftest_s3(site, check):
    """S3 conventions (ROADMAP S3, docs/CONTRACT.md 2.5): own RNG, so the numbers above stay those of S0."""
    rnd = random.Random(30030016)
    print("\nS3: mesh conventions, entity scale k, Frame/Entity shear, C3 payloads")
    cube = cube_model()
    vs, ns, ts = cube["positions"], cube["normals"], tris_of(cube)
    vu, nu, tu = mesh_to_unity(vs, ns, ts)
    good = winding_measure(vu, nu, tu)
    no_swap = winding_measure(vu, nu, ts)                                   # S3.M2
    no_mirror = winding_measure(vs, ns, tu)                                 # S3.M1 (mirror dropped, swap kept)
    check("S3 winding", good == (12, 0, 0) and no_swap == (0, 12, 0) and no_mirror == (0, 12, 0)
          and winding_fraction(no_swap) < WINDING_FLIP_BELOW,
          f"cube after mirror + (I0, I2, I1): {good}; without the swap (S3.M2): {no_swap} -> flip; "
          f"without the mirror (S3.M1): {no_mirror}")
    worst_w = 0
    for _ in range(20000):
        n, t, b = random_unit(rnd), random_unit(rnd), random_unit(rnd)
        worst_w += tangent_w(mirror(n), mirror(t), mirror(b)) != se_tangent_sign(n, t, b)
    check("S3 tangent w", worst_w == 0, f"Unity sign(dot(cross(n',t'),b')) on mirrored vectors == SE's sign(dot(cross(t,n),b)) "
          f"in 20000/20000 random frames (mismatches {worst_w}): the mirror does not change w")
    # Frame (s,1,s) / Entity (q, k) composition == Apply with k
    worst = 0.0
    for _ in range(2000):
        q = random_quat(rnd)
        s = rnd.choice((1.0284, 1.0345, 0.9942, 1.0))
        k = rnd.choice((1.0, 0.5, 2.0))
        o = (rnd.uniform(-2000, 2000), rnd.uniform(-2000, 0), rnd.uniform(-2000, 2000))
        cols, t0 = frame_entity_matrix(o, q, s, k)
        for c in aabb_corners((-1.25, -1.25, -1.25), (1.25, 1.25, 1.25)):
            worst = max(worst, norm(sub(affine_point(cols, t0, mirror(c)), sn_apply_k(o, q, s, k, c))))
    check("S3 Frame/Entity", worst < 1e-9, f"T(o) diag(s,1,s) R(q) k applied to Mirror(corner) == o + S q k Mirror(corner): worst {worst:.1e} m")
    # S3.G1-E2 / S3.M7: a rigid child (no scaled Frame) is off by exactly rigid_draw_error
    print("  rigid draw (S3.M7) vs Apply, 2.5 m cube rotating 360 deg in 1 deg steps about a tilted axis:")
    axis = unit((0.3, 1.0, 0.45))
    for s in (1.0284, 1.0345):
        worst_dev = worst_mis = 0.0
        for deg in range(360):
            a = math.radians(deg)
            q = tuple(c * math.sin(a / 2) for c in axis) + (math.cos(a / 2),)
            q = tuple(-c for c in q) if q[3] < 0 else q
            for c in aabb_corners((-1.25, -1.25, -1.25), (1.25, 1.25, 1.25)):
                rigid = add((0.0, 0.0, 0.0), qrot(q, mirror(c)))
                exact = sn_apply_k((0.0, 0.0, 0.0), q, s, 1.0, c)
                dev = norm(sub(rigid, exact))
                worst_dev = max(worst_dev, dev)
                worst_mis = max(worst_mis, abs(dev - rigid_draw_error(q, s, 1.0, c)))
        print(f"    Frame ({s}, 1, {s}): rigid-draw deviation up to {worst_dev*1000:.2f} mm (equals the formula to {worst_mis:.1e} m)")
        ok_m7 = worst_mis < 1e-12 and worst_dev > 1e-4
        check(f"S3 rigid draw s={s}", ok_m7, "S3.M7 would read the rigid-draw error, far above the 1e-4 m threshold")
    # affine vs F with the pivot at the camera's nearest box point (not the entity origin)
    worst_ratio = 0.0
    for _ in range(3000):
        qsn = (rnd.uniform(-2048, 2048), rnd.uniform(-1657, 0), rnd.uniform(-2048, 2048))
        m = random_se_matrix(rnd, site.sn_to_se(qsn))
        L = rnd.choice((2.5, 20.0, 100.0))
        bmin, bmax = (-L / 2, -L / 2, -L / 2), (L / 2, L / 2, L / 2)
        cam = se_transform(m, (rnd.uniform(-2 * L, 2 * L), rnd.uniform(-L, L), rnd.uniform(-2 * L, 2 * L)))
        P = pivot_nearest(m, bmin, bmax, cam)
        o, q, s = wire_pose(site, m, P)
        for c in aabb_corners(bmin, bmax):
            Pc = se_transform(m, c)
            err = norm(sub(sn_apply(o, q, s, c), site.se_to_sn(Pc)))
            bound = affine_vs_f_bound_at(site, P, Pc)
            worst_ratio = max(worst_ratio, err / bound if bound > 0 else (0.0 if err < 1e-9 else float("inf")))
    check("S3 affine vs F", worst_ratio <= 1.0, f"|Apply - F(corner)| / bound over 3000 boxes (2.5-100 m) with P* at the camera's "
          f"nearest point: worst ratio {worst_ratio:.3f}")
    # the lab block (SN -1029, -150, -1116), camera P1 6 m in front
    m = lab_block(site)
    cam = se_transform(m, (0.0, 0.0, -1.25 - 6.0))
    P = pivot_nearest(m, (-1.25,) * 3, (1.25,) * 3, cam)
    o, q, s = wire_pose(site, m, P)
    errs = [norm(sub(sn_apply(o, q, s, c), site.se_to_sn(se_transform(m, c)))) for c in aabb_corners((-1.25,) * 3, (1.25,) * 3)]
    bounds = [affine_vs_f_bound_at(site, P, se_transform(m, c)) for c in aabb_corners((-1.25,) * 3, (1.25,) * 3)]
    o32 = f32v(o)
    q32 = f32v(q)
    s32 = f32(s)
    e32 = max(norm(sub(sn_apply(o32, q32, s32, c), sn_apply(o, q, s, c))) for c in aabb_corners((-1.25,) * 3, (1.25,) * 3))
    check("S3 lab block", max(e / b for e, b in zip(errs, bounds)) <= 1.0 and e32 < 1e-3,
          f"2.5 m block at SN (-1029, -150, -1116), P* nearest to a camera 6 m ahead: |Apply - F| up to {max(errs)*1000:.4f} mm "
          f"(bound {max(bounds)*1000:.4f} mm); float32 o, q, s move a corner by {e32*1000:.4f} mm (S3.C2 threshold 1 mm)")
    # C3 payload round trip and message framing
    msgs = model_messages(cube, 256)
    data = b"".join(p[16:] for t, p in msgs if t == C3_MODEL_CHUNK)
    back = decode_model_payload(msgs[0][1], data)
    same = all(back[k] == [tuple(map(float, x)) for x in cube[k]] for k in ("positions", "normals", "tangents", "binormals", "uvs")) \
        and back["indices"] == cube["indices"] and back["submeshes"] == [tuple(x) for x in cube["submeshes"]]
    end_hash = struct.unpack_from("<Q", msgs[-1][1], 8)[0]
    check("S3 model payload", same and end_hash == fnv1a64(data) and len(data) == 24 * 56 + 36 * 4 + 24,
          f"cube payload {len(data)} B in {len(msgs) - 2} chunks of <= 256 B, decoded back equal, ModelEnd hash 0x{end_hash:016x}")

def s3_vector_records():
    """Deterministic S3 vectors for the C# tests (tests/DeepEngineers.WireMath.Tests/S3VectorTests.cs)."""
    site = site_by_name("v1")
    rnd = random.Random(30030017)
    out = [{"kind": "header", "generator": "tools/worldmap/wire_ref.py s3vectors", "site": "SITE_V1",
            "conventions": {"corner_order": "bit0 max x, bit1 max y, bit2 max z (SE-local)",
                            "apply_k": "o + S*(q*(k*mirror(l)))",
                            "bound": "max(s,1) d^2/(sqrt3 (r - d)) + (theta/sin theta - 1) s d, theta = max(anchor angle of P*, of the point)",
                            "model_key": "fnv1a64(utf8(normalised path + '|lod' + lod [+ '|scale=' + .NET float R]))",
                            "material_key": "fnv1a64(utf8('cm|ng|add|alpha|technique|flags'))"}}]
    boxes = [((-1.25, -1.25, -1.25), (1.25, 1.25, 1.25)), ((-3.75, -1.25, -3.75), (3.75, 1.25, 3.75)),
             ((-10.0, -4.0, -30.0), (10.0, 6.0, 25.0))]
    cases = [lab_block(site), lab_block(site, yaw_deg=-60.0, tilt_deg=20.0, tilt_az_deg=130.0)]
    for _ in range(120):
        qsn = (rnd.uniform(-7800, 7800), rnd.uniform(*POSE_Y_RANGE), rnd.uniform(-7800, 7800))
        cases.append(random_se_matrix(rnd, site.sn_to_se(qsn)))
    for n, m in enumerate(cases):
        bmin, bmax = boxes[n % len(boxes)]
        L = max(bmax[i] - bmin[i] for i in range(3))
        cam = se_transform(m, (rnd.uniform(-2 * L, 2 * L), rnd.uniform(-L, L), rnd.uniform(-2 * L, 2 * L)))
        P = pivot_nearest(m, bmin, bmax, cam)
        k = (1.0, 1.0, 0.5, 2.0)[n % 4]
        o, q, s = wire_pose(site, m, P)
        corners = aabb_corners(bmin, bmax)
        out.append({"kind": "pose_k", "se_matrix": {"right": r(m[0]), "up": r(m[1]), "backward": r(m[2]), "translation": r(m[3])},
                    "pivot_se": r(P), "k": k, "o": r(o), "q": r(q), "s": s, "box_min": r(bmin), "box_max": r(bmax),
                    "corners_se": [r(c) for c in corners],
                    "corners_sn": [r(sn_apply_k(o, q, s, k, c)) for c in corners],
                    "exact_sn": [r(site.se_to_sn(se_transform(m, mul(c, k)))) for c in corners],
                    "bound": [affine_vs_f_bound_at(site, P, se_transform(m, mul(c, k))) for c in corners],
                    "rigid_err": [rigid_draw_error(q, s, k, c) for c in corners]})
    cube = cube_model(mat_key=material_key(0, 0, r"C:\SE\Content\Textures\Models\Cubes\large_edges_cm.dds"))
    vu, nu, tu = mesh_to_unity(cube["positions"], cube["normals"], tris_of(cube))
    msgs = model_messages(cube, 256)
    out.append({"kind": "mesh_cube", "path": cube["path"], "model_key": str(cube["key"]), "flags": cube["flags"],
                "bounds": list(cube["bounds"]), "pattern_scale": cube["pattern_scale"],
                "positions": [r(v) for v in cube["positions"]], "normals": [r(v) for v in cube["normals"]],
                "tangents": [r(v) for v in cube["tangents"]], "binormals": [r(v) for v in cube["binormals"]],
                "uvs": [list(v) for v in cube["uvs"]], "indices": cube["indices"],
                "submeshes": [[str(a), b, c, d] for (a, b, c, d) in cube["submeshes"]],
                "unity_positions": [r(v) for v in vu], "unity_tris": [list(t) for t in tu],
                "winding": list(winding_measure(vu, nu, tu)), "winding_no_swap": list(winding_measure(vu, nu, tris_of(cube))),
                "tangent_w": [tangent_w(mirror(n), mirror(t), mirror(b)) for n, t, b in
                              zip(cube["normals"], cube["tangents"], cube["binormals"])],
                "payload_hex": model_payload(cube).hex(), "payload_hash": str(fnv1a64(model_payload(cube))),
                "chunk_bytes": 256, "messages": [[t, p.hex()] for t, p in msgs]})
    for _ in range(64):
        n, t, b = random_unit(rnd), random_unit(rnd), random_unit(rnd)
        out.append({"kind": "tangent", "n": r(n), "t": r(t), "b": r(b), "w": tangent_w(mirror(n), mirror(t), mirror(b)),
                    "se_sign": se_tangent_sign(n, t, b)})
    paths = (("Models\\Cubes\\Large\\ArmorCube.mwm", 0, 1.0), ("models/cubes/large/armorcube.mwm", 0, 1.0),
             ("Models\\Cubes\\Large\\ArmorCube.mwm", 1, 1.0), ("  C:\\Mods\\Été\\Bloc.MWM ", 2, 1.0),
             ("Models\\Cubes\\Small\\ArmorCube.mwm", 0, 0.2), ("a.mwm", 0, 0.5), ("a.mwm", 0, 1.0 / 3.0),
             ("a.mwm", 0, 1e-5), ("a.mwm", 0, 12345678.0), ("a.mwm", 0, 2.0), ("a.mwm", 0, 0.1), ("a.mwm", 0, 16777217.0))
    for p, lod, sc in paths:
        out.append({"kind": "model_key", "path": p, "lod": lod, "scale": f32(sc),
                    "scale_text": net_float_r(sc) if f32(sc) != 1.0 else "", "key": str(model_key(p, lod, sc))})
    mats = ((0, 0, r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Textures\Models\Cubes\large_edges_cm.dds",
             r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Textures\Models\Cubes\large_edges_ng.dds",
             r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Textures\Models\Cubes\large_edges_add.dds", ""),
            (15, 1, "C:/Glass/Fenêtre_cm.dds", "", "", ""), (3, 4, "", "", "", r"D:\alpha\mask.dds"))
    for (tech, flags, cm, ng, ad, al) in mats:
        key = material_key(tech, flags, cm, ng, ad, al)
        out.append({"kind": "material", "technique": tech, "flags": flags, "cm": cm, "ng": ng, "add": ad, "alpha": al,
                    "key": str(key), "payload_hex": material_def_payload(key, tech, flags, cm, ng, ad, al).hex()})
    for _ in range(16):
        m = random_se_matrix(rnd, (rnd.uniform(-10, 10), rnd.uniform(-10, 10), rnd.uniform(-10, 10)))
        rows = f32v(m[0]) + f32v(m[1]) + f32v(m[2]) + f32v(m[3])
        hsv = f32v((rnd.uniform(0, 1), rnd.uniform(-1, 1), rnd.uniform(-1, 1)))
        uvo = pattern_offset(rnd.randrange(0, 4), rnd.randrange(0, 4), rnd.randrange(1, 5), rnd.randrange(1, 5))
        ids = (rnd.getrandbits(64), cube_part_key(rnd.randrange(-300, 300), rnd.randrange(-300, 300), rnd.randrange(-300, 300),
                                                   rnd.randrange(0, 8)), rnd.getrandbits(64))
        out.append({"kind": "part_add", "entity_id": str(ids[0]), "part_key": str(ids[1]), "model_key": str(ids[2]),
                    "local": list(rows), "hsv": list(hsv), "uv_offset": list(uvo), "skin_id": 3, "flags": 5,
                    "payload_hex": part_add_payload(ids[0], ids[1], ids[2], rows, hsv, uvo, 3, 5).hex()})
    for (x, y, z, i) in ((0, 0, 0, 0), (-1, 2, -3, 1), (300, -300, 7, 5)):
        out.append({"kind": "cube_part_key", "x": x, "y": y, "z": z, "index": i, "key": str(cube_part_key(x, y, z, i))})
    for _ in range(16):
        ox, oy, px, py = rnd.randrange(0, 16), rnd.randrange(0, 16), rnd.randrange(1, 17), rnd.randrange(1, 17)
        w7 = ((px - 1) << 4) | (py - 1)
        uv = f32v((rnd.uniform(0, 4), rnd.uniform(0, 4)))
        ps = rnd.choice((1.0, 2.0, 4.0, 3.0))
        out.append({"kind": "uv", "ox": ox, "oy": oy, "px": px, "py": py, "w7": w7, "offset": list(pattern_offset(ox, oy, px, py)),
                    "offset_packed": list(pattern_offset_packed(ox, oy, w7)), "uv": list(uv), "pattern_scale": ps,
                    "armor_uv": list(armor_uv(uv, ps, pattern_offset(ox, oy, px, py)))})
    return out

def s3_vectors(path):
    recs = s3_vector_records()
    text = "".join(json.dumps(rec, separators=(",", ":"), ensure_ascii=False) + "\n" for rec in recs)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print(f"S3 vectors: wrote {len(recs)} records, sha256 {hashlib.sha256(text.encode()).hexdigest()} -> {path}")

# ---------------------------------------------------------------- selftest
def selftest(site_name="v1"):
    site = site_by_name(site_name)
    print(f"=== wire_ref selftest, site {SITES[site_name]} (Rs {site.Rs:.8f} m, C {site.C}) ===")
    rnd = random.Random(1210014)
    ok = True

    def check(name, cond, detail):
        nonlocal ok
        ok &= bool(cond)
        print(f"[{'PASS' if cond else 'FAIL'}] {name}: {detail}")

    # 1) the pose quaternion maps Unity local axes onto mapdir(Right, Up, Forward)
    worst = 0.0
    for _ in range(5000):
        qsn = (rnd.uniform(-2048, 2048), rnd.uniform(-1700, 150), rnd.uniform(-2048, 2048))
        m = random_se_matrix(rnd, site.sn_to_se(qsn))
        o, q, s = wire_pose(site, m, m[3])
        R, U, B, T = m
        for loc, se_axis in (((1, 0, 0), R), ((0, 1, 0), U), ((0, 0, 1), mul(B, -1.0))):
            worst = max(worst, norm(sub(qrot(q, loc), site.dir_se_to_sn(T, se_axis))))
    check("pose axes", worst < 1e-12, f"q*x=mapdir(Right), q*y=mapdir(Up), q*z=mapdir(Forward), worst {worst:.1e}")

    # 2) handedness: the coordinate map is improper, q is proper, mirror is improper, S positive
    def det3(a, b, c):
        return dot(a, cross(b, c))
    worst_det = -1e9
    for _ in range(500):
        qsn = (rnd.uniform(-2048, 2048), rnd.uniform(-1700, 150), rnd.uniform(-2048, 2048))
        P = site.sn_to_se(qsn)
        eps = 1e-3
        cols = [mul(sub(site.se_to_sn(add(P, e)), site.se_to_sn(P)), 1 / eps)
                for e in ((eps, 0, 0), (0, eps, 0), (0, 0, eps))]
        worst_det = max(worst_det, det3(*cols))
    check("handedness", worst_det < 0, f"det of the numerical Jacobian of F is negative everywhere (max {worst_det:.4f}); "
          "det(q)=+1, det(mirror)=-1, det(S)=s^2>0, so S*q*mirror has the sign of F")

    # 3) first-order agreement of the affine with the exact map, pivot at the entity origin
    print("\naffine (o, q, s) vs exact map F, pivot at the entity origin, worst over the tile (|x|,|z|<=2048):")
    th = math.hypot(2048, 2048) / site.Rs
    aniso = th / math.sin(th) - 1
    for d in (1.0, 10.0, 50.0, 100.0, 200.0):
        worst = 0.0
        bound = 0.0
        for _ in range(3000):
            qsn = (rnd.uniform(-2048, 2048), rnd.uniform(-1657, 0), rnd.uniform(-2048, 2048))
            m = random_se_matrix(rnd, site.sn_to_se(qsn))
            o, q, s = wire_pose(site, m, m[3])
            l = mul(random_unit(rnd), d)
            exact = site.se_to_sn(se_transform(m, l))
            worst = max(worst, norm(sub(sn_apply(o, q, s, l), exact)))
            r = norm(sub(m[3], site.C))
            # second order: h^2/2r vertically from a horizontal offset h, h*v/r horizontally from a vertical
 # offset v; the worst split of |l| = d gives d^2/(sqrt(3) r). Plus the transverse anisotropy.
            bound = max(bound, d * d / (math.sqrt(3) * (r - d)) + aniso * s * d)
        print(f"  |l| = {d:6.1f} m: worst {worst:.4e} m   bound d^2/(sqrt3 r) + (theta/sin theta - 1) s d = {bound:.4e} m")
        ok &= worst <= bound * 1.02 + 1e-9

    # 4) pivot policy: error at the camera and over the hull, SE camera inside or outside a hull
    print("\npivot policy: max |affine - F| on the hull surface (m), at a cockpit point ~1 m from the camera (m),")
    print("and the largest angle (deg) between where a hull point is drawn and where it belongs, seen from the camera")
    print(f"  {'hull LxWxH m':>14} {'depth':>6} {'camera':>8} | {'origin@end':>11} {'centre':>8} {'nearest':>8} | "
          f"{'cockpit: end':>12} {'centre':>8} {'nearest':>8} | {'angle: end':>10} {'centre':>7} {'nearest':>7}")
    for (L, W, H) in ((20, 6, 6), (100, 25, 25), (200, 40, 40), (500, 100, 60)):
        for depth in (200.0, 1657.0):
            for cam_case in ("inside", "outside"):
                qsn0 = (300.0, -depth, -500.0)
                C0 = site.sn_to_se(qsn0)
                E, U, N = site.frame_at(C0)
                yaw = math.radians(37.0)
                fwd = add(mul(N, math.cos(yaw)), mul(E, math.sin(yaw)))
                m_centre = (cross(fwd, U), U, mul(fwd, -1.0), C0)  # level hull, centre at C0
                bmin, bmax = (-W / 2, -H / 2, -L / 2), (W / 2, H / 2, L / 2)
                # SE grid origin at the stern end (local z = +L/2): same rows, translated
                T_end = se_transform(m_centre, (0, 0, L / 2))
                m_end = (m_centre[0], m_centre[1], m_centre[2], T_end)
                bmin_e, bmax_e = (bmin[0], bmin[1], bmin[2] - L / 2), (bmax[0], bmax[1], bmax[2] - L / 2)
                # camera near the bow (local z = -L/2 + 2 m in centre coordinates)
                cam_l = (0.0, 0.5, -L / 2 + 2.0) if cam_case == "inside" else (0.0, 0.5, -L / 2 - 20.0)
                cam = se_transform(m_centre, cam_l)
                cockpit_l = add(cam_l, (0.6, -0.4, -0.7)) if cam_case == "inside" else (0.0, 0.5, -L / 2)
                res = []
                for which in ("end", "centre", "nearest"):
                    if which == "end":
                        m, P = m_end, T_end
                        to_l = lambda lc: (lc[0], lc[1], lc[2] - L / 2)  # noqa: E731
                    elif which == "centre":
                        m, P = m_centre, C0
                        to_l = lambda lc: lc  # noqa: E731
                    else:
                        m, P = m_centre, pivot_nearest(m_centre, bmin, bmax, cam)
                        to_l = lambda lc: lc  # noqa: E731
                    o, q, s = wire_pose(site, m, P)
                    cam_sn = site.se_to_sn(cam)  # the camera is always mapped exactly
                    worst = worst_ang = 0.0
                    samples = [cockpit_l]
                    for _ in range(1500):
                        face = rnd.randrange(3)
                        lc = [rnd.uniform(bmin[k], bmax[k]) for k in range(3)]
                        lc[face] = bmin[face] if rnd.random() < 0.5 else bmax[face]
                        samples.append(tuple(lc))
                    for lc in samples:
                        drawn = sn_apply(o, q, s, to_l(lc))
                        true = site.se_to_sn(se_transform(m_centre, lc))
                        worst = max(worst, norm(sub(drawn, true)))
                        a, b = sub(drawn, cam_sn), sub(true, cam_sn)
                        if norm(b) > 0.3:
                            cosang = max(-1.0, min(1.0, dot(a, b) / (norm(a) * norm(b))))
                            worst_ang = max(worst_ang, math.degrees(math.acos(cosang)))
                    ck = norm(sub(sn_apply(o, q, s, to_l(cockpit_l)), site.se_to_sn(se_transform(m_centre, cockpit_l))))
                    res.append((worst, ck, worst_ang))
                print(f"  {L:>4}x{W:>3}x{H:>3}   {depth:6.0f} {cam_case:>8} | {res[0][0]:11.4f} {res[1][0]:8.4f} {res[2][0]:8.4f} | "
                      f"{res[0][1]:12.5f} {res[1][1]:8.5f} {res[2][1]:8.5f} | {res[0][2]:10.3f} {res[1][2]:7.3f} {res[2][2]:7.3f}")

    # 5) winding: SE convention -> mirror -> (I0, I2, I1) is front-facing (clockwise) on a Unity camera
    verts, tris, normals = cube_mesh()
    good = bad_mutant = cw = 0
    for t, n in zip(tris, normals):
        a, b, c = (mirror(verts[i]) for i in tri_to_unity(t))
        nu = mirror(n)
        good += dot(cross(sub(b, a), sub(c, a)), nu) > 0
        am, bm, cm = (mirror(verts[i]) for i in t)
        bad_mutant += dot(cross(sub(bm, am), sub(cm, am)), nu) > 0
        cam_pos = mul(nu, 6.0)
        up_hint = (0.0, 1.0, 0.0) if abs(nu[1]) < 0.9 else (1.0, 0.0, 0.0)
        cam_q = look_rotation(mul(nu, -1.0), up_hint)
        cw += screen_area_unity(cam_pos, cam_q, (a, b, c)) < 0
    check("winding", good == len(tris) and cw == len(tris) and bad_mutant == 0,
          f"{good}/{len(tris)} Unity triangles have cross(b-a,c-a) along the outward normal and {cw}/{len(tris)} are "
          f"clockwise on a Unity camera outside the face; mutant without the swap: {bad_mutant}/{len(tris)}")
    # and the pose (rotation + positive non-uniform scale) preserves it
    m = random_se_matrix(rnd, site.sn_to_se((1500.0, -1600.0, 1500.0)))
    o, q, s = wire_pose(site, m, m[3])
    keep = 0
    for t, n in zip(tris, normals):
        a, b, c = (sn_apply(o, q, s, verts[i]) for i in tri_to_unity(t))
        nw = sn_apply_dir(q, s, n)
        keep += dot(cross(sub(b, a), sub(c, a)), nw) > 0
    check("winding under pose", keep == len(tris), f"{keep}/{len(tris)} still outward after o + S q (det > 0)")

    # 6) SN -> SE local round trip (damage points), double and with a float32 wire
    worst = worst32 = 0.0
    for _ in range(20000):
        qsn = (rnd.uniform(-7800, 7800), rnd.uniform(-1920, 150), rnd.uniform(-7800, 7800))
        m = random_se_matrix(rnd, site.sn_to_se(qsn))
        o, q, s = wire_pose(site, m, m[3])
        l = mul(random_unit(rnd), rnd.uniform(0, 150))
        w = sn_apply(o, q, s, l)
        worst = max(worst, norm(sub(sn_to_local_se(o, q, s, w), l)))
        o32, q32, s32 = f32v(o), f32v(q), f32(s)
        w32 = f32v(sn_apply(o32, q32, s32, l))
        worst32 = max(worst32, norm(sub(sn_to_local_se(o32, q32, s32, w32), l)))
    check("local round trip", worst < 1e-9 and worst32 < 1e-3,
          f"SE local -> SN world -> SE local: {worst:.1e} m in doubles, {worst32*1000:.3f} mm with float32 o, q, s and world point")

    # 7) float32 wire vs double affine (what SN draws vs what the SE plugin computed)
    worst = 0.0
    for _ in range(20000):
        qsn = (rnd.uniform(-7800, 7800), rnd.uniform(-1920, 150), rnd.uniform(-7800, 7800))
        m = random_se_matrix(rnd, site.sn_to_se(qsn))
        o, q, s = wire_pose(site, m, m[3])
        l = mul(random_unit(rnd), rnd.uniform(0, 150))
        worst = max(worst, norm(sub(sn_apply(f32v(o), f32v(q), f32(s), l), sn_apply(o, q, s, l))))
    check("float32 wire", worst < 1e-3, f"float32 (o, q, s) moves a point up to 150 m from the origin by {worst*1000:.3f} mm")

    # 8) velocities and angular velocities
    worst_v = worst_w = 0.0
    for _ in range(2000):
        qsn = (rnd.uniform(-2048, 2048), rnd.uniform(-1657, 0), rnd.uniform(-2048, 2048))
        m = random_se_matrix(rnd, site.sn_to_se(qsn))
        P = m[3]
        v = mul(random_unit(rnd), 20.0)
        dt = 1e-4
        fd = mul(sub(site.se_to_sn(add(P, mul(v, dt))), site.se_to_sn(P)), 1 / dt)
        worst_v = max(worst_v, norm(sub(fd, vel_to_sn(site, P, v))) / 20.0)
        w = mul(random_unit(rnd), 0.8)
        dt = 1e-6
        m2 = se_rotate_rows_world(m, w, norm(w) * dt)
        _, q1, _ = wire_pose(site, m, P)
        _, q2, _ = wire_pose(site, m2, P)
        dq = qmul(q2, qconj(q1))
        if dq[3] < 0:
            dq = tuple(-c for c in dq)
        ang = 2 * math.atan2(norm(dq[:3]), dq[3])
        w_meas = mul(unit(dq[:3]), ang / dt) if ang > 0 else (0.0, 0.0, 0.0)
        worst_w = max(worst_w, norm(sub(w_meas, angvel_to_sn(site, P, w))) / 0.8)
    check("velocity", worst_v < 1e-3, f"v_SN = S*mapdir(v_SE) vs finite difference of F: relative {worst_v:.1e}")
    check("angular velocity", worst_w < 1e-4,
          f"omega_SN = -mapdir(omega_SE) (Unity AngleAxis numeric convention) vs quaternion difference: relative {worst_w:.1e}")

    # 9) collider pre-scaling vs rigid sections, sections 16 m, boxes up to 4 m half extent
    print("\ncollider placement error (corner of box vs exact affine), section 16 m cube, box half extents <= 4 m:")
    print(f"  {'depth m':>8} {'tilt':>5} | {'rigid section':>13} {'K pre-scaled':>13} | {'level-ship K':>12}")
    for depth in (200.0, 500.0, 1000.0, 1657.0):
        for tilt in (0.0, 15.0, 45.0):
            worst_r = worst_k = 0.0
            for _ in range(400):
                qsn = (rnd.uniform(-2000, 2000), -depth, rnd.uniform(-2000, 2000))
                T = site.sn_to_se(qsn)
                m = random_se_matrix(rnd, T, tilt_max_deg=tilt if tilt > 0 else 1e-9, site=site)
                if tilt > 0:  # force exactly that tilt for the worst case
                    E, U, N = site.frame_at(T)
                    az = rnd.uniform(0, 2 * math.pi)
                    up = add(mul(U, math.cos(math.radians(tilt))),
                             mul(add(mul(E, math.cos(az)), mul(N, math.sin(az))), math.sin(math.radians(tilt))))
                    f = unit(sub(random_unit(rnd), mul(up, 0)))
                    f = unit(sub(f, mul(up, dot(f, up))))
                    m = (cross(f, up), up, mul(f, -1.0), T)
                o, q, s = wire_pose(site, m, T)
                c_sec = (rnd.uniform(-50, 50), rnd.uniform(-10, 10), rnd.uniform(-50, 50))
                sec_world = sn_apply(o, q, s, c_sec)
                h = (rnd.uniform(0.5, 4), rnd.uniform(0.5, 4), rnd.uniform(0.5, 4))
                b = tuple(c_sec[k] + rnd.uniform(-8 + h[k], 8 - h[k]) for k in range(3))
                axes = ((1.0, 0, 0), (0, 1.0, 0), (0, 0, 1.0))
                centre_k, h_k, axes_u = box_prescale(q, s, c_sec, b, h, axes)
                centre_r = mirror(sub(b, c_sec))
                for sx in (-1, 1):
                    for sy in (-1, 1):
                        for sz in (-1, 1):
                            corner_se = (b[0] + sx * h[0], b[1] + sy * h[1], b[2] + sz * h[2])
                            exact = sn_apply(o, q, s, corner_se)
                            ck = add(sec_world, qrot(q, add(centre_k, (sx * h_k[0], sy * h_k[1], -sz * h_k[2]))))
                            cr = add(sec_world, qrot(q, add(centre_r, (sx * h[0], sy * h[1], -sz * h[2]))))
                            worst_k = max(worst_k, norm(sub(ck, exact)))
                            worst_r = max(worst_r, norm(sub(cr, exact)))
            print(f"  {depth:8.0f} {tilt:4.0f}d | {worst_r:13.4f} {worst_k:13.4f} | {'exact' if tilt == 0 and worst_k < 1e-9 else '':>12}")
            if tilt == 0:
                ok &= worst_k < 1e-9

    # 10) HUD / crosshair alignment under display design F: SE projects its HUD with its own camera in SE space,
 # Subnautica draws the mirrored world in SN space with an unscaled camera. Angle between the two for an
 # SE point on the SE camera's view axis (the crosshair), by look elevation and depth.
    print("\ncrosshair misalignment (deg) of an SE point on the SE view axis, 10 m ahead, unscaled SN camera:")
    print(f"  {'depth m':>8} | " + " ".join(f"{e:>6}d" for e in (0, 15, 30, 45, 60, 90)))
    for depth in (100.0, 500.0, 1000.0, 1657.0):
        row = []
        for elev in (0, 15, 30, 45, 60, 90):
            T = site.sn_to_se((-400.0, -depth, 250.0))
            E, U, N = site.frame_at(T)
            e = math.radians(elev)
            fwd = add(mul(N, math.cos(e)), mul(U, math.sin(e)))
            up = add(mul(N, -math.sin(e)), mul(U, math.cos(e)))
            cam = (cross(fwd, up), up, mul(fwd, -1.0), T)
            cpos, cq = camera_pose(site, cam)
            target = add(T, mul(fwd, 10.0))
            o, q, s = wire_pose(site, cam, T)  # an entity linearised at the camera (pivot = camera)
            drawn = sn_apply(o, q, s, se_inverse_point(cam, target))
            d = unit(sub(drawn, cpos))
            row.append(math.degrees(math.acos(max(-1.0, min(1.0, dot(d, qrot(cq, (0.0, 0.0, 1.0))))))))
        print(f"  {depth:8.0f} | " + " ".join(f"{a:7.3f}" for a in row))

    # 11) SN-side self-check data that must hold inside Subnautica's own Unity (run in game, see gap-1.md)
    check("LookRotation identity", max(abs(c - d) for c, d in zip(look_rotation((0, 0, 1), (0, 1, 0)), (0, 0, 0, 1))) < 1e-15,
          "LookRotation(+Z, +Y) = identity, so local +X = Cross(up, forward) with Unity's Cross formula")

    # 12) static primitives (SN -> SE, CONTRACT 2.7): staticAxes has determinant +1 and inverts the pose rotation;
 # the mutant without the mirror (third axis mapdir^-1(q*z)) has determinant -1
    worst_det = worst_inv = 0.0
    worst_mut = -1e9
    for _ in range(2000):
        c = (rnd.uniform(-7800, 7800), rnd.uniform(-3040, 350), rnd.uniform(-7800, 7800))
        qs = random_quat(rnd)
        P, s, axes, det, _ = static_to_se(site, c, qs, (1.0, 1.0, 1.0))
        worst_det = max(worst_det, abs(det - 1.0))
        _, q_back, _ = wire_pose(site, (axes[0], axes[1], axes[2], P), P)  # SE matrix -> Unity rotation at P
        worst_inv = max(worst_inv, qdist(q_back, qs))
        mut = det_cols((axes[0], axes[1], site.dir_sn_to_se(P, qrot(qs, (0.0, 0.0, 1.0)))))
        worst_mut = max(worst_mut, mut)
    check("static axes", worst_det < 1e-12 and worst_inv < 1e-12 and worst_mut < 0,
          f"det(staticAxes) - 1 worst {worst_det:.1e}; pose(staticAxes) = q worst {worst_inv:.1e}; "
          f"mutant without the mirror: det max {worst_mut:.4f} (< 0)")

    # 13) rotated collider boxes (CONTRACT 2.6): the polar child frame equals the rigid mirrored frame, level boxes give
 # the identity, and the corner error vs the exact affine stays within (s - 1)*|h|
    print("\nrotated collider boxes: corner error vs exact affine (m) and the bound (s - 1)*|h|, 16 m section, h <= 4 m:")
    print(f"  {'depth m':>8} {'tilt':>5} | {'worst':>8} {'bound':>8}")
    worst_rigid = worst_level = 0.0
    for depth in (200.0, 1000.0, 1657.0, 2000.0, 3000.0):
        for tilt in (0.0, 15.0, 45.0):
            worst = worst_ratio = bound_shown = 0.0
            for _ in range(300):
                T = site.sn_to_se((rnd.uniform(-2000, 2000), -depth, rnd.uniform(-2000, 2000)))
                E, U, N = site.frame_at(T)
                az = rnd.uniform(0, 2 * math.pi)
                up = add(mul(U, math.cos(math.radians(tilt))),
                         mul(add(mul(E, math.cos(az)), mul(N, math.sin(az))), math.sin(math.radians(tilt))))
                f = random_unit(rnd)
                f = unit(sub(f, mul(up, dot(f, up))))
                m = (cross(f, up), up, mul(f, -1.0), T)
                o, q, s = wire_pose(site, m, T)
                c_sec = (rnd.uniform(-50, 50), rnd.uniform(-10, 10), rnd.uniform(-50, 50))
                h = (rnd.uniform(0.5, 4), rnd.uniform(0.5, 4), rnd.uniform(0.5, 4))
                b = tuple(c_sec[k] + rnd.uniform(-8 + h[k], 8 - h[k]) for k in range(3))
                axes = random_se_matrix(rnd, (0.0, 0.0, 0.0))[:3]
                centre, h_u, axes_u, rq = box_collider(q, s, c_sec, b, h, axes)
                rigid = quat_from_cols(axes_u[0], axes_u[1], mul(axes_u[2], -1.0))
                worst_rigid = max(worst_rigid, qdist(rq, rigid))
                _, _, _, rq_level = box_collider(q, s, c_sec, b, h, ((1.0, 0, 0), (0, 1.0, 0), (0, 0, 1.0)))
                worst_level = max(worst_level, qdist(rq_level, (0.0, 0.0, 0.0, 1.0)))
                root = sn_apply(o, q, s, c_sec)
                bound = (s - 1.0) * norm(h)
                for sx in (-1, 1):
                    for sy in (-1, 1):
                        for sz in (-1, 1):
                            corner = add(b, add(add(mul(axes[0], sx * h[0]), mul(axes[1], sy * h[1])), mul(axes[2], sz * h[2])))
                            exact = sn_apply(o, q, s, corner)
                            loc = add(centre, qrot(rq, (sx * h_u[0], sy * h_u[1], -sz * h_u[2])))
                            err = norm(sub(add(root, qrot(q, loc)), exact))
                            worst = max(worst, err)
                            worst_ratio = max(worst_ratio, err / bound if bound > 0 else 0.0)
                bound_shown = max(bound_shown, bound)
            print(f"  {depth:8.0f} {tilt:4.0f}d | {worst:8.4f} {bound_shown:8.4f}")
            ok &= worst_ratio <= 1.0 + 1e-9
    check("rotated boxes", worst_rigid < 1e-12 and worst_level < 1e-12,
          f"polar child frame = rigid mirrored frame (worst {worst_rigid:.1e}); grid-aligned boxes: identity (worst {worst_level:.1e}); "
          "corner errors within (s - 1)*|h| (table)")

    # 14) S3 (own RNG: the numbers above are unchanged)
    selftest_s3(site, check)
    print(f"\nSELFTEST ({SITES[site_name]})", "PASS" if ok else "FAIL")
    return ok

# ---------------------------------------------------------------- WM-1 conformance vectors
def r(v):
    return [float(c) for c in v]

POSE_Y_RANGE = (-3040.0, 350.0)  # CONTRACT O1: pose and camera samples cover SE's floor (-3,000 m) to the Approach band

def vector_records(site_name="v1", n=10000):
    """The WM-1 records (list of dicts) for one site; deterministic (fixed seed, stdlib only)."""
    site = site_by_name(site_name)
    rnd = random.Random(4546_0001)
    out = []
    site_rec = {"name": SITES[site_name], "C": r(site.C), "Rs": site.Rs, "U0": r(site.U0), "N0": r(site.N0), "E0": r(site.E0),
                "psi_deg": math.degrees(site.psi), "O": r(site.O)}
    if site_name == "v1":
        site_rec["siteVersion"] = site_v1.SITE_VERSION
    out.append({"kind": "header", "wire_version": WIRE_VERSION, "generator": "tools/worldmap/wire_ref.py",
                "site": site_rec,
                "conventions": {
                    "se_matrix": "rows Right, Up, Backward, Translation (VRage MatrixD M11..M43), Forward = -Backward",
                    "quaternion": "Unity (x, y, z, w), Hamilton, w >= 0",
                    "pose_apply": "sn = o + S*(q*mirror(l)), S = diag(s,1,s), mirror(x,y,z) = (x,y,-z)",
                    "triangle": "unity = (I0, I2, I1) of MyModel.Triangles",
                    "box": "centre = K*mirror(b - c_sec), half_i = h_i*|K*mirror(e_i)|, K = q^-1 S q; child rotation "
                           "(section-local Unity) = polar frame of (K*mirror(e0), K*mirror(e1), -K*mirror(e2))",
                    "static": "SE centre = F^-1(c); axes rows (Right, Up, Backward) = (mapdir^-1(q*x), mapdir^-1(q*y), "
                              "mapdir^-1(q*(-z))) at the SE centre, det +1; half_i = h_i*|S^-1*(q*e_i)|; q normalised first"},
                "ranges": {"pose_camera_sn_y": list(POSE_Y_RANGE)},
                "tolerances": {"pos_double_m": 1e-6, "pos_float32_m": 1e-3, "dir": 1e-9, "quat": 1e-9,
                               "scale": 1e-12, "local_roundtrip_m": 1e-9}})
    pts = list(wm.LANDMARKS_SN.values())
    for _ in range(n):
        pts.append((rnd.uniform(-8192, 8192), rnd.uniform(-3040, 300), rnd.uniform(-8192, 8192)))
    for qsn in pts:
        P = site.sn_to_se(qsn)
        sn = site.se_to_sn(P)
        out.append({"kind": "point", "se": r(P), "sn": r(sn), "sn_f32": r(f32v(sn))})
    for _ in range(n // 5):
        qsn = (rnd.uniform(-7800, 7800), rnd.uniform(-1920, 150), rnd.uniform(-7800, 7800))
        P = site.sn_to_se(qsn)
        w = random_unit(rnd)
        d = site.dir_se_to_sn(P, w)
        out.append({"kind": "dir", "at_se": r(P), "se": r(w), "sn": r(d)})
    poses = []
    for _ in range(n // 5):
        qsn = (rnd.uniform(-7800, 7800), rnd.uniform(*POSE_Y_RANGE), rnd.uniform(-7800, 7800))
        T = site.sn_to_se(qsn)
        m = random_se_matrix(rnd, T)
        L = rnd.choice((2.0, 20.0, 100.0, 300.0))
        bmin = (-L / 4, -L / 8, -L / 2)
        bmax = (L / 4, L / 8, L / 2)
        cam = se_transform(m, (rnd.uniform(-L, L), rnd.uniform(-L / 2, L / 2), rnd.uniform(-1.5 * L, 1.5 * L)))
        P = pivot_nearest(m, bmin, bmax, cam)
        o, q, s = wire_pose(site, m, P)
        poses.append((m, o, q, s))
        out.append({"kind": "pose", "se_matrix": {"right": r(m[0]), "up": r(m[1]), "backward": r(m[2]), "translation": r(m[3])},
                    "box_min": r(bmin), "box_max": r(bmax), "camera_se": r(cam), "pivot_se": r(P),
                    "o": r(o), "q": r(q), "s": s, "o_f32": r(f32v(o)), "q_f32": r(f32v(q)), "s_f32": f32(s)})
        cm = (m[0], m[1], m[2], cam)
        cpos, cq = camera_pose(site, cm)
        out.append({"kind": "camera", "se_matrix": {"right": r(cm[0]), "up": r(cm[1]), "backward": r(cm[2]),
                                                    "translation": r(cm[3])}, "pos": r(cpos), "q": r(cq)})
    for k in range(n // 2):
        m, o, q, s = poses[k % len(poses)]
        l = mul(random_unit(rnd), rnd.uniform(0, 150))
        w = sn_apply(o, q, s, l)
        out.append({"kind": "local", "pose": k % len(poses), "local_se": r(l), "sn": r(w),
                    "exact_sn": r(site.se_to_sn(se_transform(m, l)))})

    def box_record(k, axes, rotated):
        m, o, q, s = poses[k % len(poses)]
        c_sec = (rnd.uniform(-50, 50), rnd.uniform(-10, 10), rnd.uniform(-50, 50))
        h = (rnd.uniform(0.5, 4), rnd.uniform(0.5, 4), rnd.uniform(0.5, 4))
        b = tuple(c_sec[i] + rnd.uniform(-8 + h[i], 8 - h[i]) for i in range(3))
        if axes is None:
            axes = random_se_matrix(rnd, (0.0, 0.0, 0.0))[:3]
        centre, h_u, axes_u, rq = box_collider(q, s, c_sec, b, h, axes)
        return {"kind": "box", "pose": k % len(poses), "rotated": rotated, "section_centre_se": r(c_sec),
                "section_root_sn": r(sn_apply(o, q, s, c_sec)),
                "box_centre_se": r(b), "half_extents_se": r(h), "axes_se": [r(a) for a in axes],
                "centre_section_local_unity": r(centre), "half_extents_unity": r(h_u), "axes_unity": [r(a) for a in axes_u],
                "rotation_section_local_unity": r(rq)}
    for k in range(n // 10):  # grid-aligned boxes (on the section root)
        out.append(box_record(k, ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)), False))
    for k in range(n // 10):  # rotated boxes (on a unit-scale child), CONTRACT O1
        out.append(box_record(k, None, True))
    verts, tris, normals = cube_mesh()
    out.append({"kind": "mesh", "verts_se": [r(v) for v in verts], "tris_se": [list(t) for t in tris],
                "verts_unity": [r(mirror(v)) for v in verts], "tris_unity": [list(tri_to_unity(t)) for t in tris],
                "normals_se": [r(nv) for nv in normals]})
    for k in range(n // 10):
        m, o, q, s = poses[k % len(poses)]
        P = m[3]
        w = mul(random_unit(rnd), rnd.uniform(0, 2))
        v = mul(random_unit(rnd), rnd.uniform(0, 40))
        out.append({"kind": "motion", "at_se": r(P), "omega_se": r(w), "omega_sn": r(angvel_to_sn(site, P, w)),
                    "v_se": r(v), "v_sn": r(vel_to_sn(site, P, v))})
    for _ in range(n // 10):  # Subnautica static box primitives -> SE (CONTRACT 2.7, O1); inputs are float32 wire values
        c = f32v((rnd.uniform(-7800, 7800), rnd.uniform(*POSE_Y_RANGE), rnd.uniform(-7800, 7800)))
        qs = f32v(random_quat(rnd))
        h = f32v((rnd.uniform(0.1, 30), rnd.uniform(0.1, 30), rnd.uniform(0.1, 30)))
        P, s, axes, det, he = static_to_se(site, c, qs, h)
        out.append({"kind": "static", "primitive": "box", "centre_sn": r(c), "q_sn": r(qs), "half_extents_sn": r(h),
                    "centre_se": r(P), "s": s,
                    "axes_se": {"right": r(axes[0]), "up": r(axes[1]), "backward": r(axes[2])}, "det": det,
                    "half_extents_se": r(he)})
    return out

def vectors_text(site_name="v1", n=10000):
    """The exact file text (one compact JSON object per line, LF)."""
    return "".join(json.dumps(rec, separators=(",", ":")) + "\n" for rec in vector_records(site_name, n))

def vectors(path, n=10000, site_name="v1"):
    text = vectors_text(site_name, n)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print(f"site {SITES[site_name]}: wrote {text.count(chr(10))} records, {len(text)/1e6:.2f} MB, "
          f"sha256 {hashlib.sha256(text.encode()).hexdigest()} -> {path}")

# ================================================================ S5 (CONTRACT 5.4 "C1-C4 conventions for S5")
# An independent reading of CONTRACT 5.4 (never generated from the C# sources): the conventions text and hash, the version
# nibble, the key texts, the canonical skeleton (read from its data file, the single source of the bone table), the
# skinned-mesh rules (bone map by name, weight remap, SkinnedBind = inverse absolute bind, split by material), the SE
# row-matrix algebra (product, inverse, the subgrid relative matrix), the Unity mirror conjugation, the 2.8
# interpolation, chunk cells and colour keys, the character records and the.derec format. The C# twins are
# MirrorConventions, MirrorSkeleton, SkinMath, InstanceMath, ChunkMath and StreamRecording in DeepEngineers.Wire, compared
# record by record by tests/DeepEngineers.Wire.Tests/S5VectorTests.cs ("s5vectors -"). Layout 1.0 is unchanged.
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
S5_SKELETON_TSV = os.path.join(REPO_ROOT, "src", "DeepEngineers.Wire", "Data", "SeSkeleton.canon1.tsv")
S5_CONV_HASH = 0x31DFD6F9BEFDEF5D
S5_SKELETON_HASH = 0x995765282E7D4F1D
LAYOUT_HASH_1_0 = 0x4F3A35F6FC1EAF31
FNV32_OFFSET, FNV32_PRIME = 0x811C9DC5, 0x01000193
S5_V1 = 1 << 28
S5_TSV_HEADER = ["index", "seBone", "parent", "seParent", "human", "role", "snBone"]
S5_CONSTS = (
    ("MirrorCharacterFlags", "Player", 1), ("MirrorCharacterFlags", "Fallback", 2), ("MirrorCharacterFlags", "Version1", S5_V1),
    ("MirrorCharacterPart", "Body", 1), ("MirrorCharacterPart", "Head1st", 2), ("MirrorCharacterPart", "Tool", 3),
    ("MirrorGridFlags", "Static", 1), ("MirrorGridFlags", "Subgrid", 2), ("MirrorGridFlags", "FloatingObject", 4),
    ("MirrorGridFlags", "Version1", S5_V1),
    ("MirrorLimits", "MaxSkinWeights", 4), ("MirrorLimits", "CanonicalBones", 78),
    ("MirrorPartClass", "CubePart", 1), ("MirrorPartClass", "Edge", 2), ("MirrorPartClass", "FatBlock", 3),
    ("MirrorPartClass", "Subpart", 4), ("MirrorPartClass", "CharacterBody", 5), ("MirrorPartClass", "Tool", 6),
    ("MirrorPartClass", "FloatingObject", 7),
    ("MirrorPartFlags", "ClassMask", 15), ("MirrorPartFlags", "Dynamic", 16), ("MirrorPartFlags", "HiddenInFirstPerson", 32),
    ("MirrorPartFlags", "BuildStage", 64), ("MirrorPartFlags", "Deformed", 128), ("MirrorPartFlags", "NoShadows", 256),
    ("MirrorPartFlags", "MetalnessColorable", 512), ("MirrorPartFlags", "Version1", S5_V1),
    ("MirrorPartFlags", "VersionMask", 0xF0000000),
)
S5_FORMATS = (
    "modelKey <path>|lod<n>[|scale=<s>][|skin=<skin>][|char=body|char=head1st]",
    "partKey.cube fnv1a64(u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)",
    "partKey.edge fnv1a64(u32 2, u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)",
    "partKey.fat fnv1a64(utf8 fat|<minX>,<minY>,<minZ>)",
    "partKey.subpart fnv1a64(utf8 sub|<fatKey hex16>|<name>[/<name>])",
    "partKey.floating 1",
    "skinId fnv1a32(utf8 lower(<skin>)) or 0",
)
CUBE, EDGE, FAT, SUBPART, BODY, TOOL, FLOATING = 1, 2, 3, 4, 5, 6, 7
PF_DYNAMIC, PF_HIDDEN1ST, PF_STAGE, PF_DEFORMED, PF_NOSHADOWS, PF_METALCOL = 16, 32, 64, 128, 256, 512
DRAW_NONE, DRAW_CHUNK, DRAW_OWN, DRAW_FLOATING, DRAW_SKINNED = 0, 1, 2, 3, 4
S5_REQUIRED_HUMAN = ("Hips", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg", "LeftFoot", "RightFoot",
                     "Spine", "Head", "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm", "LeftHand", "RightHand")
SE_IDENTITY = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, 0.0))

def fnv1a32(data, h=FNV32_OFFSET):
    for b in data:
        h = ((h ^ b) * FNV32_PRIME) & 0xFFFFFFFF
    return h

def s5_skeleton_rows(path=S5_SKELETON_TSV):
    """The canonical skeleton rows (7 string columns each) of the data file; '#' lines are comments."""
    rows, header = [], None
    with open(path, encoding="utf-8") as fh:
        for line in fh.read().split("\n"):
            line = line.rstrip("\r")
            if not line or line.startswith("#"):
                continue
            cols = line.split("\t")
            if header is None:
                header = cols
                if header != S5_TSV_HEADER:
                    raise ValueError(f"skeleton header {header}")
                continue
            if len(cols) != 7:
                raise ValueError(f"skeleton row {line!r}")
            rows.append(cols)
    return rows

def s5_skeleton_text(rows):
    return "".join(f"bone {r[0]} {r[1]} {r[2]}\n" for r in rows)

def s5_skeleton_hash(rows=None):
    return fnv1a64(s5_skeleton_text(rows if rows is not None else s5_skeleton_rows()).encode("ascii"))

def s5_conv_text(skeleton_hash=None):
    """CONTRACT 5.4 canonical text: const lines sorted by group, value, name; the format lines; the skeleton line."""
    sh = s5_skeleton_hash() if skeleton_hash is None else skeleton_hash
    lines = ["ext s5 mirror 1"]
    lines += [f"const {g} {n} {v}" for (g, n, v) in sorted(S5_CONSTS, key=lambda e: (e[0], e[2], e[1]))]
    lines += ["format " + f for f in S5_FORMATS]
    lines.append("skeleton canon1 78 0x%016x" % sh)
    return "".join(line + "\n" for line in lines)

def s5_version(flags):
    """0 legacy (S3 producer), 1 version 1, 2 rejected (ignored and counted)."""
    v = flags >> 28
    return 0 if v == 0 else (1 if v == 1 else 2)

def s5_effective_class(flags):
    v = s5_version(flags)
    if v == 0:
        return CUBE
    if v == 1:
        c = flags & 15
        return CUBE if c == 0 else c
    return 0

def s5_draw_kind(flags):
    v = s5_version(flags)
    if v == 0:
        return DRAW_CHUNK
    if v != 1:
        return DRAW_NONE
    c = s5_effective_class(flags)
    if c in (CUBE, EDGE, FAT, SUBPART):
        return DRAW_OWN if flags & PF_DYNAMIC else DRAW_CHUNK
    return {TOOL: DRAW_OWN, FLOATING: DRAW_FLOATING, BODY: DRAW_SKINNED}.get(c, DRAW_NONE)

def s5_chunk_flags(flags):
    return flags & (PF_NOSHADOWS | PF_METALCOL) if s5_version(flags) == 1 else 0

def s5_part_flags(cls, extra=0):
    assert 1 <= cls <= 7 and not extra & (15 | 0xF0000000)
    return S5_V1 | cls | extra

def s5_skin_id(skin):
    return 0 if not skin else fnv1a32(skin.lower().encode("utf-8"))

def s5_model_key_text(path, lod=0, scale=1.0, skin=None, char=0):
    text = normalize_model_path(path) + "|lod" + str(int(lod))
    if f32(scale) != 1.0:
        text += "|scale=" + net_float_r(scale)
    if skin:
        text += "|skin=" + skin.lower()
    if char:
        text += {1: "|char=body", 2: "|char=head1st"}[char]
    return text

def s5_model_key(path, lod=0, scale=1.0, skin=None, char=0):
    return fnv1a64(s5_model_key_text(path, lod, scale, skin, char).encode("utf-8"))

def s5_cube_key(mk, tx, ty, tz, rot):
    return fnv1a64(struct.pack("<Q4f", mk, tx, ty, tz, rot))

def s5_edge_key(mk, tx, ty, tz, rot):
    return fnv1a64(struct.pack("<IQ4f", EDGE, mk, tx, ty, tz, rot))

def s5_fat_key_text(x, y, z):
    return "fat|%d,%d,%d" % (x, y, z)

def s5_subpart_key_text(fat_key, names):
    return "sub|%016x|%s" % (fat_key, "/".join(names))

def s5_text_key(text):
    return fnv1a64(text.encode("utf-8"))

# ---- SE row matrices (rows R, U, B, T; p' = p.x R + p.y U + p.z B + T)
def se_dir(m, d):
    return add(add(mul(m[0], d[0]), mul(m[1], d[1])), mul(m[2], d[2]))

def se_mul(a, b):
    """VRage a * b: apply a, then b (child local x parent)."""
    return (se_dir(b, a[0]), se_dir(b, a[1]), se_dir(b, a[2]), se_transform(b, a[3]))

def se_inverse(m):
    """General affine inverse: the columns of A^-1 are (u x b, b x r, r x u) / det."""
    r, u, b, t = m
    det = dot(r, cross(u, b))
    c0, c1, c2 = cross(u, b), cross(b, r), cross(r, u)
    ir = (c0[0] / det, c1[0] / det, c2[0] / det)
    iu = (c0[1] / det, c1[1] / det, c2[1] / det)
    ib = (c0[2] / det, c1[2] / det, c2[2] / det)
    it = add(add(mul(ir, t[0]), mul(iu, t[1])), mul(ib, t[2]))
    return (ir, iu, ib, (-it[0], -it[1], -it[2]))

def s5_relative(child, parent):
    """child x parent^-1 for an orthonormal parent, translation difference first (the C2 subgrid row, doubles)."""
    def proj(v):
        return (dot(v, parent[0]), dot(v, parent[1]), dot(v, parent[2]))
    return (proj(child[0]), proj(child[1]), proj(child[2]), proj(sub(child[3], parent[3])))

def s5_rows(m):
    return [c for row in m for c in row]

def s5_matrix(flat, off=0):
    v = flat[off:off + 12]
    return (tuple(v[0:3]), tuple(v[3:6]), tuple(v[6:9]), tuple(v[9:12]))

def _vdiv(v, s):
    return (v[0] / s, v[1] / s, v[2] / s)

def _neg(v):
    return (-v[0], -v[1], -v[2])

def s5_to_unity_affine(m):
    """The mirror conjugation D A D: columns (mirror R, mirror U, -mirror B), translation mirror T."""
    return (mirror(m[0]), mirror(m[1]), _neg(mirror(m[2]))), mirror(m[3])

def s5_to_unity(m):
    """(position, rotation (x, y, z, w), scale) of a mirrored SE-local matrix with orthogonal rows."""
    sx, sy, sz = norm(m[0]), norm(m[1]), norm(m[2])
    x, y, z = mirror(_vdiv(m[0], sx)), mirror(_vdiv(m[1], sy)), mirror(_vdiv(m[2], sz))
    return mirror(m[3]), quat_from_cols(x, y, _neg(z)), (sx, sy, sz)

def s5_rotation(m):
    return quat_from_cols(_vdiv(m[0], norm(m[0])), _vdiv(m[1], norm(m[1])), _vdiv(m[2], norm(m[2])))

def s5_slerp(a, b, t):
    d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]
    if d < 0:
        b = tuple(-c for c in b)
        d = -d
    if d > 0.9995:
        wa, wb = 1.0 - t, t
    else:
        th = math.acos(d)
        sn = math.sin(th)
        wa, wb = math.sin((1.0 - t) * th) / sn, math.sin(t * th) / sn
    q = tuple(a[i] * wa + b[i] * wb for i in range(4))
    n = math.sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3])
    return tuple(c / n for c in q)

def s5_interpolate(a, b, alpha):
    """CONTRACT 2.8 for a C2 row or a bone: T lerp, rotation slerp and row-scale lerp in SE space; alpha 1 = b exactly."""
    if alpha >= 1.0:
        return b
    if not alpha > 0.0:
        return a
    t = add(a[3], mul(sub(b[3], a[3]), alpha))
    sa, sb = (norm(a[0]), norm(a[1]), norm(a[2])), (norm(b[0]), norm(b[1]), norm(b[2]))
    s = add(sa, mul(sub(sb, sa), alpha))
    q = s5_slerp(s5_rotation(a), s5_rotation(b), alpha)
    return (mul(qrot(q, (1.0, 0.0, 0.0)), s[0]), mul(qrot(q, (0.0, 1.0, 0.0)), s[1]), mul(qrot(q, (0.0, 0.0, 1.0)), s[2]), t)

# ---- skinned meshes
def s5_bone_map(names, parents, rows=None):
    """(canon_to_model, model_to_canonical_ancestor) or raises ValueError (the fallback reasons)."""
    rows = rows if rows is not None else s5_skeleton_rows()
    if len(set(names)) != len(names):
        raise ValueError("bone name repeated")
    index = {n: i for i, n in enumerate(names)}
    canon_to_model = []
    for r in rows:
        if r[1] not in index:
            raise ValueError("canonical bone missing: " + r[1])
        mi = index[r[1]]
        want = None if r[3] == "-" else r[3]
        got = None if parents[mi] < 0 else names[parents[mi]]
        if want != got:
            raise ValueError("canonical bone %s has parent %s, expected %s" % (r[1], got, want))
        canon_to_model.append(mi)
    model_to_canon = {mi: c for c, mi in enumerate(canon_to_model)}
    anc = []
    for i in range(len(names)):
        if i in model_to_canon:
            anc.append(model_to_canon[i])
            continue
        p, target = parents[i], 0
        while p >= 0:
            if p in model_to_canon:
                target = model_to_canon[p]
                break
            p = parents[p]
        anc.append(target)
    return canon_to_model, anc

def s5_remap_weights(anc, idx, w):
    """Weights to canonical indices: drop w <= 0, merge in slot order (doubles), keep the 4 largest (ties: lower index),
 renormalise (float32 of w / total); none -> (0, 1)."""
    acc, order = {}, []
    for b, x in zip(idx, w):
        x = f32(x)
        if not x > 0.0:
            continue
        c = anc[b]
        if c not in acc:
            acc[c] = 0.0
            order.append(c)
        acc[c] += x
    items = sorted(((c, acc[c]) for c in order), key=lambda e: (-e[1], e[0]))[:4]
    if not items:
        return [0, 0, 0, 0], [1.0, 0.0, 0.0, 0.0]
    total = items[0][1]
    for _, x in items[1:]:
        total += x
    out_i = [c for c, _ in items] + [0] * (4 - len(items))
    out_w = [f32(x / total) for _, x in items] + [0.0] * (4 - len(items))
    return out_i, out_w

def s5_abs_from_local(local, parents):
    absm = [None] * len(local)

    def resolve(i, depth=0):
        if absm[i] is None:
            if depth > len(local):
                raise ValueError("cyclic bone parents")
            p = parents[i]
            absm[i] = local[i] if p < 0 else se_mul(local[i], resolve(p, depth + 1))
        return absm[i]
    for i in range(len(local)):
        resolve(i)
    return absm

def s5_bind_rows(abs_model, canon_to_model):
    """SkinnedBind rows (78 x 12 float32): inverse of each canonical bone's absolute bind (= SE's SkinTransform)."""
    out = []
    for mi in canon_to_model:
        out += f32v(s5_rows(se_inverse(abs_model[mi])))
    return out

def s5_bone_locals(abs_rows, rows=None):
    """local_i = abs_i x abs_parent^-1 (general inverse); the root keeps its absolute matrix."""
    rows = rows if rows is not None else s5_skeleton_rows()
    absm = [s5_matrix(abs_rows, 12 * i) for i in range(len(rows))]
    return [absm[i] if int(r[2]) < 0 else se_mul(absm[i], se_inverse(absm[int(r[2])])) for i, r in enumerate(rows)]

def s5_skin_point(v, idx, w, bind, absm):
    """SE skinning reference: sum w_k (v x bind[b_k] x abs[b_k])."""
    acc = (0.0, 0.0, 0.0)
    for b, x in zip(idx, w):
        if x == 0.0:
            continue
        acc = add(acc, mul(se_transform(absm[b], se_transform(bind[b], v)), x))
    return acc

def s5_split(m, head_flags):
    """(body, head) model dicts (or None) of a character model split by submesh (CONTRACT 5.4 "Character")."""
    tris = len(m["indices"]) // 3
    owner = []
    for t in range(tris):
        o = -1
        for s, (_, start, count, _) in enumerate(m["submeshes"]):
            if start <= t < start + count:
                o = s
                break
        owner.append(o)

    def part(want_head):
        tri_list, subs = [], []
        for s, (key, _, _, tech) in enumerate(m["submeshes"]):
            if head_flags[s] != want_head:
                continue
            start = len(tri_list)
            tri_list += [t for t in range(tris) if owner[t] == s]
            if len(tri_list) > start:
                subs.append((key, start, len(tri_list) - start, tech))
        if not want_head:
            tri_list += [t for t in range(tris) if owner[t] < 0]
        if not tri_list:
            return None
        remap, order, indices = {}, [], []
        for t in tri_list:
            for e in range(3):
                v = m["indices"][3 * t + e]
                if v not in remap:
                    remap[v] = len(order)
                    order.append(v)
                indices.append(remap[v])
        out = {k: [m[k][v] for v in order] for k in ("positions", "normals", "tangents", "binormals", "uvs", "bone_indices", "bone_weights")}
        out.update(indices=indices, submeshes=subs, flags=m["flags"], key=m["key"], lod=m.get("lod", 0),
                   pattern_scale=m.get("pattern_scale", 1.0))
        out["bounds"] = tuple(min(p[i] for p in out["positions"]) for i in range(3)) + tuple(max(p[i] for p in out["positions"]) for i in range(3))
        return out
    return part(False), part(True)

# ---- chunks (Subnautica side)
def s5_cell(tx, ty, tz, target_class):
    c = 4.0 if target_class == 2 else 20.0
    return tuple(int(math.floor(f32(f32(t) / c))) for t in (tx, ty, tz))

def s5_colour_key(h, s, v):
    def comp(x):
        x = f32(x)
        if math.isnan(x):
            x = 0.0
        x = -2.0 if x < -2.0 else (2.0 if x > 2.0 else x)
        return int(round(x * 10000.0)) + 20000
    return (comp(h) << 32) | (comp(s) << 16) | comp(v)

# ---- character records
def s5_character_add_payload(char_id, model_key, fallback):
    return struct.pack("<QQII", char_id, model_key, S5_V1 | 1 | (2 if fallback else 0), 78)

def s5_character_part_payload(char_id, part, model_key, hsv, skin_id):
    flags = s5_part_flags(BODY, PF_METALCOL | (PF_HIDDEN1ST if part == 2 else 0))
    return part_add_payload(char_id, part, model_key, f32v(s5_rows(SE_IDENTITY)), f32v(hsv), (0.0, 0.0), skin_id, flags)

# ----.derec
DEREC_KINDS = {"c3": 1, "c4": 2, "c2": 3, "c1": 4, "marker": 5, "end": 0xFFFF}

def derec_header(se_version, created_ms, flags, world_epoch, layout_hash=LAYOUT_HASH_1_0, conv_hash=S5_CONV_HASH, wire_version=WIRE_VERSION):
    return struct.pack("<8sIIQQIIQIIQ", b"DEREC001", 1, 64, layout_hash, conv_hash, wire_version, se_version, created_ms, flags, world_epoch, 0)

def derec_record(kind, se_tick, qpc, data=b""):
    return struct.pack("<HHIQq", kind, 0, len(data), se_tick, qpc) + bytes(data) + b"\0" * ((-len(data)) % 8)

def derec_read(blob):
    """(header fields, [(kind, seTick, qpc, bytes)], saw_end, truncated) of a.derec blob."""
    if len(blob) < 64 or blob[:8] != b"DEREC001":
        raise ValueError("not a .derec")
    h = struct.unpack_from("<8sIIQQIIQIIQ", blob, 0)
    if h[1] != 1:
        raise ValueError("formatVersion %d" % h[1])
    off, recs = h[2], []
    while off + 24 <= len(blob):
        kind, pad, n, tick, qpc = struct.unpack_from("<HHIQq", blob, off)
        end = off + 24 + n + ((-n) % 8)
        if pad != 0 or end > len(blob):
            return h, recs, False, True
        if kind == 0xFFFF:
            return h, recs, True, False
        recs.append((kind, tick, qpc, blob[off + 24:off + 24 + n]))
        off = end
    return h, recs, False, off != len(blob)

# ---- the C# skeleton table (D15 style: C# arrays, not an embedded resource)
def s5_skeleton_cs(rows=None):
    rows = rows if rows is not None else s5_skeleton_rows()

    def strs(col):
        return ",\n            ".join(", ".join('"%s"' % r[col] for r in rows[i:i + 6]) for i in range(0, len(rows), 6))

    def ints(col):
        return ",\n            ".join(", ".join(r[col] for r in rows[i:i + 20]) for i in range(0, len(rows), 20))
    return ("// <auto-generated> python -I tools/worldmap/wire_ref.py s5skeleton-cs src/DeepEngineers.Wire/MirrorSkeleton.Canon1.cs\n"
            "// from src/DeepEngineers.Wire/Data/SeSkeleton.canon1.tsv (skeleton hash 0x%016x). Do not edit by hand: edit the data\n"
            "// file and regenerate; MirrorConventionsTests compares every column with the data file. </auto-generated>\n"
            "namespace DeepEngineers.Wire\n{\n"
            "    public static partial class MirrorSkeleton\n    {\n"
            "        private static readonly string[] Names =\n        {\n            %s,\n        };\n\n"
            "        private static readonly int[] Parents =\n        {\n            %s,\n        };\n\n"
            "        private static readonly string[] Humans =\n        {\n            %s,\n        };\n\n"
            "        private static readonly string[] Roles =\n        {\n            %s,\n        };\n\n"
            "        private static readonly string[] SnBones =\n        {\n            %s,\n        };\n"
            "    }\n}\n") % (s5_skeleton_hash(rows), strs(1), ints(2), strs(4), strs(5), strs(6))

# ---- vectors for S5VectorTests (one record per line: kind name=value...; d<16 hex> = IEEE double bits, s<hex> = UTF-8,
# x<hex> = bytes, L<a,b,...> = list of decimal ints or d-doubles, plain decimal = integer)
def _s5_enc(v):
    if isinstance(v, bool):
        return str(int(v))
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        return "d" + struct.pack(">d", v).hex()
    if isinstance(v, str):
        return "s" + v.encode("utf-8").hex()
    if isinstance(v, (bytes, bytearray)):
        return "x" + bytes(v).hex()
    flat = []

    def walk(x):
        if isinstance(x, (list, tuple)):
            for y in x:
                walk(y)
        else:
            flat.append(x)
    walk(v)
    return "L" + ",".join(str(x) if isinstance(x, int) and not isinstance(x, bool) else "d" + struct.pack(">d", float(x)).hex() for x in flat)

def _s5_line(rec_kind, /, **fields):
    return rec_kind + "".join(" %s=%s" % (k, _s5_enc(v)) for k, v in fields.items())

def _s5_rigid(rnd, tscale):
    m = random_se_matrix(rnd, (rnd.uniform(-tscale, tscale), rnd.uniform(-tscale, tscale), rnd.uniform(-tscale, tscale)))
    return m

def _s5_scaled(rnd, tscale, smin=0.5, smax=2.0):
    m = _s5_rigid(rnd, tscale)
    k = (rnd.uniform(smin, smax), rnd.uniform(smin, smax), rnd.uniform(smin, smax))
    return (mul(m[0], k[0]), mul(m[1], k[1]), mul(m[2], k[2]), m[3])

def s5_synthetic_skeleton(rnd, rows):
    """A model skeleton: the 78 canonical bones shuffled, plus non-canonical bones (a chain under the left forearm, one
 under the root), each with a random rigid local bind and a random posed absolute matrix."""
    names = [r[1] for r in rows]
    parent_names = [None if r[3] == "-" else r[3] for r in rows]
    names += ["DE_Extra_A", "DE_Extra_B", "DE_MeshNode"]
    parent_names += ["SE_RigLForearm1", "DE_Extra_A", "RootNode"]
    order = list(range(len(names)))
    rnd.shuffle(order)
    m_names = [names[i] for i in order]
    pos = {n: i for i, n in enumerate(m_names)}
    m_parents = [-1 if parent_names[i] is None else pos[parent_names[i]] for i in order]
    local = [_s5_rigid(rnd, 0.3) for _ in m_names]
    posed = [_s5_rigid(rnd, 1.5) for _ in m_names]
    return m_names, m_parents, local, posed

def s5_vector_records():
    rnd = random.Random(50050017)
    rows = s5_skeleton_rows()
    out = []
    text = s5_conv_text()
    out.append(_s5_line("conv", text=text, hash=fnv1a64(text.encode("ascii")), lines=text.count("\n"), bytes=len(text)))
    out.append(_s5_line("skeleton", text=s5_skeleton_text(rows), hash=s5_skeleton_hash(rows), rows=len(rows)))
    # keys
    for (p, lod, sc, skin, ch) in (("Models\\Cubes\\Large\\ArmorCube.mwm", 0, 1.0, None, 0),
                                   ("models/cubes/large/armorcube.mwm", 0, 1.0, "Clean_Armor", 0),
                                   ("Models\\Characters\\Astronaut\\SE_astronaut.mwm", 0, 1.0, None, 1),
                                   ("Models\\Characters\\Astronaut\\SE_astronaut.mwm", 0, 1.0, None, 2),
                                   ("Models\\Characters\\Astronaut\\SE_astronaut.mwm", 0, 1.0, "Neon_Colorable_Suit", 1),
                                   ("  Models\\Characters\\Female\\SE_astronaut.MWM ", 0, 1.0, "Astronaut_Rust", 2),
                                   ("Models\\Cubes\\Small\\ArmorCube.mwm", 0, 0.2, "Golden_Armor", 0),
                                   ("Models\\Cubes\\Large\\RotorPart.mwm", 1, 1.0, "", 0)):
        out.append(_s5_line("model_key", path=p, lod=lod, scale=f32(sc), skin=skin or "", char=ch,
                            text=s5_model_key_text(p, lod, sc, skin, ch), key=s5_model_key(p, lod, sc, skin, ch)))
    mk0 = s5_model_key("models\\cubes\\large\\armorcube.mwm", 0)
    cases = [(mk0, 2.5, 0.0, -5.0, 0.0)]
    for _ in range(12):
        cases.append((rnd.getrandbits(64), f32(rnd.uniform(-200, 200)), f32(rnd.uniform(-200, 200)), f32(rnd.uniform(-200, 200)),
                      f32(rnd.choice((0.0, 1.0, 17.0, 23.0, rnd.uniform(0, 64))))))
    cases.append((rnd.getrandbits(64), -0.0, 0.0, -0.0, 0.0))
    for (mk, tx, ty, tz, rot) in cases:
        out.append(_s5_line("part_keys", mk=mk, t=[tx, ty, tz], rot=rot, cube=s5_cube_key(mk, tx, ty, tz, rot),
                            edge=s5_edge_key(mk, tx, ty, tz, rot)))
    for (x, y, z) in ((-3, 0, 12), (0, 0, 0), (-300, 299, -1), (2147483647, -2147483648, 5)):
        fk = s5_text_key(s5_fat_key_text(x, y, z))
        out.append(_s5_line("fat_key", x=x, y=y, z=z, text=s5_fat_key_text(x, y, z), key=fk))
        for names in (["DoorLeft"], ["TurretBase", "TurretBarrel"], ["a", "b", "c"], ["Été"]):
            out.append(_s5_line("subpart_key", fat=fk, path="/".join(names), text=s5_subpart_key_text(fk, names),
                                key=s5_text_key(s5_subpart_key_text(fk, names))))
    for skin in ("clean_armor", "Clean_Armor", "", "Neon_Colorable_Surfaces", "DLC_Suit_Été", "x"):
        out.append(_s5_line("skin_id", skin=skin, id=s5_skin_id(skin)))
    # flags and drawing
    for (cls, extra) in ((CUBE, 0), (EDGE, PF_NOSHADOWS), (FAT, PF_STAGE | PF_METALCOL), (SUBPART, PF_DYNAMIC),
                         (BODY, PF_METALCOL | PF_HIDDEN1ST), (TOOL, PF_DYNAMIC), (FLOATING, 0), (SUBPART, PF_DEFORMED | PF_NOSHADOWS)):
        out.append(_s5_line("part_flags", cls=cls, extra=extra, flags=s5_part_flags(cls, extra)))
    for fl in (0, 5, S5_V1, S5_V1 | 3, S5_V1 | 4 | PF_DYNAMIC, S5_V1 | 6, S5_V1 | 7, S5_V1 | 5, S5_V1 | 9, 2 << 28, 0xF0000001,
               S5_V1 | 2 | PF_NOSHADOWS | PF_METALCOL | PF_STAGE, 7):
        out.append(_s5_line("version", flags=fl, version=s5_version(fl), cls=s5_effective_class(fl), draw=s5_draw_kind(fl),
                            chunk_flags=s5_chunk_flags(fl)))
    # SE algebra
    for _ in range(12):
        parent = random_se_matrix(rnd, (rnd.uniform(-2e6, 2e6), rnd.uniform(-2e6, 2e6), rnd.uniform(-2e6, 2e6)))
        child = se_mul(_s5_rigid(rnd, 30.0), parent)
        out.append(_s5_line("relative", child=s5_rows(child), parent=s5_rows(parent), rel=s5_rows(s5_relative(child, parent))))
    for _ in range(12):
        a, b = _s5_scaled(rnd, 50.0), _s5_scaled(rnd, 50.0)
        out.append(_s5_line("algebra", a=s5_rows(a), b=s5_rows(b), ab=s5_rows(se_mul(a, b)), inv=s5_rows(se_inverse(a))))
    for i in range(16):
        m = _s5_rigid(rnd, 40.0)
        k = 1.0 if i < 8 else rnd.uniform(0.25, 3.0)
        m = (mul(m[0], k), mul(m[1], k), mul(m[2], k), m[3])
        pos, rot, scale = s5_to_unity(m)
        cols, t = s5_to_unity_affine(m)
        out.append(_s5_line("unity", m=s5_rows(m), pos=pos, rot=rot, scale=scale, affine=[cols[0], cols[1], cols[2], t]))
    for i in range(18):
        a = _s5_rigid(rnd, 10.0)
        if i % 3 == 0:
            b = se_mul(a, (qrot_rows(rnd, 0.3)))
        else:
            b = _s5_rigid(rnd, 10.0)
        alpha = (0.0, 1.0, 0.5, 0.25, 0.75, 1e-3)[i % 6] if i < 12 else rnd.uniform(0, 1)
        out.append(_s5_line("interp", a=s5_rows(a), b=s5_rows(b), alpha=alpha, r=s5_rows(s5_interpolate(a, b, alpha))))
    # skeleton, binds, C1 bones, weights, skinning
    names, parents, local, posed = s5_synthetic_skeleton(rnd, rows)
    c2m, anc = s5_bone_map(names, parents, rows)
    abs_bind = s5_abs_from_local(local, parents)
    bind_rows = s5_bind_rows(abs_bind, c2m)
    c1_rows = []
    for mi in c2m:
        c1_rows += f32v(s5_rows(posed[mi]))
    locals_ = s5_bone_locals(c1_rows, rows)
    out.append(_s5_line("skeleton_case", names="|".join(names), parents=parents, local=[s5_rows(m) for m in local],
                        posed=[s5_rows(m) for m in posed], c2m=c2m, anc=anc, bind=bind_rows, c1=c1_rows,
                        locals=[s5_rows(m) for m in locals_]))
    bind_m = [s5_matrix(bind_rows, 12 * c) for c in range(78)]
    pose_m = [s5_matrix(c1_rows, 12 * c) for c in range(78)]
    n_model = len(names)
    for i in range(40):
        idx = [rnd.randrange(n_model) for _ in range(4)]
        w = [f32(rnd.choice((0.0, rnd.uniform(0, 1), rnd.uniform(0, 1)))) for _ in range(4)]
        if i == 0:
            w = [0.0, 0.0, 0.0, 0.0]
        if i == 1:
            idx = [names.index("DE_Extra_B"), names.index("DE_Extra_A"), names.index("SE_RigLForearm1"), names.index("DE_MeshNode")]
            w = [f32(0.25), f32(0.25), f32(0.25), f32(0.25)]
        if i == 2:
            w = [f32(-0.5), f32(float("nan")), f32(0.75), f32(0.25)]
        ci, cw = s5_remap_weights(anc, idx, w)
        v = (rnd.uniform(-1, 1), rnd.uniform(0, 2), rnd.uniform(-0.5, 0.5))
        out.append(_s5_line("weights", idx=idx, w=w, cidx=ci, cw=cw, v=v, skinned=s5_skin_point(v, ci, cw, bind_m, pose_m)))
    bad_names = list(names)
    bad_names[names.index("SE_RigHead")] = "SE_RigHeadX"
    out.append(_s5_line("skeleton_bad", names="|".join(bad_names), parents=parents, reason="missing"))
    bad_parents = list(parents)
    bad_parents[names.index("SE_RigRPalm")] = names.index("SE_RigLPalm")
    out.append(_s5_line("skeleton_bad", names="|".join(names), parents=bad_parents, reason="parent"))
    # split by material
    model = s5_synthetic_character_model(rnd)
    for head in ((False, True, False, False), (False, False, False, False), (True, True, True, True)):
        body, headm = s5_split(model, head)
        out.append(_s5_line("split", head=[int(h) for h in head], src_key=model["key"], positions=model["positions"],
                            normals=model["normals"], tangents=model["tangents"], binormals=model["binormals"], uvs=model["uvs"],
                            bones=model["bone_indices"], weights=model["bone_weights"], indices=model["indices"],
                            submeshes=[[k, s, c, t] for (k, s, c, t) in model["submeshes"]],
                            body=model_payload(body) if body else b"", body_bounds=list(body["bounds"]) if body else [],
                            head_payload=model_payload(headm) if headm else b"", head_bounds=list(headm["bounds"]) if headm else []))
    # chunks
    for cls in (1, 2):
        c = 20.0 if cls == 1 else 4.0
        for t in (0.0, -0.0, c, -c, c - f32(1e-6 * c), f32(-1e-7), 1.25, -1.25, 39.999996, 7 * c + 0.5 * c, -1e5, 1e5, f32(0.1) * 200):
            tx, ty, tz = f32(t), f32(-t * 0.5), f32(t * 3.0)
            out.append(_s5_line("cell", t=[tx, ty, tz], cls=cls, cell=list(s5_cell(tx, ty, tz, cls))))
        for _ in range(8):
            tx, ty, tz = f32(rnd.uniform(-300, 300)), f32(rnd.uniform(-300, 300)), f32(rnd.uniform(-300, 300))
            out.append(_s5_line("cell", t=[tx, ty, tz], cls=cls, cell=list(s5_cell(tx, ty, tz, cls))))
    for hsv in ((0.0, 0.0, 0.0), (1.0, -1.0, 1.0), (0.00005, 0.00015, -0.00025), (3.0, -3.0, float("nan")), (0.123456, -0.8, 0.55),
                (f32(0.1), f32(0.2), f32(0.3)), (-2.0, 2.0, 1.99995)):
        h3 = f32v(hsv)
        out.append(_s5_line("colour", hsv=list(h3), key=s5_colour_key(*h3)))
    # character records
    for (cid, part, fb) in ((0x0123456789ABCDEF, 1, False), (77, 2, True)):
        mk = s5_model_key("Models\\Characters\\Astronaut\\SE_astronaut.mwm", 0, 1.0, None, part)
        hsv = f32v((rnd.uniform(0, 1), rnd.uniform(-1, 1), rnd.uniform(-1, 1)))
        skin = s5_skin_id("Neon_Colorable_Suit" if part == 1 else "")
        out.append(_s5_line("character", cid=cid, part=part, fallback=int(fb), mk=mk, hsv=list(hsv), skin=skin,
                            add=s5_character_add_payload(cid, mk, fb), part_payload=s5_character_part_payload(cid, part, mk, hsv, skin)))
    #.derec
    hdr = derec_header(1210014, 1792195200123, 3, 7)
    data = bytes(range(13))
    out.append(_s5_line("derec", se_version=1210014, created=1792195200123, flags=3, epoch=7, header=hdr,
                        kind=2, tick=123456789, qpc=-5, data=data, record=derec_record(2, 123456789, -5, data),
                        end=derec_record(0xFFFF, 123456790, 99)))
    return out

def qrot_rows(rnd, max_angle):
    """A small rotation as an SE matrix (identity translation)."""
    a = random_unit(rnd)
    ang = rnd.uniform(-max_angle, max_angle)
    return se_rotate_rows_world(SE_IDENTITY, a, ang)

def s5_synthetic_character_model(rnd):
    """A small skinned model: 14 vertices, 12 triangles, 4 submeshes (one overlapping, triangles 10-11 uncovered)."""
    nv = 14
    m = {"key": 0xDEADBEEF12345678, "lod": 0, "flags": MODEL_FLAG_SKINNED | MODEL_FLAG_HAS_TANGENTS, "pattern_scale": 1.0}
    m["positions"] = [f32v((rnd.uniform(-1, 1), rnd.uniform(0, 2), rnd.uniform(-0.4, 0.4))) for _ in range(nv)]
    m["normals"] = [f32v(random_unit(rnd)) for _ in range(nv)]
    m["tangents"] = [f32v(random_unit(rnd)) for _ in range(nv)]
    m["binormals"] = [f32v(random_unit(rnd)) for _ in range(nv)]
    m["uvs"] = [f32v((rnd.uniform(0, 1), rnd.uniform(0, 1))) for _ in range(nv)]
    m["bone_indices"] = [[rnd.randrange(78) for _ in range(4)] for _ in range(nv)]
    m["bone_weights"] = [list(f32v((0.5, 0.25, 0.25, 0.0))) for _ in range(nv)]
    m["indices"] = [rnd.randrange(nv) for _ in range(36)]
    k = material_key
    m["submeshes"] = [(k(0, 0, "a_cm.dds"), 0, 4, 0), (k(0, 0, "head_cm.dds"), 4, 3, 0), (k(15, 0, "glass_cm.dds"), 6, 3, 15),
                      (k(0, 0, "b_cm.dds"), 9, 1, 0)]
    return m

def s5_vectors_text():
    return "".join(line + "\n" for line in s5_vector_records())

def s5_vectors(path):
    text = s5_vectors_text()
    if path == "-":
        sys.stdout.write(text)
        return
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print(f"S5 vectors: wrote {text.count(chr(10))} records, sha256 {hashlib.sha256(text.encode()).hexdigest()} -> {path}")

def s5_selftest():
    """The S5 conventions against CONTRACT 5.4 (pins, example vectors) and the Python-only consistency checks."""
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(("PASS " if cond else "FAIL ") + name + (": " + detail if detail else ""))
    rows = s5_skeleton_rows()
    sh = s5_skeleton_hash(rows)
    check("skeleton rows", len(rows) == 78 and all(int(r[0]) == i for i, r in enumerate(rows)), f"{len(rows)} rows")
    check("skeleton parents precede children", all(int(r[2]) < i for i, r in enumerate(rows)))
    check("skeleton seParent column", all((r[3] == "-") == (int(r[2]) < 0) and (int(r[2]) < 0 or rows[int(r[2])][1] == r[3]) for r in rows))
    check("skeleton roles", all(r[5] in ("root", "human", "twist", "follow", "dummy") for r in rows))
    humans = {r[4] for r in rows if r[4] != "-"}
    check("15 required human bones mapped", all(h in humans for h in S5_REQUIRED_HUMAN))
    check("skeleton hash pinned", sh == S5_SKELETON_HASH, "0x%016x" % sh)
    text = s5_conv_text(sh)
    h = fnv1a64(text.encode("ascii"))
    check("conventions hash pinned", h == S5_CONV_HASH and text.count("\n") == 37 and len(text) == 1539,
          "0x%016x, %d lines, %d bytes" % (h, text.count("\n"), len(text)))
    mk = s5_model_key("models\\cubes\\large\\armorcube.mwm", 0)
    fk = s5_text_key("fat|-3,0,12")
    ex = ((fk, 0x5148ADB433AE18CF), (s5_text_key("sub|5148adb433ae18cf|DoorLeft"), 0x09B78CE1C6718CC7),
          (s5_text_key(s5_subpart_key_text(fk, ["TurretBase", "TurretBarrel"])), 0xF4A128D807CA725C),
          (s5_skin_id("clean_armor"), 0xFDED4532), (mk, 0xFF3F6E15D5FFF26A),
          (s5_cube_key(mk, 2.5, 0.0, -5.0, 0.0), 0xEB6D37388D73D6D4), (s5_edge_key(mk, 2.5, 0.0, -5.0, 0.0), 0x3DAB9DA5885325A6))
    # The key vectors first handed to the S5 builders (0xf18ee13d1b3e0dec / 0xb59d1fc7857bd192 / 0xd5f1574eb7e10570) hash
 # a text with BEL (0x07) where the path has \a (a shell unescaped the backslash); the values above hash the
 # real text models\cubes\large\armorcube.mwm|lod0.
    check("CONTRACT 5.4 example vectors", all(a == b for a, b in ex), ", ".join("0x%x" % a for a, _ in ex))
    check("model key S3 compatible", s5_model_key("Models/Cubes/Large/ArmorCube.mwm", 0) == model_key("Models/Cubes/Large/ArmorCube.mwm", 0))
    check("version nibble", [s5_version(f) for f in (0, S5_V1, 2 << 28, 0xF0000000)] == [0, 1, 2, 2])
    # skinning: the Unity route (conjugated bone and bind matrices) equals the mirror of the SE route
    rnd = random.Random(5)
    names, parents, local, posed = s5_synthetic_skeleton(rnd, rows)
    c2m, anc = s5_bone_map(names, parents, rows)
    abs_bind = s5_abs_from_local(local, parents)
    worst = 0.0
    for _ in range(50):
        c = rnd.randrange(78)
        bind = se_inverse(abs_bind[c2m[c]])
        v = (rnd.uniform(-1, 1), rnd.uniform(0, 2), rnd.uniform(-1, 1))
        se_p = se_transform(posed[c2m[c]], se_transform(bind, v))
        (bc, bt), (pc, pt) = s5_to_unity_affine(bind), s5_to_unity_affine(posed[c2m[c]])
        u = affine_point(pc, pt, affine_point(bc, bt, mirror(v)))
        worst = max(worst, norm(sub(u, mirror(se_p))))
        # rest pose: the bind cancels the absolute bind
        rest = se_transform(abs_bind[c2m[c]], se_transform(bind, v))
        worst = max(worst, norm(sub(rest, v)))
    check("skinning conjugation", worst < 1e-12, "worst %.3g m" % worst)
    # relative pose: rel x parent == child, and the subgrid point maps the same through either path
    worst = 0.0
    for _ in range(50):
        parent = random_se_matrix(rnd, (rnd.uniform(-2e6, 2e6), rnd.uniform(-2e6, 2e6), rnd.uniform(-2e6, 2e6)))
        child = se_mul(_s5_rigid(rnd, 30.0), parent)
        l = (rnd.uniform(-5, 5), rnd.uniform(-5, 5), rnd.uniform(-5, 5))
        worst = max(worst, norm(sub(se_transform(se_mul(s5_relative(child, parent), parent), l), se_transform(child, l))))
    check("subgrid relative pose", worst < 1e-6, "worst %.3g m at 2,000 km" % worst)
    i, w = s5_remap_weights(anc, [names.index("DE_Extra_B"), names.index("DE_Extra_A"), names.index("SE_RigLForearm1"), 0],
                            [0.25, 0.25, 0.25, 0.0])
    check("weights move to the canonical ancestor", i[0] == 26 and w[0] == 1.0, f"{i} {w}")
    a = _s5_rigid(rnd, 5.0)
    b = _s5_rigid(rnd, 5.0)
    check("interpolation endpoints", s5_interpolate(a, b, 1.0) is b and s5_interpolate(a, b, 0.0) is a)
    check("cell floor", s5_cell(-0.0001, 19.999998, 20.0, 1) == (-1, 0, 1) and s5_cell(-4.0, 3.9999998, 0.0, 2) == (-1, 0, 0))
    hdr = derec_header(0, 1, 4, 2)
    blob = hdr + derec_record(1, 5, 6, b"abc") + derec_record(0xFFFF, 7, 8)
    hh, recs, end, trunc = derec_read(blob)
    check(".derec round trip", len(hdr) == 64 and end and not trunc and recs == [(1, 5, 6, b"abc")])
    hh, recs, end, trunc = derec_read(blob[:-10])
    check(".derec truncated", not end and trunc and recs == [(1, 5, 6, b"abc")])
    print("S5 SELFTEST " + ("PASS" if ok else "FAIL"))
    return ok

# ================================================================ S6 (CONTRACT 2.6, 2.7, 4.1, 5.4 "S6 combat conventions")
# An independent reading of CONTRACT 2.6/2.7/4.1/5.4 for S6 (never generated from the C# sources): the combat conventions
# text and hash, the collision skin (section lattice, segment split, cell boxes, model-shape OBBs, the segmentation
# stand-in, sections, the SkinSet payload and its decoder), the Subnautica-side rules (refresh, subgrid placement, hit
# point frames, character capsule, shape budget, the K-pre-scaled corner error and the S6.G1-E3 bound with the S6.M5
# mutant family) and the SE damage rules (C8 record bytes, checks, dedup window, factors, hit cell). The C# twins are
# CombatConventions, CollisionSkinMath, SkinCodec, DamageRules and ShapeBudget in DeepEngineers.Wire, compared record by
# record by tests/DeepEngineers.Wire.Tests/S6VectorTests.cs ("s6vectors -") and
# tests/DeepEngineers.WireMath.Tests/S6BoundTests.cs ("s6bound", "s6vectors -"). Layout 1.0 is unchanged.
# The stand-ins (tests/standins/fake_se_producer.py, fake_sn_host.py) import: s6_build_sections, s6_skin_payload,
# s6_skin_decode, s6_synthetic_section, s6_damage_record, s6_damage_unpack, S6Dedup, s6_validate, s6_factor_for_class.
S6_CONV_HASH = 0xB6EE880712F8253B
S6_REJECT = ("None", "Generation", "Epoch", "Sequence", "Duplicate", "TooOld", "Unit", "Amount", "Kind", "Type", "Entity",
             "Section", "Frame", "HitPoint", "NoBlock", "NotDestructible", "CannotDie", "NotApplied")
S6_REJECT_CODE = {name: i for i, name in enumerate(S6_REJECT)}
S6_CONSTS = (
    ("C8FlagsExt", "GhostPlayer", 16),
    ("CombatLimits", "RefreshMm", 10), ("CombatLimits", "SkinDebounceTicks", 30), ("CombatLimits", "CapsuleRadiusMm", 250),
    ("CombatLimits", "MaxShapesPerStep", 256), ("CombatLimits", "MaxAmountSnHp", 500), ("CombatLimits", "CapsuleHeightMm", 1800),
    ("CombatLimits", "MaxHalfExtentMm", 4000), ("CombatLimits", "OverflowRecords", 4096), ("CombatLimits", "DedupWindow", 8192),
    ("CombatLimits", "SkinMaxBytes", 2097152),
    ("DamageScale", "KChar", 1), ("DamageScale", "KSmall", 1), ("DamageScale", "KLarge", 25), ("DamageScale", "Version", 1),
    ("SkinBoxFlags", "Rotated", 1), ("SkinBoxFlags", "SourceShift", 8), ("SkinBoxFlags", "SourceMask", 0xF00),
    ("SkinBoxSource", "Segment", 0), ("SkinBoxSource", "ModelShapeObb", 1), ("SkinBoxSource", "Synthetic", 15),
    ("SkinLattice", "MaxBoxCellsLarge", 3), ("SkinLattice", "SectionCellsLarge", 6), ("SkinLattice", "SectionIdBits", 10),
    ("SkinLattice", "MaxBoxCellsSmall", 16), ("SkinLattice", "SectionCellsSmall", 32), ("SkinLattice", "SectionIdBias", 512),
    ("SnDamageForward", "Normal", 0), ("SnDamageForward", "Explosive", 7), ("SnDamageForward", "Electrical", 8),
) + tuple(("DamageReject", name, i) for i, name in enumerate(S6_REJECT) if i > 0)
S6_FORMATS = (
    "characterSection id 0, capsule r 0.25 h 1.8 centred on the C1 pelvis bone, axis pelvis->head",
    "hitId snGeneration:dedupSeq, dedupSeq 1.. per Subnautica generation",
    "seDamageType 0=DeepBite 7=DeepBlast 8=DeepShock",
    "sectionCentre (i*SectionCells+(SectionCells-1)/2)*GridSize per axis",
    "sectionId ((ix+512)<<20)|((iy+512)<<10)|(iz+512), i=floorDiv(roundAway(c/GridSize),SectionCells), c=box centre",
    "skinVersion 1.. per entity per stream, a SkinSet replaces the whole skin",
)
S6_K = {"char": 1.0, "small": 1.0, "large": 25.0}
S6_MAX_HALF = 4.0
S6_MAX_HALF_ACCEPTED = 4.0001
S6_MIN_OBB_HALF = 0.005
S6_REFRESH_M = 0.010
S6_MAX_AMOUNT = 500.0
S6_MAX_HIT_ABS_M = 1e5            # review F8: |localHit component| beyond this is a HitPoint reject (DamageRules.MaxHitAbsM)
S6_SKIN_MAX_BYTES = 2097152
S6_ROTATED, S6_SOURCE_SHIFT, S6_SOURCE_MASK = 1, 8, 0xF00
S6_SRC_SEGMENT, S6_SRC_OBB, S6_SRC_SYNTH = 0, 1, 15
S6_KIND_GRID, S6_KIND_CHAR = 1, 2
S6_FLAG_MOUTH, S6_FLAG_RADIUS, S6_FLAG_FALLBACK, S6_FLAG_SUBGRID, S6_FLAG_GHOST = 1, 2, 4, 8, 16
S6_TYPE_NORMAL, S6_TYPE_EXPLOSIVE, S6_TYPE_ELECTRICAL = 0, 7, 8
S6_SE_TYPE = {0: "DeepBite", 7: "DeepBlast", 8: "DeepShock"}
S6_IDENTITY_AXES = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))
S6_PELVIS, S6_HEAD = 2, 13  # canonical bone indices (MirrorSkeleton.Pelvis / Head)

def s6_conventions_text():
    """CONTRACT 5.4 "S6 combat conventions": const lines sorted by group, value, name; the format lines sorted."""
    lines = ["ext s6 combat 1"]
    lines += [f"const {g} {n} {v}" for (g, n, v) in sorted(S6_CONSTS, key=lambda e: (e[0], e[2], e[1]))]
    lines += ["format " + f for f in sorted(S6_FORMATS)]
    return "".join(line + "\n" for line in lines)

def s6_se_damage_type_name(t):
    return S6_SE_TYPE.get(t)

# ---- lattice
def s6_round_away(x):
    """Half away from zero by the exact fraction split (as.NET Framework's Math.Round(x, AwayFromZero))."""
    t = float(math.trunc(x))
    f = x - t
    if f >= 0.5:
        t += 1.0
    elif f <= -0.5:
        t -= 1.0
    return int(t)

def s6_floor_div(a, b):
    return a // b  # Python's // floors

def s6_section_cells(large):
    return 6 if large else 32

def s6_max_box_cells(large):
    return 3 if large else 16

def s6_section_index(c, g, large):
    """(ix, iy, iz) of a box centre, or None (not finite or outside [-512, 511])."""
    S = s6_section_cells(large)
    if not all(math.isfinite(v) for v in c) or not g > 0:
        return None
    f = [v / g for v in c]
    lim = (511 + 2.0) * S
    if any(abs(v) > lim for v in f):
        return None
    idx = tuple(s6_floor_div(s6_round_away(v), S) for v in f)
    return idx if all(-512 <= i <= 511 for i in idx) else None

def s6_section_id(ix, iy, iz):
    if not all(-512 <= i <= 511 for i in (ix, iy, iz)):
        raise ValueError("section index outside [-512, 511]")
    return ((ix + 512) << 20) | ((iy + 512) << 10) | (iz + 512)

def s6_section_index_of(sid):
    return (((sid >> 20) & 1023) - 512, ((sid >> 10) & 1023) - 512, (sid & 1023) - 512)

def s6_section_centre(ix, iy, iz, g, large):
    S = s6_section_cells(large)
    h = (S - 1) / 2.0
    return ((ix * S + h) * g, (iy * S + h) * g, (iz * S + h) * g)

# ---- boxes (a box is a dict: c, h, e (3 rows), flags; SE-local metres)
def s6_box(c, h, e=S6_IDENTITY_AXES, flags=0):
    return {"c": tuple(float(v) for v in c), "h": tuple(float(v) for v in h), "e": tuple(tuple(float(x) for x in r) for r in e),
            "flags": flags}

def _s6_split_axis(lo, hi, S, M):
    out = []
    start = lo
    while start <= hi:
        end = min(hi, (s6_floor_div(start, S) + 1) * S - 1)
        n_cells = end - start + 1
        n = -(-n_cells // M)
        base, extra = divmod(n_cells, n)
        p = start
        for i in range(n):
            ln = base + (1 if i < extra else 0)
            out.append((p, p + ln - 1))
            p += ln
        start = end + 1
    return out

def s6_split_segment(seg, large):
    """seg = (minX, minY, minZ, maxX, maxY, maxZ): cut at the section lattice, then <= MaxBoxCells per axis (equal,
 larger first); pieces in z, y, x order of their minimum cell."""
    S, M = s6_section_cells(large), s6_max_box_cells(large)
    if not (seg[3] >= seg[0] and seg[4] >= seg[1] and seg[5] >= seg[2]):
        raise ValueError(f"invalid segment {seg}")
    xs = _s6_split_axis(seg[0], seg[3], S, M)
    ys = _s6_split_axis(seg[1], seg[4], S, M)
    zs = _s6_split_axis(seg[2], seg[5], S, M)
    return [(x[0], y[0], z[0], x[1], y[1], z[1]) for z in zs for y in ys for x in xs]

def s6_cell_box(r, g):
    c = ((r[0] + r[3]) / 2.0 * g, (r[1] + r[4]) / 2.0 * g, (r[2] + r[5]) / 2.0 * g)
    h = ((r[3] - r[0] + 1.0) * g / 2.0, (r[4] - r[1] + 1.0) * g / 2.0, (r[5] - r[2] + 1.0) * g / 2.0)
    return s6_box(c, h, S6_IDENTITY_AXES, S6_SRC_SEGMENT << S6_SOURCE_SHIFT)

def _s6_axis_of(v):
    e = 1e-6
    a = [abs(x) for x in v]
    for i in range(3):
        if abs(a[i] - 1.0) <= e and all(a[j] <= e for j in range(3) if j != i):
            return i
    return -1

def s6_obb_from_model_aabb(mn, mx, orient, t, scale):
    """MyCubeBlockCollector.AddShapesCustom placement of a model AABB: p' = t + R*(scale*p), R mapping x, y, z to the
 orientation rows (Right, Up, Backward); grid-aligned when the rows are a signed axis permutation."""
    r, u, b = orient[0], orient[1], orient[2]
    cm = mul(add(mn, mx), 0.5 * scale)
    hm = tuple(max(S6_MIN_OBB_HALF, abs(mx[i] - mn[i]) * 0.5 * scale) for i in range(3))
    c = add(add(add(t, mul(r, cm[0])), mul(u, cm[1])), mul(b, cm[2]))
    src = S6_SRC_OBB << S6_SOURCE_SHIFT
    ar, au, ab = _s6_axis_of(r), _s6_axis_of(u), _s6_axis_of(b)
    if min(ar, au, ab) >= 0 and len({ar, au, ab}) == 3:
        h = [0.0, 0.0, 0.0]
        h[ar], h[au], h[ab] = hm[0], hm[1], hm[2]
        return s6_box(c, h, S6_IDENTITY_AXES, src)
    e0 = unit(r)
    e1 = unit(sub(u, mul(e0, dot(u, e0))))
    e2 = cross(e0, e1)
    return s6_box(c, hm, (e0, e1, e2), src | S6_ROTATED)

def s6_split_obb(box, max_half=S6_MAX_HALF):
    hx, hy, hz = box["h"]
    e0, e1, e2 = box["e"]

    def pieces(h):
        return 1 if not h > max_half else int(math.ceil(h / max_half))
    nx, ny, nz = pieces(hx), pieces(hy), pieces(hz)
    if nx == ny == nz == 1:
        return [box]
    px, py, pz = hx / nx, hy / ny, hz / nz
    out = []
    for k in range(nz):
        for j in range(ny):
            for i in range(nx):
                ox, oy, oz = -hx + (2 * i + 1) * px, -hy + (2 * j + 1) * py, -hz + (2 * k + 1) * pz
                c = add(add(add(box["c"], mul(e0, ox)), mul(e1, oy)), mul(e2, oz))
                out.append(s6_box(c, (px, py, pz), box["e"], box["flags"]))
    return out

def s6_merge_cells_greedy(cells):
    """Stand-in for MyVoxelSegmentation(Simple): x runs per (y, z), equal runs merged along y, then along z; sorted by
 minimum cell (z, y, x). Returns ranges (minX, minY, minZ, maxX, maxY, maxZ)."""
    keys = sorted(set((c[2], c[1], c[0]) for c in cells))
    runs = []
    i = 0
    while i < len(keys):
        z, y, x0 = keys[i]
        j = i
        while j + 1 < len(keys) and keys[j + 1][0] == z and keys[j + 1][1] == y and keys[j + 1][2] == keys[j][2] + 1:
            j += 1
        runs.append([x0, y, z, keys[j][2], y, z])
        i = j + 1

    def merge(items, axis):  # axis 1 = y, 2 = z
        groups = {}
        for r in items:
            key = tuple(r[k] for k in range(6) if k not in (axis, axis + 3))
            groups.setdefault(key, []).append(r)
        out = []
        for key in groups:
            g = sorted(groups[key], key=lambda r: r[axis])
            cur = list(g[0])
            for n in g[1:]:
                if n[axis] == cur[axis + 3] + 1:
                    cur[axis + 3] = n[axis + 3]
                else:
                    out.append(cur)
                    cur = list(n)
            out.append(cur)
        return out
    boxes = merge(merge(runs, 1), 2)
    return sorted((tuple(b) for b in boxes), key=lambda b: (b[2], b[1], b[0], b[5], b[4], b[3]))

def s6_check_coverage(cells, boxes):
    """(ok, error): the union of the boxes is exactly the cell set and no two boxes overlap."""
    want = set(tuple(c) for c in cells)
    got = set()
    for b in boxes:
        for z in range(b[2], b[5] + 1):
            for y in range(b[1], b[4] + 1):
                for x in range(b[0], b[3] + 1):
                    if (x, y, z) not in want:
                        return False, f"box {b} covers {(x, y, z)} outside the cells"
                    if (x, y, z) in got:
                        return False, f"overlap at {(x, y, z)}"
                    got.add((x, y, z))
    missing = want - got
    return (not missing), (f"cell {sorted(missing)[0]} not covered" if missing else "")

# ---- sections and the SkinSet payload
def s6_section_bytes(sec):
    out = bytearray(struct.pack("<I3fII", sec["id"], *sec["c"], len(sec["boxes"]), sec.get("kind", S6_KIND_GRID)))
    for b in sec["boxes"]:
        out += struct.pack("<3f3f9fI", *b["c"], *b["h"], *b["e"][0], *b["e"][1], *b["e"][2], b["flags"])
    return bytes(out)

def s6_section_hash(sec):
    return fnv1a64(s6_section_bytes(sec))

def s6_build_sections(segments, obbs, g, large):
    """(sections, dropped): split, assign each piece to the section of its centre, order (ids ascending; Segment
 boxes by minimum cell z, y, x, then OBB pieces in input order); a section is a dict id, c, kind, boxes, hash."""
    cell_pieces, obb_pieces, dropped = {}, {}, 0
    for seg in segments:
        for p in s6_split_segment(seg, large):
            idx = s6_section_index(s6_cell_box(p, g)["c"], g, large)
            if idx is None:
                dropped += 1
                continue
            cell_pieces.setdefault(s6_section_id(*idx), []).append(p)
    for obb in obbs:
        for p in s6_split_obb(obb):
            idx = s6_section_index(p["c"], g, large)
            if idx is None:
                dropped += 1
                continue
            obb_pieces.setdefault(s6_section_id(*idx), []).append(p)
    out = []
    for sid in sorted(set(cell_pieces) | set(obb_pieces)):
        boxes = [s6_cell_box(r, g) for r in sorted(cell_pieces.get(sid, []), key=lambda r: (r[2], r[1], r[0], r[5], r[4], r[3]))]
        boxes += obb_pieces.get(sid, [])
        sec = {"id": sid, "c": s6_section_centre(*s6_section_index_of(sid), g, large), "kind": S6_KIND_GRID, "boxes": boxes}
        sec["hash"] = s6_section_hash(sec)
        out.append(sec)
    return out, dropped

def s6_synthetic_section(sid, centre, boxes):
    """A section for the stand-in producer: boxes as dicts (s6_box), flags forced to the Synthetic source (Rotated kept)."""
    bx = [dict(b, flags=(b["flags"] & S6_ROTATED) | (S6_SRC_SYNTH << S6_SOURCE_SHIFT)) for b in boxes]
    sec = {"id": sid, "c": tuple(centre), "kind": S6_KIND_GRID, "boxes": bx}
    sec["hash"] = s6_section_hash(sec)
    return sec

def s6_skin_payload(entity_id, version, sections):
    """C4 type 6 payload (without the 8-byte message header): u64 entityId, u32 skinVersion, u32 sectionCount, sections."""
    ids = [s["id"] for s in sections]
    if any(b <= a for a, b in zip(ids, ids[1:])) or any(not s["boxes"] for s in sections):
        raise ValueError("section ids must ascend strictly and every section needs a box")
    return struct.pack("<QII", entity_id, version, len(sections)) + b"".join(s6_section_bytes(s) for s in sections)

def s6_box_error(b):
    """The consumer's per-box check (float32 values): None when valid, else the reason."""
    if not all(math.isfinite(v) for v in b["c"]):
        return "box centre not finite"
    if not all(0 < v <= S6_MAX_HALF_ACCEPTED for v in b["h"]):
        return "half extent out of (0, 4.0001]"
    e = b["e"]
    if not all(math.isfinite(v) for r in e for v in r):
        return "axes not finite"
    for i in range(3):
        for j in range(i, 3):
            if abs(dot(e[i], e[j]) - (1.0 if i == j else 0.0)) > 1e-5:
                return "axes not orthonormal"
    if dot(e[0], cross(e[1], e[2])) <= 0:
        return "axes not right-handed"
    if b["flags"] & ~(S6_ROTATED | S6_SOURCE_MASK):
        return "unknown box flag bits"
    if not b["flags"] & S6_ROTATED and tuple(tuple(r) for r in e) != S6_IDENTITY_AXES:
        return "axes not the identity on a box without the Rotated flag"
    return None

def s6_skin_decode(payload):
    """(header (entityId, skinVersion, sectionCount), sections, ignored kind-2 sections); raises ValueError (skinBad)."""
    n = len(payload)
    if n < 16:
        raise ValueError("payload shorter than the 16-byte header")
    if n > S6_SKIN_MAX_BYTES:
        raise ValueError("payload larger than SkinMaxBytes")
    eid, ver, count = struct.unpack_from("<QII", payload, 0)
    if ver == 0:
        raise ValueError("skinVersion 0")
    if count > (n - 16) // 88:
        raise ValueError("sectionCount does not fit")
    off, prev, out, ignored = 16, None, [], 0
    for _ in range(count):
        if n - off < 24:
            raise ValueError("truncated section")
        sid, cx, cy, cz, nb, kind = struct.unpack_from("<I3fII", payload, off)
        if prev is not None and sid <= prev:
            raise ValueError("section ids not ascending")
        prev = sid
        if kind not in (S6_KIND_GRID, S6_KIND_CHAR):
            raise ValueError(f"section kind {kind}")
        if nb < 1:
            raise ValueError("section without a box")
        if not all(math.isfinite(v) for v in (cx, cy, cz)):
            raise ValueError("section centre not finite")
        need = 24 + 64 * nb
        if n - off < need:
            raise ValueError("truncated section")
        boxes = []
        for k in range(nb):
            v = struct.unpack_from("<3f3f9fI", payload, off + 24 + 64 * k)
            b = s6_box(v[0:3], v[3:6], (v[6:9], v[9:12], v[12:15]), v[15])
            why = s6_box_error(b)
            if why:
                raise ValueError(why)
            boxes.append(b)
        sec = {"id": sid, "c": (cx, cy, cz), "kind": kind, "boxes": boxes, "hash": fnv1a64(payload[off:off + need])}
        off += need
        if kind == S6_KIND_CHAR:
            ignored += 1
        else:
            out.append(sec)
    if off != n:
        raise ValueError("length differs from the counts")
    return (eid, ver, count), out, ignored

def s6_rho_max(sec):
    return max((norm(sub(b["c"], sec["c"])) + norm(b["h"]) for b in sec["boxes"]), default=0.0)

# ---- Subnautica side
def s6_up_in_section(q):
    return qrot(qconj(q), (0.0, 1.0, 0.0))

def s6_needs_refresh(s_applied, u_applied, s, u, rho):
    """CONTRACT 2.6: |s - s_applied|*rho or |s - 1|*|u - u_applied|*rho above 1 cm."""
    if abs(s - s_applied) * rho > S6_REFRESH_M:
        return True
    return abs(s - 1.0) * norm(sub(u, u_applied)) * rho > S6_REFRESH_M

def s6_subgrid_rotation(rel):
    """R(rel) = LookRotation(Mirror(rel.Forward), Mirror(rel.Up))."""
    return look_rotation(mirror(se_forward(rel)), mirror(rel[1]))

def s6_section_root_position(o, q, s, rel, c_sec):
    return sn_apply(o, q, s, se_transform(rel, c_sec))

def s6_qnorm(q):
    n = math.sqrt(sum(c * c for c in q))
    q = tuple(c / n for c in q)
    return tuple(-c for c in q) if q[3] < 0 else q

def s6_section_root_rotation(q, rel):
    return s6_qnorm(qmul(q, s6_subgrid_rotation(rel)))

def s6_invert_rigid(m):
    R, U, B, T = m
    return ((R[0], U[0], B[0]), (R[1], U[1], B[1]), (R[2], U[2], B[2]), (-dot(T, R), -dot(T, U), -dot(T, B)))

def s6_hit_local(o, q, s, rel, subgrid, w):
    """CONTRACT 2.7: ToLocal with the step's pose, then InvertRigid(rel) for a subgrid section."""
    l = sn_to_local_se(o, q, s, w)
    return se_transform(s6_invert_rigid(rel), l) if subgrid else l

def s6_local_to_world(o, q, s, rel, subgrid, l):
    return sn_apply(o, q, s, se_transform(rel, l) if subgrid else l)

def s6_character_capsule(rows, valid):
    """(centre, axis) in the character's SE model space: pelvis translation and unit pelvis->head, else the fallback."""
    p, h = S6_PELVIS * 12 + 9, S6_HEAD * 12 + 9
    if valid and rows is not None and len(rows) >= h + 3:
        pel, head = tuple(rows[p:p + 3]), tuple(rows[h:h + 3])
        d = sub(head, pel)
        if all(math.isfinite(v) for v in pel + head) and norm(d) > 1e-3:
            return pel, mul(d, 1.0 / norm(d))
    return (0.0, 0.9, 0.0), (0.0, 1.0, 0.0)

def s6_character_capsule_world(o, q, s, centre, axis):
    c = sn_apply(o, q, s, centre)
    y = sn_apply_dir(q, s, axis)
    y = mul(y, 1.0 / norm(y)) if norm(y) > 1e-12 else qrot(q, (0.0, 1.0, 0.0))
    z = qrot(q, (0.0, 0.0, 1.0))
    z = sub(z, mul(y, dot(z, y)))
    if norm(z) < 1e-6:
        z = qrot(q, (1.0, 0.0, 0.0))
        z = sub(z, mul(y, dot(z, y)))
    z = unit(z)
    return c, quat_from_cols(cross(y, z), y, z)

def s6_budget_plan(pending, cursor, budget):
    """Round robin of `budget` shapes over the work items from `cursor`: (grants, next cursor)."""
    n = len(pending)
    grants = [0] * n
    if n == 0:
        return grants, 0
    start = cursor % n
    remaining, nxt, anything = max(0, budget), start, False
    for k in range(n):
        if remaining <= 0:
            break
        i = (start + k) % n
        want = max(0, pending[i])
        if want == 0:
            continue
        g = min(want, remaining)
        grants[i] = g
        remaining -= g
        anything = True
        nxt = i if g < want else (i + 1) % n
    return grants, (nxt if anything else start)

def s6_prescale_corner_error(q, s, h, axes):
    """Worst corner deviation of the K-pre-scaled box (box_collider) from K*Mirror(corner offset): the S6.G1-E3
 per-box prediction (independent of o, c_sec and the box centre)."""
    centre, h_u, axes_u, rq = box_collider(q, s, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0), h, axes)
    worst = 0.0
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                d = add(add(mul(axes[0], sx * h[0]), mul(axes[1], sy * h[1])), mul(axes[2], sz * h[2]))
                exact = k_apply(q, s, mirror(d))
                placed = add(centre, qrot(rq, (sx * h_u[0], sy * h_u[1], -sz * h_u[2])))
                worst = max(worst, norm(sub(placed, exact)))
    return worst

def s6_tilt_deg(q):
    return math.degrees(math.acos(max(-1.0, min(1.0, qrot(q, (0.0, 1.0, 0.0))[1]))))

def _s6_hull(site, rnd, depth, tilt_deg):
    """A hull at SN y = -depth (x, z within 2 km of the site anchor), up tilted by exactly tilt_deg towards a random
 azimuth, random heading; returns (m, o, q, s) with the pivot at the hull origin."""
    T = site.sn_to_se((rnd.uniform(-2000, 2000), -depth, rnd.uniform(-2000, 2000)))
    E, U, N = site.frame_at(T)
    az = rnd.uniform(0, 2 * math.pi)
    t = math.radians(tilt_deg)
    up = add(mul(U, math.cos(t)), mul(add(mul(E, math.cos(az)), mul(N, math.sin(az))), math.sin(t)))
    f = random_unit(rnd)
    f = unit(sub(f, mul(up, dot(f, up))))
    m = (cross(f, up), up, mul(f, -1.0), T)
    o, q, s = wire_pose(site, m, T)
    return m, o, q, s

def s6_lossy_scale(W, s):
    """Unity's lossyScale of a unit-scale transform with world rotation W under the Frame scale diag(s, 1, s):
 diag(W^-1 S W) (GetWorldScaleLossy; HYPOTHESIS for Unity 2019.4, its manual says it only approximates a skew)."""
    out = []
    for e in S6_IDENTITY_AXES:
        c = qrot(W, e)
        out.append(dot(c, scale_h(c, s)))
    return tuple(out)

def s6_m5_box_error(o, q, s, b, h, axes, rotated):
    """S6.M5 model ("colliders parented under the scaled Frame", CONTRACT 2.9): Frame (o, I, (s, 1, s)) > Entity (q).
 A grid-aligned box is a BoxCollider on the Entity (center Mirror(b), size 2h); a rotated box is on a unit-scale child
 at localPosition Mirror(b) with the rigid rotation (Mirror(e0), Mirror(e1), -Mirror(e2)). The PhysX shape (HYPOTHESIS,
 Unity 2019.4): rotation = the transform's world rotation W, half extents = h * lossyScale, centre = transform position
 + W*(lossyScale * center). Returns the worst corner deviation from the exact affine."""
    if rotated:
        W = qmul(q, quat_from_cols(mirror(axes[0]), mirror(axes[1]), mul(mirror(axes[2]), -1.0)))
        pos = sn_apply(o, q, s, b)
    else:
        W = q
        ls0 = s6_lossy_scale(W, s)
        mb = mirror(b)
        pos = add(o, qrot(W, (ls0[0] * mb[0], ls0[1] * mb[1], ls0[2] * mb[2])))
    ls = s6_lossy_scale(W, s)
    worst = 0.0
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                corner = add(b, add(add(mul(axes[0], sx * h[0]), mul(axes[1], sy * h[1])), mul(axes[2], sz * h[2])))
                exact = sn_apply(o, q, s, corner)
                if rotated:
                    loc = (sx * h[0] * ls[0], sy * h[1] * ls[1], -sz * h[2] * ls[2])
                else:  # Unity-local corner offset Mirror(d) for identity axes
                    loc = (sx * h[0] * ls[0], sy * h[1] * ls[1], -sz * h[2] * ls[2])
                worst = max(worst, norm(sub(add(pos, qrot(W, loc)), exact)))
    return worst

def s6_box_bound(site, depth, tilt_deg, h=(4.0, 4.0, 4.0), offset=8.0, samples=400, seed=1, axes_mode="random", m5=False):
    """The S6.G1-E3 bound (CONTRACT 2.6): the worst corner deviation of a K-pre-scaled box from the exact affine over
 random headings, azimuths and box axes ("random"; "level" = identity axes) at SN y = -depth with the hull tilted
 by tilt_deg, box centre up to `offset` m from c_sec per axis (c_sec within the WM-1 ranges of the entity origin);
 bound = max(1.02 * worst, 0.001 m). With m5, also the S6.M5 family's worst deviations (level boxes on the Entity:
 m5LevelWorstM; rotated boxes on children: m5RotatedWorstM) over the same hulls and boxes."""
    rnd = random.Random(int(seed))
    worst = 0.0
    s_first = None
    m5_level = m5_rot = 0.0
    for _ in range(int(samples)):
        m, o, q, s = _s6_hull(site, rnd, float(depth), float(tilt_deg))
        if s_first is None:
            s_first = s
        axes = S6_IDENTITY_AXES if axes_mode == "level" else random_se_matrix(rnd, (0.0, 0.0, 0.0))[:3]
        c_sec = (rnd.uniform(-50, 50), rnd.uniform(-10, 10), rnd.uniform(-50, 50))
        b = tuple(c_sec[k] + rnd.uniform(-offset, offset) for k in range(3))
        worst = max(worst, s6_prescale_corner_error(q, s, h, axes))
        if m5:
            m5_level = max(m5_level, s6_m5_box_error(o, q, s, b, h, S6_IDENTITY_AXES, False))
            m5_rot = max(m5_rot, s6_m5_box_error(o, q, s, b, h, axes, True))
    out = {"depth": float(depth), "tilt": float(tilt_deg), "s": s_first, "h": [float(v) for v in h], "offset": float(offset),
           "samples": int(samples), "seed": int(seed), "axes": axes_mode, "worstM": worst, "boundM": max(1.02 * worst, 0.001)}
    if m5:
        out["m5LevelWorstM"] = m5_level
        out["m5RotatedWorstM"] = m5_rot
    return out

# ---- SE damage rules
_S6_DAMAGE_FMT = "<IIIffHHIIQQQ3f3f3fiIId16x"
assert struct.calcsize(_S6_DAMAGE_FMT) == 128
_S6_DAMAGE_FIELDS = ("dedupSeq", "snGeneration", "unit", "amount", "seScaleExpected", "snDamageType", "flags", "targetKind",
                     "sectionId", "targetEntityId", "rootEntityId", "snapshotTick", "localHit", "snWorldHit", "attackDirSn",
                     "dealerTechType", "dealerInstanceId", "worldEpoch", "snTimeSec")

def s6_damage_record(dedupSeq=1, snGeneration=1, unit=1, amount=1.0, seScaleExpected=1.0, snDamageType=0, flags=0,
                     targetKind=1, sectionId=0, targetEntityId=0, rootEntityId=0, snapshotTick=0, localHit=(0.0, 0.0, 0.0),
                     snWorldHit=(0.0, 0.0, 0.0), attackDirSn=(0.0, 0.0, 0.0), dealerTechType=0, dealerInstanceId=0,
                     worldEpoch=0, snTimeSec=0.0):
    """The 128-byte C8 record (CONTRACT 5.4)."""
    return struct.pack(_S6_DAMAGE_FMT, dedupSeq, snGeneration, unit, amount, seScaleExpected, snDamageType, flags, targetKind,
                       sectionId, targetEntityId, rootEntityId, snapshotTick, *localHit, *snWorldHit, *attackDirSn,
                       dealerTechType, dealerInstanceId, worldEpoch, snTimeSec)

def s6_damage_unpack(data):
    v = struct.unpack(_S6_DAMAGE_FMT, bytes(data[:128]))
    d = dict(zip(_S6_DAMAGE_FIELDS[:12], v[:12]))
    d["localHit"], d["snWorldHit"], d["attackDirSn"] = v[12:15], v[15:18], v[18:21]
    d.update(zip(_S6_DAMAGE_FIELDS[15:], v[21:]))
    return d

def s6_check_header(r, gen, epoch):
    if r["snGeneration"] != gen:
        return "Generation"
    if r["worldEpoch"] != epoch:
        return "Epoch"
    return "None"

def s6_check_payload(r):
    if r["unit"] != 1:
        return "Unit"
    a = r["amount"]
    if not math.isfinite(a) or not a > 0 or a > S6_MAX_AMOUNT:
        return "Amount"
    if r["targetKind"] not in (S6_KIND_GRID, S6_KIND_CHAR):
        return "Kind"
    if r["snDamageType"] not in S6_SE_TYPE:
        return "Type"
    if not all(math.isfinite(v) and abs(v) <= S6_MAX_HIT_ABS_M for v in r["localHit"]):   # review F8: magnitude bound too
        return "HitPoint"
    return "None"

def s6_check_frame(r, target_is_subgrid):
    flag = bool(r["flags"] & S6_FLAG_SUBGRID)
    if r["targetKind"] == S6_KIND_CHAR:
        return "Frame" if flag else "None"
    return "None" if flag == bool(target_is_subgrid) else "Frame"

class S6Dedup:
    """SE's dedup window (CONTRACT 4.1): highest seq hw and the last `window` ids per generation."""

    def __init__(self, window=8192):
        self.window = window
        self.generation = 0
        self.hw = 0
        self.seen = set()
        self.bypass = False
        self.admitted = self.duplicates = self.too_old = self.reordered = self.gaps = 0

    def reset(self, generation):
        self.generation, self.hw, self.seen = generation, 0, set()

    def admit(self, seq):
        if seq == 0:
            return "Sequence"
        if self.bypass:
            self.admitted += 1
            return "None"
        if seq > self.hw:
            if seq > self.hw + 1:
                self.gaps += 1
            self.hw = seq
            self.seen = {x for x in self.seen if x > seq - self.window}
            self.seen.add(seq)
            self.admitted += 1
            return "None"
        if seq <= self.hw - self.window:
            self.too_old += 1
            return "TooOld"
        if seq in self.seen:
            self.duplicates += 1
            return "Duplicate"
        self.seen.add(seq)
        self.reordered += 1
        self.admitted += 1
        return "None"

def s6_validate(r, gen, epoch, dedup, target=None):
    """CONTRACT 4.1 order: header, dedup (marks the id), payload, target(r) -> (reason, is_subgrid), frame."""
    x = s6_check_header(r, gen, epoch)
    if x != "None":
        return x
    if dedup is not None:
        if dedup.generation != gen:
            dedup.reset(gen)
        x = dedup.admit(r["dedupSeq"])
        if x != "None":
            return x
    elif r["dedupSeq"] == 0:
        return "Sequence"
    x = s6_check_payload(r)
    if x != "None":
        return x
    sub_ = False
    if target is not None:
        x, sub_ = target(r)
        if x != "None":
            return x
    return s6_check_frame(r, sub_)

def s6_factor(kind, large):
    if kind == S6_KIND_CHAR:
        return S6_K["char"]
    if kind == S6_KIND_GRID:
        return S6_K["large"] if large else S6_K["small"]
    return float("nan")

def s6_factor_for_class(kind, target_class):
    if kind == S6_KIND_CHAR:
        return S6_K["char"]
    if kind != S6_KIND_GRID or target_class not in (1, 2):
        return float("nan")
    return S6_K["large"] if target_class == 1 else S6_K["small"]

def s6_scale_matches(expected, k):
    return math.isfinite(k) and math.isfinite(expected) and abs(expected - k) <= 1e-6 * max(1.0, abs(k))

def s6_hit_cell(l, g):
    return tuple(s6_round_away(v / g) for v in l)

def s6_nearest_occupied(l, g, occupied):
    """(cell, searched) or None: the own cell when occupied, else the occupied neighbour nearest to l (ties x, y, z)."""
    c = s6_hit_cell(l, g)
    if c in occupied:
        return c, False
    best, best_d = None, None
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                if dx == dy == dz == 0:
                    continue
                n = (c[0] + dx, c[1] + dy, c[2] + dz)
                if n not in occupied:
                    continue
                d = sum((n[i] * g - l[i]) ** 2 for i in range(3))
                if best is None or d < best_d or (d == best_d and n < best):
                    best, best_d = n, d
    return (best, True) if best is not None else None

def s6_hit_id(gen, seq):
    return f"{gen}:{seq}"

# ---- vectors for S6VectorTests / S6BoundTests (the S5 line format)
def _s6_box_flat(b):
    return list(b["c"]) + list(b["h"]) + [v for r in b["e"] for v in r]

def _s6_rand_rot_rows(rnd):
    return random_se_matrix(rnd, (0.0, 0.0, 0.0))[:3]

def s6_vector_records():
    rnd = random.Random(4546_0006)
    site = site_v1.SITE_V1
    out = []
    L = _s5_line
    text = s6_conventions_text()
    out.append(L("conv", text=text, hash=fnv1a64(text.encode("ascii")), lines=text.count("\n"), bytes=len(text)))
    for t in (0, 7, 8, 1, 2, 9, 255):
        out.append(L("dtype", t=t, name=s6_se_damage_type_name(t) or ""))

    # rounding and floor division
    xs = [0.5, -0.5, 1.5, -1.5, 2.5, -2.5, 0.49999999999999994, -0.49999999999999994, 2.5000000000000004, 1e9 + 0.5,
          -7.4999999999, 3.0, -3.0, 0.0, -0.0]
    xs += [rnd.uniform(-5000, 5000) for _ in range(40)] + [rnd.randint(-4000, 4000) / 2.0 for _ in range(20)]
    for x in xs:
        out.append(L("round", x=float(x), r=s6_round_away(x)))
    for a in list(range(-13, 14)) + [rnd.randint(-100000, 100000) for _ in range(20)]:
        for b in (1, 3, 6, 32):
            out.append(L("floordiv", a=a, b=b, q=s6_floor_div(a, b)))

    # lattice
    for large, g in ((True, 2.5), (False, 0.5)):
        S = s6_section_cells(large)
        pts = [(0.0, 0.0, 0.0), (g * (S - 0.5), 0.0, 0.0), (g * S, -g * 0.5, -g * S), (-g * 0.5, g * 0.49999, 3 * g * S),
               (511 * S * g + (S - 1) * g, 0.0, 0.0), (512 * S * g, 0.0, 0.0), (-512 * S * g, 0.0, 0.0),
               (-512 * S * g - g, 0.0, 0.0), (float("nan"), 0.0, 0.0), (float("inf"), 0.0, 0.0)]
        pts += [(rnd.uniform(-3000, 3000), rnd.uniform(-500, 500), rnd.uniform(-3000, 3000)) for _ in range(60)]
        for c in pts:
            idx = s6_section_index(c, g, large)
            if idx is None:
                out.append(L("section", c=list(c), g=g, large=large, ok=0))
            else:
                sid = s6_section_id(*idx)
                out.append(L("section", c=list(c), g=g, large=large, ok=1, ix=idx[0], iy=idx[1], iz=idx[2], id=sid,
                             centre=list(s6_section_centre(*idx, g, large))))

    # segment split and cell boxes
    segs = [(0, 0, 0, 0, 0, 0), (0, 0, 0, 6, 0, 0), (-1, 0, 0, 5, 0, 0), (-7, -3, 2, 13, 4, 11), (0, 0, 0, 31, 0, 0),
            (-33, 0, 0, 40, 17, 2), (5, 5, 5, 5, 5, 5)]
    for _ in range(30):
        lo = [rnd.randint(-80, 80) for _ in range(3)]
        segs.append(tuple(lo) + tuple(lo[i] + rnd.randint(0, 40) for i in range(3)))
    for large, g in ((True, 2.5), (False, 0.5)):
        for seg in segs:
            pieces = s6_split_segment(seg, large)
            out.append(L("split", seg=list(seg), large=large, pieces=[v for p in pieces for v in p]))
            for p in pieces[:3]:
                b = s6_cell_box(p, g)
                out.append(L("cellbox", r=list(p), g=g, c=list(b["c"]), h=list(b["h"])))

    # model-shape OBBs and their split
    orients = [S6_IDENTITY_AXES, ((0.0, 0.0, -1.0), (0.0, 1.0, 0.0), (1.0, 0.0, 0.0)),
               ((0.0, 1.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 0.0, 1.0)), ((-1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0))]
    orients += [_s6_rand_rot_rows(rnd) for _ in range(12)]
    for orient in orients:
        for _ in range(2):
            mn = (rnd.uniform(-6, 1), rnd.uniform(-6, 1), rnd.uniform(-6, 1))
            mx = tuple(mn[i] + rnd.choice((0.0, rnd.uniform(0.2, 12.0))) for i in range(3))
            t = (rnd.uniform(-20, 20), rnd.uniform(-20, 20), rnd.uniform(-20, 20))
            scale = rnd.choice((1.0, 0.5, 2.5, rnd.uniform(0.3, 3.0)))
            b = s6_obb_from_model_aabb(mn, mx, orient, t, scale)
            out.append(L("obb", mn=list(mn), mx=list(mx), orient=[v for r in orient for v in r], t=list(t), scale=scale,
                         box=_s6_box_flat(b), flags=b["flags"]))
            pieces = s6_split_obb(b)
            out.append(L("splitobb", box=_s6_box_flat(b), flags=b["flags"], pieces=[v for p in pieces for v in _s6_box_flat(p)]))

    # segmentation stand-in
    cell_sets = [[(0, 0, 0)], [(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0)], [(x, y, z) for x in range(4) for y in range(3) for z in range(2)],
                 [(0, 0, 0), (2, 0, 0), (0, 0, 2), (1, 1, 1)]]
    for _ in range(12):
        n = rnd.randint(1, 120)
        cell_sets.append([(rnd.randint(-5, 5), rnd.randint(-3, 3), rnd.randint(-5, 5)) for _ in range(n)])
    hull = [(x, y, z) for x in range(-8, 9) for y in range(-2, 3) for z in range(-20, 21) if abs(x) + abs(y) * 2 <= 9]
    cell_sets.append(hull)
    for cells in cell_sets:
        boxes = s6_merge_cells_greedy(cells)
        ok, err = s6_check_coverage(cells, boxes)
        assert ok, err
        out.append(L("merge", cells=[v for c in cells for v in c], boxes=[v for b in boxes for v in b]))

    # sections and payloads
    def build_case(cells, obbs, g, large, entity, version):
        segs_ = s6_merge_cells_greedy(cells)
        secs, dropped = s6_build_sections(segs_, obbs, g, large)
        payload = s6_skin_payload(entity, version, secs)
        out.append(L("build", g=g, large=large, segs=[v for s_ in segs_ for v in s_], obbs=[v for b in obbs for v in _s6_box_flat(b)],
                     obbflags=[b["flags"] for b in obbs], entity=entity, version=version, payload=payload,
                     ids=[s_["id"] for s_ in secs], hashes=[s_["hash"] for s_ in secs], rho=[s6_rho_max(s_) for s_ in secs],
                     dropped=dropped))
        return secs, payload
    build_case(hull, [], 2.5, True, 0x1122334455667788, 1)
    big = [(x, y, z) for x in range(-20, 21) for y in range(-3, 4) for z in range(-60, 61) if abs(x) + abs(y) * 3 <= 21]
    obbs = [s6_obb_from_model_aabb((-0.5, -0.5, -1.2), (0.5, 0.5, 1.2), _s6_rand_rot_rows(rnd), (rnd.uniform(-40, 40), rnd.uniform(-5, 5), rnd.uniform(-100, 100)), 1.0)
            for _ in range(6)]
    obbs.append(s6_obb_from_model_aabb((-1.0, -1.0, -6.0), (1.0, 1.0, 6.0), _s6_rand_rot_rows(rnd), (3.0, 1.0, -7.0), 1.25))
    obbs.append(s6_obb_from_model_aabb((-0.6, -0.6, -0.6), (0.6, 0.6, 0.6), orients[1], (1.25, 0.0, 2.5), 1.0))
    secs_big, payload_big = build_case(big, obbs, 2.5, True, 77, 3)
    small = [(x, y, z) for x in range(-30, 31) for y in range(-4, 5) for z in range(-50, 51) if abs(x) <= 30 - abs(y) * 2 and (x + z) % 7 != 0]
    build_case(small, obbs[:3], 0.5, False, 0xFFFFFFFFFFFFFFFF, 2)
    far = [(x, 0, 0) for x in range(3060, 3080)]  # beyond the large lattice (511*6 + 5 = 3071): pieces dropped
    build_case(far, [], 2.5, True, 5, 1)

    # decode: good payloads and every bad kind
    out.append(L("decode", payload=payload_big, ok=1, reason=""))
    good = secs_big[:2]
    base = s6_skin_payload(9, 4, good)
    out.append(L("decode", payload=base, ok=1, reason=""))
    char_sec = {"id": 0, "c": (0.0, 0.9, 0.0), "kind": S6_KIND_CHAR, "boxes": [s6_box((0, 0.9, 0), (0.25, 0.9, 0.25))]}
    with_char = s6_skin_payload(9, 4, [char_sec] + good)
    out.append(L("decode", payload=with_char, ok=1, reason="", ignored=1))
    bads = []
    bads.append((base[:-1], "length"))
    bads.append((base + b"\0", "length"))
    bads.append((base[:8] + struct.pack("<I", 0) + base[12:], "version"))
    bads.append((base[:12] + struct.pack("<I", 3) + base[16:], "count"))
    b2 = bytearray(base)
    struct.pack_into("<I", b2, 16 + 20, 3)
    bads.append((bytes(b2), "kind"))
    b2 = bytearray(base)
    struct.pack_into("<f", b2, 16 + 24 + 12, 0.0)
    bads.append((bytes(b2), "half 0"))
    b2 = bytearray(base)
    struct.pack_into("<f", b2, 16 + 24 + 12, 4.01)
    bads.append((bytes(b2), "half 4.01"))
    b2 = bytearray(base)
    struct.pack_into("<f", b2, 16 + 24 + 24, 0.9)
    bads.append((bytes(b2), "axes"))
    b2 = bytearray(base)
    struct.pack_into("<3f", b2, 16 + 24 + 48, 0.0, 0.0, -1.0)
    struct.pack_into("<I", b2, 16 + 24 + 60, 1)
    bads.append((bytes(b2), "left-handed"))
    b2 = bytearray(base)
    struct.pack_into("<I", b2, 16 + 24 + 60, 0x10000)
    bads.append((bytes(b2), "flags"))
    b2 = bytearray(base)
    struct.pack_into("<f", b2, 16 + 24, float("nan"))
    bads.append((bytes(b2), "centre"))
    sec_len = 24 + 64 * len(good[0]["boxes"])
    b2 = bytearray(base)
    struct.pack_into("<I", b2, 16 + sec_len, good[0]["id"])
    bads.append((bytes(b2), "order"))
    b2 = bytearray(base)
    struct.pack_into("<I", b2, 16 + 16, 0)
    bads.append((bytes(b2), "boxCount"))
    rot = s6_box((0.0, 0.0, 0.0), (1.0, 1.0, 1.0), ((0.0, 1.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 0.0, 1.0)), 0)
    unflagged = s6_skin_payload(1, 1, [{"id": 5, "c": (0.0, 0.0, 0.0), "kind": 1, "boxes": [rot]}])
    bads.append((unflagged, "rotated flag"))
    bads.append((b"\0" * 15, "short"))
    for payload, why in bads:
        try:
            s6_skin_decode(payload)
            ok = 1
        except ValueError:
            ok = 0
        assert ok == 0, why
        out.append(L("decode", payload=payload, ok=0, reason=why))

    # refresh rule, placement and hit point frames
    for _ in range(40):
        rho = rnd.uniform(0.5, 30)
        sA = rnd.uniform(0.99, 1.06)
        s = sA + rnd.choice((0.0, rnd.uniform(-1e-3, 1e-3), rnd.uniform(-3e-2, 3e-2)))
        uA = random_unit(rnd)
        u = unit(add(uA, mul(random_unit(rnd), rnd.choice((0.0, 1e-3, 0.05, 0.5)))))
        out.append(L("refresh", sA=sA, uA=list(uA), s=s, u=list(u), rho=rho, need=s6_needs_refresh(sA, uA, s, u, rho)))
    for _ in range(40):
        y = rnd.uniform(-2000, 300)
        m, o, q, s = _s6_hull(site, rnd, -y, rnd.uniform(0, 60))
        sub_ = rnd.random() < 0.6
        rel = random_se_matrix(rnd, (rnd.uniform(-20, 20), rnd.uniform(-5, 5), rnd.uniform(-20, 20))) if sub_ else SE_IDENTITY
        c_sec = (rnd.uniform(-40, 40), rnd.uniform(-10, 10), rnd.uniform(-40, 40))
        l = (rnd.uniform(-40, 40), rnd.uniform(-10, 10), rnd.uniform(-40, 40))
        w = s6_local_to_world(o, q, s, rel, sub_, l)
        out.append(L("place", o=list(o), q=list(q), s=s, rel=s5_rows(rel), subgrid=sub_, csec=list(c_sec),
                     rootpos=list(s6_section_root_position(o, q, s, rel, c_sec)), rootrot=list(s6_section_root_rotation(q, rel)),
                     subrot=list(s6_subgrid_rotation(rel)), up=list(s6_up_in_section(s6_section_root_rotation(q, rel))),
                     l=list(l), w=list(w), hit=list(s6_hit_local(o, q, s, rel, sub_, w)),
                     tilt=s6_tilt_deg(q)))

    # character capsule
    rows_list = []
    for k in range(4):
        rows = [rnd.uniform(-1, 1) for _ in range(78 * 12)]
        if k == 1:
            rows[S6_HEAD * 12 + 9:S6_HEAD * 12 + 12] = rows[S6_PELVIS * 12 + 9:S6_PELVIS * 12 + 12]
        rows = [f32(v) for v in rows]
        rows_list.append((rows, True))
    rows_list.append((rows_list[0][0], False))
    rows_list.append((rows_list[0][0][:100], True))
    for rows, valid in rows_list:
        c, a = s6_character_capsule(rows, valid)
        y = rnd.uniform(-500, 0)
        m, o, q, s = _s6_hull(site, rnd, -y, rnd.uniform(0, 40))
        cw, qw = s6_character_capsule_world(o, q, s, c, a)
        out.append(L("capsule", rows=rows, valid=valid, c=list(c), a=list(a), o=list(o), q=list(q), s=s, cw=list(cw), qw=list(qw)))

    # round-robin budget
    plans = [([1000], 0, 256), ([300, 0, 50, 10], 0, 256), ([300, 0, 50, 10], 2, 256), ([0, 0, 0], 1, 256), ([5, 5, 5], 7, 4),
             ([], 0, 256), ([256, 256], 1, 256), ([10, 20, 30], 1, 0)]
    for _ in range(10):
        plans.append(([rnd.choice((0, rnd.randint(1, 400))) for _ in range(rnd.randint(1, 9))], rnd.randint(0, 20), rnd.choice((256, 100, 7))))
    for pending, cursor, budget in plans:
        grants, nxt = s6_budget_plan(pending, cursor, budget)
        out.append(L("budget", pending=pending, cursor=cursor, budget=budget, grants=grants, next=nxt))

    # K-pre-scaled corner errors (per box) at depths and tilts
    for depth in (0.0, 150.0, 1000.0, 2000.0):
        for tilt in (0.0, 15.0, 45.0, 90.0):
            for k in range(3):
                m, o, q, s = _s6_hull(site, rnd, depth, tilt)
                h = (rnd.uniform(0.5, 4), rnd.uniform(0.5, 4), rnd.uniform(0.5, 4))
                axes = S6_IDENTITY_AXES if k == 0 else _s6_rand_rot_rows(rnd)
                out.append(L("cornererr", q=list(q), s=s, h=list(h), axes=[v for r in axes for v in r],
                             err=s6_prescale_corner_error(q, s, h, axes), level=k == 0, tilt=tilt))

    # S6.M5 family (Entity tilted 30 deg) vs the bound: raw cases and the per-depth summary
    for depth in (150.0, 500.0, 1000.0, 2000.0):
        bnd = s6_box_bound(site, depth, 30.0, samples=150, seed=7, m5=True)
        out.append(L("m5", depth=depth, tilt=30.0, worst=bnd["worstM"], bound=bnd["boundM"], m5level=bnd["m5LevelWorstM"],
                     m5rot=bnd["m5RotatedWorstM"]))
        for k in range(4):
            m, o, q, s = _s6_hull(site, rnd, depth, 30.0)
            rotated = k % 2 == 1
            axes = _s6_rand_rot_rows(rnd) if rotated else S6_IDENTITY_AXES
            c_sec = (rnd.uniform(-50, 50), rnd.uniform(-10, 10), rnd.uniform(-50, 50))
            b = tuple(c_sec[i] + rnd.uniform(-8, 8) for i in range(3))
            h = (4.0, 4.0, 4.0)
            out.append(L("m5case", o=list(o), q=list(q), s=s, b=list(b), h=list(h), axes=[v for r in axes for v in r],
                         rotated=rotated, err=s6_m5_box_error(o, q, s, b, h, axes, rotated),
                         kerr=s6_prescale_corner_error(q, s, h, axes)))

    # dedup window
    def dedup_case(window, seqs, bypass=False, resets=()):
        d = S6Dedup(window)
        d.bypass = bypass
        res = []
        for i, sq in enumerate(seqs):
            if i in resets:
                d.reset(d.generation + 1)
            res.append(S6_REJECT_CODE[d.admit(sq)])
        out.append(L("dedup", window=window, bypass=bypass, resets=list(resets), seqs=seqs, results=res, admitted=d.admitted,
                     duplicates=d.duplicates, tooold=d.too_old, reordered=d.reordered, gaps=d.gaps, hw=d.hw))
    dedup_case(8192, [1, 2, 3, 2, 3, 4, 0, 6, 5, 5, 7])
    dedup_case(64, list(range(1, 70)) + [5, 6, 7, 64, 65, 3, 200, 137, 136, 135])
    dedup_case(64, [10, 1, 2, 74, 11, 10, 12, 4294967295, 4294967294, 4294967295])
    dedup_case(8192, [1, 2, 2, 1], bypass=True)
    dedup_case(128, [1, 2, 3, 1, 2, 3, 4], resets=(3,))
    seqs = []
    for i in range(1, 3000):
        seqs.append(i)
        if rnd.random() < 0.2:
            seqs.append(max(1, i - rnd.randint(0, 300)))
    rnd.shuffle(seqs)
    dedup_case(256, seqs)
    dedup_case(8192, seqs)

    # damage records: checks
    def rec(**kw):
        return s6_damage_record(**kw)
    recs = [rec(dedupSeq=1, snGeneration=3, worldEpoch=9, amount=80.0, seScaleExpected=25.0, targetKind=1, sectionId=7,
                targetEntityId=100, rootEntityId=100, localHit=(1.0, 2.0, 3.0)),
            rec(dedupSeq=2, snGeneration=2, worldEpoch=9), rec(dedupSeq=3, snGeneration=3, worldEpoch=8),
            rec(dedupSeq=4, snGeneration=3, worldEpoch=9, unit=2), rec(dedupSeq=5, snGeneration=3, worldEpoch=9, amount=0.0),
            rec(dedupSeq=6, snGeneration=3, worldEpoch=9, amount=-1.0), rec(dedupSeq=7, snGeneration=3, worldEpoch=9, amount=500.5),
            rec(dedupSeq=8, snGeneration=3, worldEpoch=9, amount=500.0), rec(dedupSeq=9, snGeneration=3, worldEpoch=9, amount=float("nan")),
            rec(dedupSeq=10, snGeneration=3, worldEpoch=9, amount=float("inf")), rec(dedupSeq=11, snGeneration=3, worldEpoch=9, targetKind=3),
            rec(dedupSeq=12, snGeneration=3, worldEpoch=9, targetKind=0), rec(dedupSeq=13, snGeneration=3, worldEpoch=9, snDamageType=1),
            rec(dedupSeq=14, snGeneration=3, worldEpoch=9, snDamageType=7, flags=S6_FLAG_RADIUS | S6_FLAG_FALLBACK),
            rec(dedupSeq=15, snGeneration=3, worldEpoch=9, snDamageType=8, flags=S6_FLAG_FALLBACK),
            rec(dedupSeq=16, snGeneration=3, worldEpoch=9, localHit=(float("nan"), 0.0, 0.0)),
            rec(dedupSeq=17, snGeneration=3, worldEpoch=9, flags=S6_FLAG_SUBGRID),
            rec(dedupSeq=18, snGeneration=3, worldEpoch=9, targetKind=2, flags=S6_FLAG_SUBGRID),
            rec(dedupSeq=19, snGeneration=3, worldEpoch=9, targetKind=2, flags=S6_FLAG_GHOST, amount=1.0),
            rec(dedupSeq=0, snGeneration=3, worldEpoch=9), rec(dedupSeq=20, snGeneration=3, worldEpoch=9, unit=0, amount=0.0)]
    for _ in range(30):
        recs.append(rec(dedupSeq=rnd.randint(0, 40), snGeneration=rnd.choice((3, 3, 3, 4)), worldEpoch=rnd.choice((9, 9, 9, 1)),
                        unit=rnd.choice((1, 1, 1, 2)), amount=rnd.choice((rnd.uniform(0.1, 500), -3.0, 0.0, 501.0, 30.0)),
                        snDamageType=rnd.choice((0, 0, 7, 8, 2)), targetKind=rnd.choice((1, 1, 2, 5)), flags=rnd.choice((0, 8, 16, 4)),
                        sectionId=rnd.choice((7, 8)), targetEntityId=rnd.choice((100, 101, 200)), rootEntityId=100,
                        localHit=(rnd.uniform(-9, 9), rnd.uniform(-9, 9), rnd.uniform(-9, 9))))
    for raw in recs:
        r = s6_damage_unpack(raw)
        out.append(L("damage", rec=raw, gen=3, epoch=9, header=S6_REJECT_CODE[s6_check_header(r, 3, 9)],
                     payload=S6_REJECT_CODE[s6_check_payload(r)], framesub=S6_REJECT_CODE[s6_check_frame(r, True)],
                     frameroot=S6_REJECT_CODE[s6_check_frame(r, False)]))

    # the full order through one dedup window and a target table (entity 100: root grid sections {7}; 101: subgrid of
 # 100, sections {8}; character 200 section 0)
    def target(r):
        e, sec = r["targetEntityId"], r["sectionId"]
        if r["targetKind"] == S6_KIND_CHAR:
            if e != 200:
                return "Entity", False
            return ("None" if sec == 0 else "Section"), False
        if e == 100:
            return ("None" if sec == 7 else "Section"), False
        if e == 101:
            if r["rootEntityId"] != 100:
                return "Entity", True
            return ("None" if sec == 8 else "Section"), True
        return "Entity", False
    d = S6Dedup(8192)
    results = []
    for raw in recs:
        results.append(S6_REJECT_CODE[s6_validate(s6_damage_unpack(raw), 3, 9, d, target)])
    out.append(L("validate", recs=b"".join(recs), gen=3, epoch=9, results=results, admitted=d.admitted, duplicates=d.duplicates))

    # factors, hit cells, nearest occupied
    for kind in (0, 1, 2, 3):
        for large in (False, True):
            out.append(L("factor", kind=kind, large=large, k=s6_factor(kind, large)))
        for cls in (0, 1, 2, 3):
            out.append(L("factorclass", kind=kind, cls=cls, k=s6_factor_for_class(kind, cls)))
    for e, k in ((25.0, 25.0), (f32(1.0000001), 1.0), (24.99999, 25.0), (24.9999, 25.0), (1.000002, 1.0), (0.0, 1.0), (float("nan"), 1.0)):
        out.append(L("scale", expected=float(e), k=k, ok=s6_scale_matches(f32(e), k)))
    for _ in range(40):
        g = rnd.choice((2.5, 0.5))
        l = tuple(f32(rnd.uniform(-30, 30)) for _ in range(3))
        out.append(L("hitcell", l=list(l), g=g, cell=list(s6_hit_cell(l, g))))
    for i in range(40):
        g = rnd.choice((2.5, 0.5))
        l = tuple(f32(rnd.uniform(-3, 3) * g) for _ in range(3))
        c = s6_hit_cell(l, g)
        occ = set()
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    if rnd.random() < (0.0 if i % 5 == 0 else 0.15 if i % 3 else 0.5):
                        occ.add((c[0] + dx, c[1] + dy, c[2] + dz))
        if i % 7 == 0:
            occ.add((c[0] + 2, c[1], c[2]))  # outside the neighbourhood: never chosen
        res = s6_nearest_occupied(l, g, occ)
        occ_l = sorted(occ)
        if res is None:
            out.append(L("nearest", l=list(l), g=g, occ=[v for o_ in occ_l for v in o_], found=0))
        else:
            out.append(L("nearest", l=list(l), g=g, occ=[v for o_ in occ_l for v in o_], found=1, cell=list(res[0]), searched=res[1]))
    for gen, sq in ((1, 1), (4294967295, 4294967295), (12, 0)):
        out.append(L("hitid", gen=gen, seq=sq, text=s6_hit_id(gen, sq)))
    return out

def s6_vectors_text():
    return "".join(line + "\n" for line in s6_vector_records())

def s6_vectors(path):
    text = s6_vectors_text()
    if path == "-":
        sys.stdout.write(text)
        return
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print(f"S6 vectors: wrote {text.count(chr(10))} records, sha256 {hashlib.sha256(text.encode()).hexdigest()} -> {path}")

def s6_selftest():
    """The S6 pins and CONTRACT examples, and the Python-only consistency checks."""
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(("PASS " if cond else "FAIL ") + name + (": " + detail if detail else ""))

    text = s6_conventions_text()
    h = fnv1a64(text.encode("ascii"))
    check("S6 conventions pin", h == S6_CONV_HASH and text.count("\n") == 54 and len(text) == 2068,
          f"0x{h:016x}, {text.count(chr(10))} lines, {len(text)} bytes (CONTRACT 5.4: 0xb6ee880712f8253b, 54, 2,068)")
    check("damage scale version", dict(((g, n), v) for g, n, v in S6_CONSTS)[("DamageScale", "Version")] == 1)

    # lattice
    sid = s6_section_id(0, 0, 0)
    check("section id", sid == (512 << 20) | (512 << 10) | 512 and s6_section_index_of(sid) == (0, 0, 0)
          and s6_section_index_of(s6_section_id(-512, 511, -1)) == (-512, 511, -1))
    check("section centre", s6_section_centre(0, 0, 0, 2.5, True) == (6.25, 6.25, 6.25)
          and s6_section_centre(-1, 0, 0, 0.5, False) == (-8.25, 7.75, 7.75))
    check("section range", s6_section_index((511 * 15 + 12.5, 0, 0), 2.5, True) == (511, 0, 0)
          and s6_section_index((512 * 15, 0, 0), 2.5, True) is None and s6_section_index((-512 * 16, 0, 0), 0.5, False) == (-512, 0, 0),
          "large lattice to +7.68 km, small to -8.19 km")
    check("round half away", [s6_round_away(x) for x in (0.5, -0.5, 1.5, -2.5, 0.49999999999999994)] == [1, -1, 2, -3, 0])

    # split: lattice planes and the as-equal-as-possible pieces
    p = s6_split_segment((0, 0, 0, 6, 0, 0), True)
    check("split at lattice", p == [(0, 0, 0, 2, 0, 0), (3, 0, 0, 5, 0, 0), (6, 0, 0, 6, 0, 0)], str(p))
    p = s6_split_segment((-1, 0, 0, 5, 0, 0), True)
    check("split negative", p == [(-1, 0, 0, -1, 0, 0), (0, 0, 0, 2, 0, 0), (3, 0, 0, 5, 0, 0)], str(p))
    p = s6_split_segment((0, 0, 0, 4, 0, 0), True)
    check("split larger first", p == [(0, 0, 0, 2, 0, 0), (3, 0, 0, 4, 0, 0)], str(p))
    p = s6_split_segment((0, 0, 0, 31, 0, 0), False)
    check("split small 16", p == [(0, 0, 0, 15, 0, 0), (16, 0, 0, 31, 0, 0)], str(p))
    b = s6_cell_box((0, 0, 0, 2, 2, 2), 2.5)
    check("cell box half 3.75 m", b["h"] == (3.75, 3.75, 3.75) and b["c"] == (2.5, 2.5, 2.5))
    b = s6_cell_box((0, 0, 0, 15, 0, 0), 0.5)
    check("cell box half 4.0 m small", b["h"][0] == 4.0)

    # merge + coverage, every box inside its section cube
    rnd = random.Random(66)
    worst_out = 0.0
    for _ in range(30):
        large = rnd.random() < 0.5
        g = 2.5 if large else 0.5
        cells = list({(rnd.randint(-40, 40), rnd.randint(-6, 6), rnd.randint(-40, 40)) for _ in range(rnd.randint(1, 400))})
        segs = s6_merge_cells_greedy(cells)
        good, err = s6_check_coverage(cells, segs)
        if not good:
            check("merge coverage", False, err)
        pieces = [pp for s_ in segs for pp in s6_split_segment(s_, large)]
        good, err = s6_check_coverage(cells, pieces)
        if not good:
            check("split coverage", False, err)
        secs, dropped = s6_build_sections(segs, [], g, large)
        S = s6_section_cells(large)
        for sec in secs:
            half_sec = S * g / 2.0
            for bx in sec["boxes"]:
                for i in range(3):
                    worst_out = max(worst_out, abs(bx["c"][i] - sec["c"][i]) + bx["h"][i] - half_sec)
                if max(bx["h"]) > S6_MAX_HALF:
                    check("cell box half <= 4 m", False, str(bx["h"]))
    check("cell boxes inside their section cube", worst_out <= 1e-9, f"worst protrusion {worst_out:.2e} m")

    # OBBs
    ob = s6_obb_from_model_aabb((-1, -2, -3), (1, 2, 3), ((0.0, 0.0, -1.0), (0.0, 1.0, 0.0), (1.0, 0.0, 0.0)), (0, 0, 0), 1.0)
    check("90-degree OBB is grid-aligned", ob["e"] == S6_IDENTITY_AXES and ob["h"] == (3.0, 2.0, 1.0) and not ob["flags"] & S6_ROTATED,
          str(ob["h"]))
    ob = s6_obb_from_model_aabb((-1, -1, -10), (1, 1, 10), _s6_rand_rot_rows(rnd), (0, 0, 0), 1.0)
    pcs = s6_split_obb(ob)
    check("OBB split 10 m -> 3 pieces of 3.33 m", len(pcs) == 3 and all(abs(pp["h"][2] - 10 / 3) < 1e-12 for pp in pcs)
          and ob["flags"] & S6_ROTATED)

    # payload round trip and decode
    cells = [(x, y, z) for x in range(-6, 7) for y in range(-2, 3) for z in range(-15, 16)]
    secs, _ = s6_build_sections(s6_merge_cells_greedy(cells), [ob], 2.5, True)
    pl = s6_skin_payload(42, 1, secs)
    hdr, dsecs, ign = s6_skin_decode(pl)
    check("SkinSet round trip", hdr == (42, 1, len(secs)) and [s_["hash"] for s_ in dsecs] == [s_["hash"] for s_ in secs]
          and len(pl) == 16 + sum(24 + 64 * len(s_["boxes"]) for s_ in secs), f"{len(secs)} sections, {len(pl)} bytes")

    # refresh: 1,000 m depth change on a one-box section (CONTRACT 2.6: each face of a 4 m box moves by 6.8 cm)
    s0 = site_v1.SITE_V1.Rs / (site_v1.SITE_V1.Rs - 0.0)
    s1 = site_v1.SITE_V1.Rs / (site_v1.SITE_V1.Rs - 1000.0)
    one = {"c": (0.0, 0.0, 0.0), "boxes": [s6_box((0, 0, 0), (4.0, 4.0, 4.0))]}
    rho = s6_rho_max(one)
    check("refresh after 1,000 m", s6_needs_refresh(s0, (0, 1, 0), s1, (0, 1, 0), rho) and abs((s1 - s0) * 4.0 - 0.0678) < 1e-3,
          f"face move {(s1 - s0) * 4.0 * 100:.2f} cm, rho {rho:.2f} m")
    check("no refresh for 1 mm", not s6_needs_refresh(1.02, (0, 1, 0), 1.02 + 1e-5, (0, 1, 0), rho))

    # placement: subgrid composition equals carrying the box into the root frame
    site = site_v1.SITE_V1
    worst = worst_hit = 0.0
    for _ in range(200):
        m, o, q, s = _s6_hull(site, rnd, rnd.uniform(0, 2000), rnd.uniform(0, 60))
        rel = random_se_matrix(rnd, (rnd.uniform(-20, 20), rnd.uniform(-5, 5), rnd.uniform(-20, 20)))
        c_sec = (rnd.uniform(-20, 20), rnd.uniform(-5, 5), rnd.uniform(-20, 20))
        bb = (c_sec[0] + rnd.uniform(-8, 8), c_sec[1] + rnd.uniform(-8, 8), c_sec[2] + rnd.uniform(-8, 8))
        root = s6_section_root_position(o, q, s, rel, c_sec)
        rq = s6_section_root_rotation(q, rel)
        centre = k_apply(rq, s, mirror(sub(bb, c_sec)))
        placed = add(root, qrot(rq, centre))
        exact = sn_apply(o, q, s, se_transform(rel, bb))
        worst = max(worst, norm(sub(placed, exact)))
        w = s6_local_to_world(o, q, s, rel, True, bb)
        worst_hit = max(worst_hit, norm(sub(s6_hit_local(o, q, s, rel, True, w), bb)))
    check("subgrid section placement", worst < 1e-9, f"box centre vs Apply(o, q, s, rel*b): worst {worst:.1e} m")
    check("hit point round trip (subgrid frame)", worst_hit < 1e-9, f"worst {worst_hit:.1e} m")

    # capsule fallback
    check("capsule fallback", s6_character_capsule(None, True) == ((0.0, 0.9, 0.0), (0.0, 1.0, 0.0)))

    # budget: a 1,000-box skin takes 4 steps
    pend, cur, steps = [1000], 0, 0
    while pend[0] > 0:
        gr, cur = s6_budget_plan(pend, cur, 256)
        pend[0] -= gr[0]
        steps += 1
    check("1,000 boxes in 4 steps of 256", steps == 4)

    # dedup scenarios
    d = S6Dedup(8192)
    r_ = [d.admit(x) for x in (1, 2, 2, 5, 4, 4, 0)]
    check("dedup", r_ == ["None", "None", "Duplicate", "None", "None", "Duplicate", "Sequence"] and d.gaps == 1 and d.reordered == 1, str(r_))
    for x in range(6, 9000):
        d.admit(x)
    check("dedup too old", d.admit(10) == "TooOld" and d.admit(8999) == "Duplicate" and d.admit(8999 - 8191) == "Duplicate"
          and d.admit(8999 - 8192) == "TooOld")

    # factors (CONTRACT 4.1 worked numbers)
    check("factors", 80 * s6_factor(1, True) == 2000 and 80 * s6_factor(1, False) == 80 and 30 * s6_factor(2, False) == 30
          and s6_factor_for_class(1, 1) == 25.0 and s6_factor_for_class(1, 2) == 1.0)

    # nearest occupied
    res = s6_nearest_occupied((1.3, 0.0, 0.0), 2.5, {(0, 0, 0), (1, 0, 0)})
    check("hit cell", s6_hit_cell((1.25, -1.25, 3.74), 2.5) == (1, -1, 1) and res == ((1, 0, 0), False))
    res = s6_nearest_occupied((1.3, 0.0, 0.0), 2.5, {(0, 0, 0), (2, 0, 0)})
    check("nearest neighbour", res == ((0, 0, 0), True), str(res))

    # the S6.G1-E3 bound and the S6.M5 family
    print("\nS6.G1-E3 bound (K-pre-scaled box, h = 4, 4, 4, random axes) and the S6.M5 family (Entity tilted 30 deg):")
    print(f"  {'depth':>6} {'tilt':>5} | {'K worst':>8} {'bound':>8} | {'M5 level':>9} {'M5 rot':>8}")
    red = True
    for depth in (150.0, 1000.0, 2000.0):
        for tilt in (0.0, 30.0):
            bnd = s6_box_bound(site, depth, tilt, samples=120, seed=3, m5=True)
            print(f"  {depth:6.0f} {tilt:4.0f}d | {bnd['worstM']:8.4f} {bnd['boundM']:8.4f} | {bnd['m5LevelWorstM']:9.4f} {bnd['m5RotatedWorstM']:8.4f}")
            if tilt == 30.0:
                red &= bnd["m5LevelWorstM"] > 2 * bnd["boundM"]
    lvl = s6_box_bound(site, 1000.0, 0.0, samples=60, seed=3, axes_mode="level")
    check("level hull, level boxes: exact", lvl["worstM"] < 1e-9 and lvl["boundM"] == 0.001, f"worst {lvl['worstM']:.1e} m")
    check("S6.M5 (level boxes under the scaled Frame, 30 deg) beyond twice the bound", red)
    print("S6 SELFTEST " + ("PASS" if ok else "FAIL"))
    return ok

def s6_kv_args(tokens):
    """key=value tokens (Windows PowerShell 5.1 strips inner quotes, so no JSON on the command line)."""
    kv = {}
    for t in tokens:
        if "=" not in t:
            raise SystemExit(f"expected key=value, got {t!r}")
        k, v = t.split("=", 1)
        kv[k.strip()] = v.strip()
    return kv

def s6_bound_cli(tokens):
    kv = s6_kv_args(tokens)
    known = {"depth", "tilt", "h", "offset", "site", "samples", "seed", "axes", "m5"}
    bad = set(kv) - known
    if bad or "depth" not in kv or "tilt" not in kv:
        raise SystemExit("s6bound depth=<m> tilt=<deg> [h=4,4,4] [offset=8] [site=v1] [samples=400] [seed=1] [axes=random|level] [m5=0|1]"
                         + (f" (unknown: {sorted(bad)})" if bad else ""))
    h = tuple(float(x) for x in kv.get("h", "4,4,4").split(","))
    if len(h) != 3 or not all(0 < x <= S6_MAX_HALF for x in h):
        raise SystemExit("h needs three half extents in (0, 4]")
    res = s6_box_bound(site_by_name(kv.get("site", "v1")), float(kv["depth"]), float(kv["tilt"]), h, float(kv.get("offset", "8")),
                       int(kv.get("samples", "400")), int(kv.get("seed", "1")), kv.get("axes", "random"), kv.get("m5", "0") == "1")
    print(json.dumps(res, separators=(",", ":")))

def _site_arg(argv, default, allow_all=False):
    """Removes '--site NAME' from argv and returns NAME (or the default)."""
    if "--site" not in argv:
        return default
    i = argv.index("--site")
    names = sorted(SITES) + (["all"] if allow_all else [])
    if i + 1 >= len(argv) or argv[i + 1] not in names:
        raise SystemExit("--site needs one of: " + ", ".join(names))
    name = argv[i + 1]
    del argv[i:i + 2]
    return name

if __name__ == "__main__":
    args = sys.argv[1:]
    if args and args[0] == "selftest":
        which = _site_arg(args, "all", allow_all=True)
        names = ["example", "v1"] if which == "all" else [which]
        results = []
        for name in names:
            results.append((name, selftest(name)))
            print()
        print("SELFTEST SUMMARY: " + ", ".join(f"{SITES[nm]} {'PASS' if good else 'FAIL'}" for nm, good in results))
        sys.exit(0 if all(good for _, good in results) else 1)
    if len(args) >= 2 and args[0] == "vectors":
        site_name = _site_arg(args, "v1")
        vectors(args[1], int(args[2]) if len(args) > 2 else 10000, site_name)
        sys.exit(0)
    if len(args) >= 2 and args[0] == "s3vectors":
        s3_vectors(args[1])
        sys.exit(0)
    if len(args) >= 2 and args[0] == "s5vectors":
        s5_vectors(args[1])
        sys.exit(0)
    if args and args[0] == "s5selftest":
        sys.exit(0 if s5_selftest() else 1)
    if args and args[0] == "s6conventions":
        t = s6_conventions_text()
        sys.stdout.write(t)
        print("hash 0x%016x lines %d bytes %d" % (fnv1a64(t.encode("ascii")), t.count(chr(10)), len(t)))
        sys.exit(0)
    if args and args[0] == "s6selftest":
        sys.exit(0 if s6_selftest() else 1)
    if len(args) >= 2 and args[0] == "s6vectors":
        s6_vectors(args[1])
        sys.exit(0)
    if args and args[0] == "s6bound":
        s6_bound_cli(args[1:])
        sys.exit(0)
    if args and args[0] == "s5skeleton-cs":
        if len(args) >= 2:
            with open(args[1], "w", encoding="utf-8", newline="\n") as fh:
                fh.write(s5_skeleton_cs())
            print("wrote " + args[1])
        else:
            sys.stdout.write(s5_skeleton_cs())
        sys.exit(0)
    print(__doc__)
