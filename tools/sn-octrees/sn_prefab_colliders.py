"""Read-only: Subnautica's prefab and scene colliders (UnityPy), and the S7 statics census.

Two modes, both read the installed game in place and write nothing into it:

1. Counts (docs/recon/probe-collision.md 2.5, 8; the original mode):
 uv run --no-project --with UnityPy python -I sn_prefab_colliders.py "seamoth.prefab_*,aurora.unity_*" [v]
 prints colliders by type, trigger flag and layer per bundle (names with "v").

2. The census (ROADMAP S7 "statics into SE", S7.N7; critic G10; CONTRACT 5.4 "C12 static primitives (S7)"):
 uv run --no-project --with UnityPy python -I sn_prefab_colliders.py --census [--out PATH] [--limit N] [--subnautica DIR]
 - every class id the world references: the BatchObjectsCache (batch-objects-*.bin, as the contract names it) and,
 because the batch objects are mostly atmosphere volumes and lights, also the baked entity cells (CellsCache,
 baked-batch-cells-*.bin: wrecks, precursor structures, Lost River trees live there); both are protobuf-net object
 trees (ProtobufSerializer: length-prefixed StreamHeader, LoopHeader, GameObjectData {7: ClassId}, components);
 - each class id's prefab file from SNUnmanagedData/prefabs.db and its Addressables bundle (<name>.prefab_<hash>.bundle,
 disambiguated by the bundle's container path), plus every class a PrefabPlaceholder inside it spawns (followed
 recursively), plus the lightmapped prefab scenes (<scene>.unity_*.bundle with a __LIGHTMAPPED_PREFAB__ root: the
 Aurora) as class "scene:<scene>";
 - per class: colliders by type, trigger, layer and enabled flag, mesh colliders (G10: counted, OBB sizes), non-Default
 colliders, and per solid Default collider its chain of parents (Rigidbody kinematic or not, LiveMixin, Creature-like,
 Pickupable, Vehicle, FreezeRigidbodyWhenFar, WorldForces scripts: the runtime filter's rules, S7StaticsFilter);
 - a decision per class: "handled" (Subnautica reports it: boxes, capsules, spheres, mesh colliders as OBBs) or "skip"
 with a reason; the allow-list = the handled classes whose reported colliders sit under a Rigidbody or a LiveMixin
 (the runtime waives those two only for allow-listed class ids, never the entity types);
 - S7 review: F5 the layers the runtime scans are the player's collision layers minus TerrainCollider and
 Trigger (PhysicsManager.m_LayerCollisionMatrix of globalgamemanagers, read-only; fallback: every layer but

 count as handled; F3 a class whose colliders sit under a WorldForces script on a serialized-kinematic Rigidbody, with
 no Creature, Pickupable, Vehicle or FreezeRigidbodyWhenFar anywhere in the class, is handled and listed in
 "allowWorldForces" (the runtime waives WorldForces for it while the body is kinematic) and in "allow";
 - writes the allow-list JSON (default %LOCALAPPDATA%\\DeepEngineers\\census\\allowlist.json: derived data, never
 shipped; --out for another path) and prints the S7.N7 verdict.
 uv run --no-project --with UnityPy python -I sn_prefab_colliders.py --check [PATH]
 re-reads the caches (no UnityPy needed) and FAILs on any referenced class id that the allow-list file does not list
 as handled or skipped (S7.N7 "FAIL on an unlisted class").
 python -I sn_prefab_colliders.py --selftest
 the parsers and the rules on synthetic data (no game, no UnityPy).
"""
from __future__ import annotations

import collections
import glob
import json
import os
import struct
import sys
import time

DEFAULT_SN = r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"
LAYERS = {0: "Default", 2: "IgnoreRaycast", 4: "Water", 8: "Viewmodel", 9: "OnlyVehicle", 13: "Useable", 14: "IgnoreDepthWorldUI",
          17: "Vehicle", 19: "Player", 21: "Trigger", 23: "SubRigidbodyExclude", 26: "DiveReel", 30: "TerrainCollider"}
COLLIDERS = ("BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider", "WheelCollider", "TerrainCollider")
PRIMITIVE_KIND = {"BoxCollider": "box", "CapsuleCollider": "capsule", "SphereCollider": "sphere", "MeshCollider": "meshObb"}
# script class names that mark a hierarchy the runtime filter never reports (by entity type; CONTRACT 5.4)
CREATURE_SCRIPTS = {"Creature", "SwimBehaviour", "Locomotion", "CreatureDeath", "SwimRandom", "StayAtLeashPosition",
                    "AggressiveWhenSeeTarget", "MeleeAttack", "LastTarget", "FleeOnDamage", "CreatureFear", "AttackLastTarget"}
VEHICLE_SCRIPTS = {"Vehicle", "SeaMoth", "Exosuit"}
LIGHTMAPPED_ROOT = "__LIGHTMAPPED_PREFAB__"
ALLOWLIST_VERSION = 1

def sn_paths(sn_dir):
    sa = os.path.join(sn_dir, "Subnautica_Data", "StreamingAssets")
    um = os.path.join(sa, "SNUnmanagedData")
    return {"bundles": os.path.join(sa, "aa", "StandaloneWindows64"), "prefabsDb": os.path.join(um, "prefabs.db"),
            "batchObjects": os.path.join(um, "Build18", "BatchObjectsCache"), "cells": os.path.join(um, "Build18", "CellsCache"),
            "build": os.path.join(sa, "__buildnumber.txt")}

def default_out():
    return os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers", "census", "allowlist.json")

# ---------------------------------------------------------------- protobuf-net object trees (ProtobufSerializer)
def varint(b, i):
    r = s = 0
    while True:
        if i >= len(b):
            raise ValueError("truncated varint")
        c = b[i]
        i += 1
        r |= (c & 0x7F) << s
        s += 7
        if not c & 0x80:
            return r, i

def pb_fields(m):
    """Fields of one protobuf message: [(field, wiretype, value)] (varint ints, bytes for wire type 2)."""
    i, out = 0, []
    while i < len(m):
        k, i = varint(m, i)
        f, wt = k >> 3, k & 7
        if wt == 0:
            v, i = varint(m, i)
        elif wt == 2:
            n, i = varint(m, i)
            v = bytes(m[i:i + n])
            i += n
        elif wt == 5:
            v = bytes(m[i:i + 4])
            i += 4
        elif wt == 1:
            v = bytes(m[i:i + 8])
            i += 8
        else:
            raise ValueError("wire type %d" % wt)
        out.append((f, wt, v))
    return out

def lp(b, i):
    """One length-prefixed message (PrefixStyle.Base128, field 0: a bare varint length)."""
    n, i = varint(b, i)
    if i + n > len(b):
        raise ValueError("truncated message")
    return b[i:i + n], i + n

def first(fields, f, default=None):
    for ff, _, v in fields:
        if ff == f:
            return v
    return default

def read_object_tree(b, i, with_header=True):
    """One serialized object tree (DeserializeObjectTreeAsync): [StreamHeader] LoopHeader {1: count}, then per game object
 GameObjectData {1 CreateEmptyObject, 3 Layer, 6 Id, 7 ClassId, 8 Parent, 10 MergeObject}, LoopHeader {1: components},
 per component ComponentHeader {1: TypeName} + its data. Returns ([(classId, typeNames)], next index)."""
    if with_header:
        _, i = lp(b, i)
    loop, i = lp(b, i)
    count = first(pb_fields(loop), 1, 0)
    out = []
    for _ in range(count):
        god, i = lp(b, i)
        fd = pb_fields(god)
        cid = first(fd, 7, b"")
        cl, i = lp(b, i)
        cc = first(pb_fields(cl), 1, 0)
        types = []
        for _ in range(cc):
            ch, i = lp(b, i)
            _, i = lp(b, i)
            types.append(first(pb_fields(ch), 1, b"").decode("utf-8", "replace"))
        out.append((cid.decode("utf-8", "replace") if isinstance(cid, bytes) else "", types))
    return out, i

def parse_batch_objects(data):
    """batch-objects-*.bin: one object tree with its stream header."""
    objs, end = read_object_tree(data, 0, True)
    if end != len(data):
        raise ValueError("%d trailing bytes" % (len(data) - end))
    return objs

def parse_cells(data):
    """baked-batch-cells-*.bin (CellManager.LoadCacheBatchCellsFromStream): CellsFileHeader {1 version, 2 numCells}; per
 cell CellHeaderEx {1 cellId, 2 level, 3 dataLength, 4 legacyDataLength, 5 waiterDataLength} then the data (a tree
 with a stream header), the legacy data (the same, version >= 9) and the waiter data (trees without stream headers)."""
    i = 0
    hdr, i = lp(data, i)
    hf = pb_fields(hdr)
    version, ncells = first(hf, 1, 0), first(hf, 2, 0)
    objs = []
    for _ in range(ncells):
        ch, i = lp(data, i)
        cf = pb_fields(ch)
        dl, ll, wl = first(cf, 3, 0), first(cf, 4, 0), first(cf, 5, 0)
        for length, header in ((dl, True), (ll if version >= 9 else 0, True)):
            if length:
                blob = data[i:i + length]
                o, end = read_object_tree(blob, 0, header)
                objs.extend(o)
                i += length
        if version >= 9 and wl:
            blob = data[i:i + wl]
            j = 0
            while j < len(blob):
                o, j = read_object_tree(blob, j, False)
                objs.extend(o)
            i += wl
    if i != len(data):
        raise ValueError("%d trailing bytes" % (len(data) - i))
    return objs

def read_prefabs_db(data):
    """SNUnmanagedData/prefabs.db (PrefabDatabase.SavePrefabDatabase): i32 count, then (classId, filename).NET strings."""
    def netstr(b, i):
        n, i = varint(b, i)
        return b[i:i + n].decode("utf-8"), i + n
    count = struct.unpack_from("<i", data, 0)[0]
    i, out = 4, {}
    for _ in range(count):
        k, i = netstr(data, i)
        v, i = netstr(data, i)
        out[k] = v
    return out

def referenced_classes(paths, limit_files=None):
    """{classId: {"instances": n, "batches": set, "source": set}} from the BatchObjectsCache and the CellsCache."""
    refs = collections.defaultdict(lambda: {"instances": 0, "batches": set(), "source": set()})
    stats = {"batchObjectsFiles": 0, "cellsFiles": 0, "errors": []}
    for kind, folder, pattern, parser in (("batchObjects", paths["batchObjects"], "batch-objects-*.bin", parse_batch_objects),
                                          ("cells", paths["cells"], "baked-batch-cells-*.bin", parse_cells)):
        files = sorted(glob.glob(os.path.join(folder, pattern)))
        if limit_files:
            files = files[:limit_files]
        for f in files:
            name = os.path.basename(f)
            batch = name.rsplit("-", 3)[-3:]
            batch = ".".join(x.split(".")[0] for x in batch)
            try:
                with open(f, "rb") as fh:
                    objs = parser(fh.read())
            except Exception as e:  # a file the walker cannot read is reported, never skipped silently
                stats["errors"].append("%s: %s" % (name, e))
                continue
            stats[kind + "Files"] += 1
            for cid, _ in objs:
                if cid:
                    r = refs[cid]
                    r["instances"] += 1
                    r["batches"].add(batch)
                    r["source"].add(kind)
    return refs, stats

# ---------------------------------------------------------------- the rules (pure; mirrored by the runtime filter)
def chain_flags(go, parents, flags_of):
    """The flags of a game object and all its parents (the runtime's GetComponentInParent)."""
    acc = set()
    seen = 0
    while go is not None and seen < 256:
        acc |= flags_of.get(go, set())
        go = parents.get(go)
        seen += 1
    return acc

# collides with every layer but OnlyVehicle 9; minus TerrainCollider 30 and Trigger 21)
FALLBACK_MASK_EXCLUDED = (9, 21, 30)
PLAYER_LAYER, TRIGGER_LAYER, TERRAIN_LAYER = 19, 21, 30

def mask_from_matrix(matrix, player=PLAYER_LAYER, terrain=TERRAIN_LAYER, trigger=TRIGGER_LAYER):
    """S7StaticsFilter.PlayerSolidMask's twin: Default plus every layer the player collides with (matrix[player] bit l),
 minus TerrainCollider and Trigger."""
    row = matrix[player] & 0xFFFFFFFF
    m = 1
    for layer in range(32):
        if layer in (terrain, trigger):
            continue
        if (row >> layer) & 1:
            m |= 1 << layer
    return m

def default_mask():
    m = 0
    for layer in range(32):
        if layer not in FALLBACK_MASK_EXCLUDED:
            m |= 1 << layer
    return m

def classify_collider(c, chain, mask=1):
    """One collider: 'trigger' | 'disabled' | 'layer' | 'blocked:<why>' | 'needsWorldForces' | 'needsAllow' | 'ok'
 (S7StaticsFilter order). `mask`: the layers the runtime scans.
 'needsWorldForces': under a WorldForces script on a serialized-kinematic body, no dynamic body; the class
 decides whether the waiver applies (no Creature, Pickupable, Vehicle or FreezeRigidbodyWhenFar in the class)."""
    if not c["enabled"]:
        return "disabled"
    if c["trigger"]:
        return "trigger"
    if not (0 <= c["layer"] < 32) or not (mask >> c["layer"]) & 1:
        return "layer"
    if c["type"] not in PRIMITIVE_KIND:
        return "blocked:collider type %s" % c["type"]
    for flag, why in (("creature", "creature"), ("pickupable", "pickupable"), ("vehicle", "vehicle"), ("freeze", "freezeWhenFar")):
        if flag in chain:
            return "blocked:" + why
    if "worldForces" in chain:
        if "kinematicBody" in chain and "dynamicBody" not in chain:
            return "needsWorldForces"
        return "blocked:worldForces"
    if "dynamicBody" in chain:
        return "blocked:dynamic rigidbody"
    if "kinematicBody" in chain or "liveMixin" in chain:
        return "needsAllow"
    return "ok"

ENTITY_BLOCKERS = ("blocked:creature", "blocked:pickupable", "blocked:vehicle", "blocked:freezeWhenFar")

def waives_world_forces(cls):
    """Review F3: the class has colliders under WorldForces on a kinematic body and no entity-type blocker anywhere."""
    cc = cls["classes"]
    return cc.get("needsWorldForces", 0) > 0 and not any(cc.get(k, 0) for k in ENTITY_BLOCKERS)

def decide(cls):
    """A class's census decision from its collider classes: (status, reason, allowListed). The WorldForces waiver is `waives_world_forces`; its colliders count as reported and need the Rigidbody waiver too."""
    cc = cls["classes"]
    wf = waives_world_forces(cls)
    reported = cc.get("ok", 0) + cc.get("needsAllow", 0) + (cc.get("needsWorldForces", 0) if wf else 0)
    if cls.get("error"):
        return "skip", cls["error"], False
    if reported > 0:
        return "handled", "%d collider(s) reported (%s)%s" % (reported, ", ".join("%s %d" % kv for kv in sorted(cls["kinds"].items())),
                                                              ", WorldForces waived (kinematic)" if wf else ""), \
            cc.get("needsAllow", 0) > 0 or wf
    total = sum(cc.values())
    if total == 0:
        return "skip", "no colliders", False
    blocked = sorted(set(k[8:] for k in cc if k.startswith("blocked:")) |
                     ({"worldForces"} if cc.get("needsWorldForces") and not wf else set()))
    if blocked:
        return "skip", "never static: " + ", ".join(blocked), False
    parts = []
    if cc.get("trigger"):
        parts.append("triggers only" if len(cc) == 1 else "triggers")
    if cc.get("layer"):
        parts.append("layers outside the player's collision mask " + ",".join(sorted(cls["nonDefaultLayers"])))
    if cc.get("disabled"):
        parts.append("disabled")
    return "skip", "; ".join(parts) or "nothing reportable", False

def n7_check(referenced, doc):
    """S7.N7: every referenced class id (and every class the census followed) is listed as handled or skipped with a
 reason. Returns (ok, lines)."""
    listed = doc.get("classes", {})
    lines, bad = [], []
    for cid in sorted(referenced):
        e = listed.get(cid)
        if e is None or e.get("status") not in ("handled", "skip") or (e.get("status") == "skip" and not e.get("reason")):
            bad.append(cid)
    for cid, e in listed.items():
        if e.get("status") == "skip" and not e.get("reason"):
            bad.append(cid)
    s = doc.get("summary", {})
    lines.append("classes referenced %d, listed %d (handled %s, skipped %s), allow-listed %d (WorldForces waived %d)" % (
        len(referenced), len(listed), s.get("handled"), s.get("skipped"), len(doc.get("allow", [])), len(doc.get("allowWorldForces", []))))
    lines.append("G10: mesh colliders %s (non-trigger Default %s), non-Default colliders %s, large mesh OBBs %s" % (
        s.get("meshColliders"), s.get("meshDefaultSolid"), s.get("nonDefaultColliders"), s.get("largeMeshObbs")))
    if bad:
        lines.append("FAIL S7.N7.census: %d unlisted class(es): %s" % (len(bad), ", ".join(bad[:10])))
        return False, lines
    lines.append("PASS S7.N7.census: every referenced class is handled or skipped with a reason")
    return True, lines

# ---------------------------------------------------------------- UnityPy reading (census)
def mono_header(raw):
    """A MonoBehaviour's fixed head without a type tree: m_GameObject PPtr (12), m_Enabled (u8, aligned 4), m_Script PPtr
 (i32 fileID at 16, i64 pathID at 20), m_Name (i32 length + UTF-8, aligned 4). Returns (scriptFileId, scriptPathId,
 name, end offset)."""
    fid, pid = struct.unpack_from("<iq", raw, 16)
    n = struct.unpack_from("<i", raw, 28)[0]
    if n < 0 or 32 + n > len(raw):
        raise ValueError("bad m_Name length")
    name = raw[32:32 + n].decode("utf-8", "replace")
    end = 32 + n
    end = (end + 3) & ~3
    return fid, pid, name, end

def read_aligned_string(raw, off):
    n = struct.unpack_from("<i", raw, off)[0]
    if n < 0 or off + 4 + n > len(raw):
        raise ValueError("bad string length")
    s = raw[off + 4:off + 4 + n].decode("utf-8", "replace")
    return s, (off + 4 + n + 3) & ~3

class Census:
    def __init__(self, sn_dir, log=print):
        import UnityPy  # noqa: E402 (only the census and the counts need it)
        UnityPy.config.FALLBACK_UNITY_VERSION = "2019.4.36f1"
        self.UnityPy = UnityPy
        self.paths = sn_paths(sn_dir)
        self.log = log
        self.mask, self.mask_source = self.player_mask(sn_dir)
        self.log("layer mask 0x%08x (%s): %s" % (self.mask, self.mask_source,
                                                 ",".join(str(LAYERS.get(i, i)) for i in range(32) if (self.mask >> i) & 1)))
        self.bundle_index = collections.defaultdict(list)
        for f in os.listdir(self.paths["bundles"]):
            if f.endswith(".bundle") and "_" in f:
                self.bundle_index[f.rsplit("_", 1)[0].lower()].append(f)
        with open(self.paths["prefabsDb"], "rb") as fh:
            self.prefabs = read_prefabs_db(fh.read())

    def player_mask(self, sn_dir):
        """Review F5: the runtime's mask from the game's PhysicsManager (globalgamemanagers, read-only), else the fallback."""
        try:
            env = self.UnityPy.load(os.path.join(sn_dir, "Subnautica_Data", "globalgamemanagers"))
            for o in env.objects:
                if o.type.name == "PhysicsManager":
                    m = o.read_typetree().get("m_LayerCollisionMatrix")
                    if m and len(m) == 32:
                        return mask_from_matrix(m), "PhysicsManager.m_LayerCollisionMatrix (player row 19)"
        except Exception as e:
            return default_mask(), "fallback (%s: %s)" % (type(e).__name__, e)
        return default_mask(), "fallback (no PhysicsManager)"

    def bundle_for(self, filename):
        base = os.path.basename(filename).lower()
        cands = self.bundle_index.get(base, []) or self.bundle_index.get(base.replace(" ", ""), [])
        if not cands:   # Addressables bundle names drop the characters a file name may not carry
            squeezed = "".join(ch for ch in base if ch.isalnum() or ch in "._-")
            cands = self.bundle_index.get(squeezed, [])
        if len(cands) <= 1:
            return cands[0] if cands else None
        want = filename.replace("\\", "/").lower()
        for c in cands:   # several bundles share the base name: the container path decides
            try:
                env = self.UnityPy.load(os.path.join(self.paths["bundles"], c))
                if any(k.lower().endswith(want) for k in env.container.keys()):
                    return c
            except Exception:
                continue
        return cands[0]

    def analyze(self, bundle, lightmapped=False):
        """Colliders and their parent chains in one prefab (or lightmapped scene) bundle."""
        env = self.UnityPy.load(os.path.join(self.paths["bundles"], bundle))
        byid = {}
        for o in env.objects:
            byid[(id(o.assets_file), o.path_id)] = o
        scripts = {}
        gos, comps_of, parents, go_of_tr, names, layers = {}, {}, {}, {}, {}, {}
        for o in env.objects:
            if o.type.name == "GameObject":
                tt = o.read_typetree()
                key = (id(o.assets_file), o.path_id)
                gos[key] = o
                names[key] = tt.get("m_Name", "")
                layers[key] = tt.get("m_Layer", 0)
                comps_of[key] = [(id(o.assets_file), c["component"]["m_PathID"]) for c in tt.get("m_Component", []) if c["component"]["m_FileID"] == 0]
        for o in env.objects:
            if o.type.name in ("Transform", "RectTransform"):
                tt = o.read_typetree()
                me = (id(o.assets_file), o.path_id)
                go = (id(o.assets_file), tt["m_GameObject"]["m_PathID"])
                go_of_tr[me] = go
                fa = tt.get("m_Father", {})
                if fa.get("m_PathID"):
                    parents[go] = (id(o.assets_file), fa["m_PathID"])   # a transform for now
        parents = {g: go_of_tr.get(t) for g, t in parents.items()}
        flags_of, colliders, placeholders, script_names = {}, [], [], collections.Counter()
        mesh_cache = {}
        for g, comps in comps_of.items():
            fl = set()
            for ck in comps:
                co = byid.get(ck)
                if co is None:
                    continue
                tn = co.type.name
                if tn == "Rigidbody":
                    try:
                        fl.add("kinematicBody" if co.read_typetree().get("m_IsKinematic") else "dynamicBody")
                    except Exception:
                        fl.add("dynamicBody")
                elif tn == "MonoBehaviour":
                    try:
                        raw = co.get_raw_data()
                        fid, pid, _, end = mono_header(raw)
                    except Exception:
                        continue
                    cname = None
                    if fid == 0:
                        so = byid.get((ck[0], pid))
                        if so is not None:
                            if pid not in scripts:
                                try:
                                    scripts[pid] = so.read().m_ClassName
                                except Exception:
                                    scripts[pid] = None
                            cname = scripts[pid]
                    script_names[cname or "external"] += 1
                    if cname == "LiveMixin":
                        fl.add("liveMixin")
                    elif cname in CREATURE_SCRIPTS:
                        fl.add("creature")
                    elif cname == "Pickupable":
                        fl.add("pickupable")
                    elif cname in VEHICLE_SCRIPTS:
                        fl.add("vehicle")
                    elif cname == "FreezeRigidbodyWhenFar":
                        fl.add("freeze")
                    elif cname == "WorldForces":
                        fl.add("worldForces")
                    elif cname == "PrefabPlaceholder":
                        try:
                            pc, _ = read_aligned_string(raw, end)
                            if pc:
                                placeholders.append(pc)
                        except Exception:
                            pass
                elif tn in COLLIDERS:
                    try:
                        ctt = co.read_typetree()
                    except Exception:
                        ctt = {}
                    c = {"type": tn, "go": g, "trigger": bool(ctt.get("m_IsTrigger")), "enabled": bool(ctt.get("m_Enabled", 1)),
                         "layer": layers.get(g, 0)}
                    if tn == "MeshCollider":
                        c["convex"] = bool(ctt.get("m_Convex"))
                        mp = ctt.get("m_Mesh", {})
                        ext = None
                        if mp.get("m_FileID") == 0 and mp.get("m_PathID"):
                            mk = (ck[0], mp["m_PathID"])
                            if mk not in mesh_cache:
                                try:
                                    mt = byid[mk].read_typetree()
                                    e = mt.get("m_LocalAABB", {}).get("m_Extent", {})
                                    mesh_cache[mk] = (e.get("x", 0.0), e.get("y", 0.0), e.get("z", 0.0))
                                except Exception:
                                    mesh_cache[mk] = None
                            ext = mesh_cache[mk]
                        c["extent"] = ext
                    colliders.append(c)
            flags_of[g] = fl
        if lightmapped:   # only the main prefab's subtree of a lightmapped scene
            roots = [g for g in gos if names.get(g) == LIGHTMAPPED_ROOT and parents.get(g) is None]

            def under(g):
                k = 0
                while g is not None and k < 256:
                    if g in roots:
                        return True
                    g = parents.get(g)
                    k += 1
                return False
            colliders = [c for c in colliders if under(c["go"])]
        res = {"bundle": bundle, "classes": collections.Counter(), "kinds": collections.Counter(), "colliders": collections.Counter(),
               "mesh": 0, "meshDefaultSolid": 0, "nonDefault": 0, "nonDefaultLayers": set(), "largeMesh": [], "rigidbody": "none",
               "liveMixin": False, "placeholders": placeholders, "scripts": dict(script_names.most_common(12))}
        for c in colliders:
            chain = chain_flags(c["go"], parents, flags_of)
            verdict = classify_collider(c, chain, self.mask)
            res["classes"][verdict] += 1
            res["colliders"]["%s/%s/%s" % (c["type"], LAYERS.get(c["layer"], c["layer"]), "trigger" if c["trigger"] else "solid")] += 1
            if c["layer"] != 0:
                res["nonDefault"] += 1
                res["nonDefaultLayers"].add(str(LAYERS.get(c["layer"], c["layer"])))
            if c["type"] == "MeshCollider":
                res["mesh"] += 1
                if c["layer"] == 0 and not c["trigger"]:
                    res["meshDefaultSolid"] += 1
                    if c.get("extent") and max(c["extent"]) > 20.0:
                        res["largeMesh"].append([names.get(c["go"], "?"), [round(x, 2) for x in c["extent"]], c.get("convex")])
            if verdict in ("ok", "needsAllow", "needsWorldForces"):
                res["kinds"][PRIMITIVE_KIND[c["type"]]] += 1
            if "dynamicBody" in chain:
                res["rigidbody"] = "dynamic"
            elif "kinematicBody" in chain and res["rigidbody"] == "none":
                res["rigidbody"] = "kinematic"
            if "liveMixin" in chain:
                res["liveMixin"] = True
        return res

    def run(self, out, limit=None, limit_files=None):
        t0 = time.time()
        refs, stats = referenced_classes(self.paths, limit_files)
        self.log("referenced class ids: %d (%d batch-objects files, %d cells files, %d unreadable)" % (
            len(refs), stats["batchObjectsFiles"], stats["cellsFiles"], len(stats["errors"])))
        queue = collections.deque((cid, None) for cid in sorted(refs))
        for f in sorted(os.listdir(self.paths["bundles"])):   # lightmapped prefab scenes (the Aurora)
            if ".unity_" in f and f.endswith(".bundle"):
                queue.append(("scene:" + f.split(".unity_")[0].lower(), "lightmapped scene"))
        classes, done = {}, set()
        n = 0
        while queue:
            cid, via = queue.popleft()
            if cid in done:
                continue
            done.add(cid)
            if limit and n >= limit and via != "lightmapped scene":
                continue
            n += 1
            entry = {"via": via, "instances": refs[cid]["instances"] if cid in refs else 0,
                     "batches": len(refs[cid]["batches"]) if cid in refs else 0,
                     "source": sorted(refs[cid]["source"]) if cid in refs else (["scene"] if cid.startswith("scene:") else ["placeholder"])}
            try:
                if cid.startswith("scene:"):
                    name = cid[6:]
                    bundle = next((f for f in os.listdir(self.paths["bundles"]) if f.lower().startswith(name + ".unity_")), None)
                    env_check = self.UnityPy.load(os.path.join(self.paths["bundles"], bundle))
                    roots = [o for o in env_check.objects if o.type.name == "GameObject" and o.read_typetree().get("m_Name") == LIGHTMAPPED_ROOT]
                    if not roots:
                        continue   # not a lightmapped prefab scene (menus, credits): not a world class
                    entry["file"] = bundle
                    res = self.analyze(bundle, lightmapped=True)
                else:
                    fn = self.prefabs.get(cid)
                    entry["file"] = fn
                    if not fn:
                        raise LookupError("not in prefabs.db")
                    bundle = self.bundle_for(fn)
                    if not bundle:
                        raise LookupError("no bundle for " + os.path.basename(fn))
                    res = self.analyze(bundle)
                for pc in res["placeholders"]:
                    if pc not in done:
                        queue.append((pc, cid))
                entry.update({"bundle": res["bundle"], "colliders": dict(res["colliders"]), "kinds": dict(res["kinds"]),
                              "verdicts": dict(res["classes"]), "mesh": res["mesh"], "meshDefaultSolid": res["meshDefaultSolid"],
                              "nonDefault": res["nonDefault"], "nonDefaultLayers": sorted(res["nonDefaultLayers"]),
                              "largeMeshObbs": res["largeMesh"], "rigidbody": res["rigidbody"], "liveMixin": res["liveMixin"],
                              "placeholders": len(res["placeholders"]), "scripts": res["scripts"]})
                status, reason, allow = decide({"classes": res["classes"], "kinds": res["kinds"], "nonDefaultLayers": res["nonDefaultLayers"]})
            except Exception as e:
                status, reason, allow = "skip", "%s: %s" % (type(e).__name__, e), False
            entry["status"], entry["reason"], entry["allowListed"] = status, reason, allow
            entry["allowWorldForces"] = status == "handled" and waives_world_forces({"classes": collections.Counter(entry.get("verdicts") or {})})
            classes[cid] = entry
            if n % 50 == 0:
                self.log("  %d classes (%.0f s)" % (n, time.time() - t0))
        summary = {
            "referenced": len(refs), "listed": len(classes),
            "handled": sum(1 for e in classes.values() if e["status"] == "handled"),
            "skipped": sum(1 for e in classes.values() if e["status"] == "skip"),
            "meshColliders": sum(e.get("mesh", 0) for e in classes.values()),
            "meshDefaultSolid": sum(e.get("meshDefaultSolid", 0) for e in classes.values()),
            "nonDefaultColliders": sum(e.get("nonDefault", 0) for e in classes.values()),
            "largeMeshObbs": sum(len(e.get("largeMeshObbs", [])) for e in classes.values()),
            "allowWorldForces": sum(1 for e in classes.values() if e.get("allowWorldForces")),
            "layerMask": "0x%08x" % self.mask, "layerMaskSource": self.mask_source,
            "unreadableFiles": stats["errors"][:20], "seconds": round(time.time() - t0, 1), "limited": bool(limit or limit_files),
        }
        build = None
        try:
            with open(self.paths["build"], "r", encoding="utf-8") as fh:
                build = fh.read().strip()
        except OSError:
            pass
        doc = {"version": ALLOWLIST_VERSION, "generator": "tools/sn-octrees/sn_prefab_colliders.py --census",
               "generatedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "subnauticaBuild": build,
               "note": "derived from the local Subnautica install; never shipped (ARCHITECTURE: census in %LOCALAPPDATA%)",
               "allow": sorted(cid for cid, e in classes.items() if e["allowListed"]),
               "allowWorldForces": sorted(cid for cid, e in classes.items() if e.get("allowWorldForces")),
               "handled": sorted(cid for cid, e in classes.items() if e["status"] == "handled"),
               "skip": {cid: e["reason"] for cid, e in sorted(classes.items()) if e["status"] == "skip"},
               "summary": summary, "classes": classes}
        write_doc(out, doc)
        ok, lines = n7_check(set(refs) if not limit else set(c for c in refs if c in classes), doc)
        for ln in lines:
            self.log(ln)
        self.log("wrote %s (%.0f s)" % (out, time.time() - t0))
        return 0 if ok else 1

def write_doc(out, doc):
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    tmp = out + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, indent=1, sort_keys=False, default=lambda o: sorted(o) if isinstance(o, set) else str(o))
        fh.write("\n")
    os.replace(tmp, out)

def check(path, sn_dir):
    with open(path, "r", encoding="utf-8") as fh:
        doc = json.load(fh)
    refs, stats = referenced_classes(sn_paths(sn_dir))
    if doc.get("summary", {}).get("limited"):
        print("INFO the allow-list was generated with --limit: classes beyond the limit are reported unlisted")
    ok, lines = n7_check(set(refs), doc)
    for ln in lines:
        print(ln)
    return 0 if ok else 1

# ---------------------------------------------------------------- the original counts mode
def counts(patterns, verbose):
    import UnityPy  # noqa: E402
    UnityPy.config.FALLBACK_UNITY_VERSION = "2019.4.36f1"
    A = sn_paths(os.environ.get("DEEPENG_SUBNAUTICA", DEFAULT_SN))["bundles"]
    for pat in patterns.split(","):
        for path in glob.glob(os.path.join(A, pat)):
            env = UnityPy.load(path)
            print("==", os.path.basename(path))
            objs = {o.path_id: o for o in env.objects}
            layer_count = collections.Counter()
            coll = collections.Counter()
            meshcols = []
            for o in env.objects:
                if o.type.name != "GameObject":
                    continue
                tt = o.read_typetree()
                comps = []
                for c in tt["m_Component"]:
                    pid = c["component"]["m_PathID"]
                    co = objs.get(pid)
                    if co is None:
                        continue
                    tn = co.type.name
                    if tn in ("BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider", "Rigidbody", "WheelCollider", "TerrainCollider"):
                        ctt = co.read_typetree()
                        if tn == "Rigidbody":
                            comps.append("Rigidbody(kin=%s)" % ctt.get("m_IsKinematic"))
                        else:
                            trig = ctt.get("m_IsTrigger")
                            en = ctt.get("m_Enabled")
                            extra = ""
                            if tn == "MeshCollider":
                                extra = ",convex=%s" % ctt.get("m_Convex")
                                meshcols.append(ctt["m_Mesh"]["m_PathID"])
                            comps.append("%s(trig=%s,en=%s%s)" % (tn, trig, en, extra))
                            coll[(tn, bool(trig), LAYERS.get(tt["m_Layer"], tt["m_Layer"]))] += 1
                if comps:
                    layer_count[LAYERS.get(tt["m_Layer"], tt["m_Layer"])] += 1
                    if verbose:
                        print("   GO", tt["m_Name"], "layer", LAYERS.get(tt["m_Layer"], tt["m_Layer"]), comps)
            print("  GameObjects with colliders/rigidbody by layer:", dict(layer_count))
            print("  colliders (type, trigger, layer):", dict(coll))
            rd = collections.Counter()
            for mp in meshcols:
                mo = objs.get(mp)
                if mo is None:
                    rd["external"] += 1
                    continue
                try:
                    mt = mo.read_typetree()
                    rd["readable=%s" % mt.get("m_IsReadable")] += 1
                except Exception:
                    rd["err"] += 1
            print("  MeshCollider meshes:", dict(rd))

# ---------------------------------------------------------------- selftest (no game, no UnityPy)
def _pb_varint(v):
    out = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)

def _pb_field(f, v):
    if isinstance(v, int):
        return _pb_varint(f << 3) + _pb_varint(v)
    v = v.encode() if isinstance(v, str) else v
    return _pb_varint((f << 3) | 2) + _pb_varint(len(v)) + v

def _pb_lp(msg):
    return _pb_varint(len(msg)) + msg

def _tree(gos, header=True):
    out = bytearray()
    if header:
        out += _pb_lp(_pb_field(1, 1369164567) + _pb_field(2, 4))
    out += _pb_lp(_pb_field(1, len(gos)))
    for cid, types in gos:
        out += _pb_lp(_pb_field(2, 1) + (_pb_field(7, cid) if cid else b"") + _pb_field(6, "id-" + (cid or "x")))
        out += _pb_lp(_pb_field(1, len(types)) if types else b"")
        for t in types:
            out += _pb_lp(_pb_field(1, t) + _pb_field(2, 1))
            out += _pb_lp(_pb_field(1, 42))
    return bytes(out)

def selftest():
    fails = []
    checks = [0]

    def ok(cond, what):
        checks[0] += 1
        if not cond:
            fails.append(what)

    # object trees and the two cache formats
    tree = _tree([("root-class", ["UnityEngine.Transform", "LargeWorldBatchRoot"]), ("", ["UnityEngine.Transform"]),
                  ("wreck-1", ["UnityEngine.Transform", "PrefabIdentifier"])])
    objs = parse_batch_objects(tree)
    ok([o[0] for o in objs] == ["root-class", "", "wreck-1"], "batch objects: class ids")
    ok(objs[0][1] == ["UnityEngine.Transform", "LargeWorldBatchRoot"], "batch objects: component types")
    try:
        parse_batch_objects(tree + b"\0")
        fails.append("trailing bytes accepted")
    except ValueError:
        pass
    data = _tree([("tree-a", ["T"])])
    legacy = _tree([("tree-b", [])])
    waiter = _tree([("tree-c", ["T"])], header=False) + _tree([("tree-d", [])], header=False)
    cells = _pb_lp(_pb_field(1, 10) + _pb_field(2, 2))
    cells += _pb_lp(_pb_field(1, _pb_field(1, 0) + _pb_field(2, 0) + _pb_field(3, 5)) + _pb_field(2, 0) + _pb_field(3, len(data)) + _pb_field(4, len(legacy)) + _pb_field(5, len(waiter)))
    cells += data + legacy + waiter
    cells += _pb_lp(_pb_field(1, _pb_field(1, 1)) + _pb_field(2, 1))   # an empty cell
    ok([o[0] for o in parse_cells(cells)] == ["tree-a", "tree-b", "tree-c", "tree-d"], "cells: data, legacy, waiter trees")
    # prefabs.db
    db = struct.pack("<i", 2) + _pb_varint(3) + b"abc" + _pb_varint(15) + b"WE/x/Wreck.pfab" + _pb_varint(1) + b"z" + _pb_varint(4) + b"a.pf"
    ok(read_prefabs_db(db) == {"abc": "WE/x/Wreck.pfab", "z": "a.pf"}, "prefabs.db strings")
    # the MonoBehaviour head and a PrefabPlaceholder's class id
    name = b"Placeholder"
    raw = struct.pack("<iq", 0, 7) + struct.pack("<B3x", 1) + struct.pack("<iq", 0, 99) + struct.pack("<i", len(name)) + name + b"\0"
    pcid = b"8e1d3c1c-0000"
    raw += struct.pack("<i", len(pcid)) + pcid + b"\0\0\0" + struct.pack("<B", 1)
    fid, pid, nm, end = mono_header(raw)
    ok((fid, pid, nm) == (0, 99, "Placeholder") and end % 4 == 0, "MonoBehaviour head")
    ok(read_aligned_string(raw, end)[0] == "8e1d3c1c-0000", "placeholder class id")
    # parents and the collider rules (the runtime filter's order)
    parents = {"child": "mid", "mid": "root", "root": None}
    flags = {"root": {"kinematicBody"}, "mid": set(), "child": set()}
    ok(chain_flags("child", parents, flags) == {"kinematicBody"}, "chain flags walk up")
    solid = {"type": "BoxCollider", "trigger": False, "enabled": True, "layer": 0}
    ok(classify_collider(solid, set()) == "ok", "a wreck box")
    ok(classify_collider(dict(solid, trigger=True), set()) == "trigger", "trigger")
    ok(classify_collider(dict(solid, layer=17), set()) == "layer", "layer")
    ok(classify_collider(dict(solid, enabled=False), set()) == "disabled", "disabled")
    ok(classify_collider(solid, {"kinematicBody"}) == "needsAllow", "kinematic: needs the allow-list")
    ok(classify_collider(solid, {"liveMixin"}) == "needsAllow", "LiveMixin: needs the allow-list")
    ok(classify_collider(solid, {"creature", "kinematicBody", "freeze"}) == "blocked:creature", "a frozen reaper is never static")
    ok(classify_collider(solid, {"dynamicBody"}) == "blocked:dynamic rigidbody", "dynamic body")
    # review F3: WorldForces on a kinematic body (the explorable wrecks) waived for the class; dynamic or a frozen reaper not
    ok(classify_collider(solid, {"worldForces", "kinematicBody"}) == "needsWorldForces", "F3: kinematic wreck with WorldForces")
    ok(classify_collider(solid, {"worldForces", "dynamicBody"}) == "blocked:worldForces", "F3: WorldForces on a dynamic body")
    ok(classify_collider(solid, {"worldForces", "kinematicBody", "creature", "freeze"}) == "blocked:creature", "F3: the reaper stays out")
    st, why, allow = decide({"classes": collections.Counter(needsWorldForces=1386, ok=5), "kinds": collections.Counter(box=1391), "nonDefaultLayers": set()})
    ok(st == "handled" and allow and "WorldForces waived" in why, "F3: the wreck class handled, allow-listed")
    ok(waives_world_forces({"classes": collections.Counter(needsWorldForces=3)}), "F3: listed in allowWorldForces")
    st, why, allow = decide({"classes": collections.Counter({"needsWorldForces": 3, "blocked:creature": 1}), "kinds": collections.Counter(),
                             "nonDefaultLayers": set()})
    ok(st == "skip" and "worldForces" in why and "creature" in why, "F3: a class with a creature is never waived")

    matrix = [0xFFFFFFFF] * 32
    matrix[19] = 0xFFFFFFFF & ~(1 << 9)
    m = mask_from_matrix(matrix)
    ok(m == default_mask(), "F5: the mask from the matrix equals the measured fallback")
    ok((m >> 26) & 1 and not (m >> 9) & 1 and not (m >> 21) & 1 and not (m >> 30) & 1 and m & 1, "F5: DiveReel in; OnlyVehicle, Trigger, terrain out")
    ok(classify_collider(dict(solid, layer=26), set()) == "layer" and classify_collider(dict(solid, layer=26), set(), m) == "ok",
       "F5: a DiveReel wreck wall handled with the mask")
    ok(classify_collider(dict(solid, layer=30), set(), m) == "layer", "F5: terrain colliders stay with the S4 octrees")
    ok(classify_collider(dict(solid, type="TerrainCollider"), set()).startswith("blocked:"), "terrain collider type")
    # decisions
    st, why, allow = decide({"classes": collections.Counter(ok=3, needsAllow=1), "kinds": collections.Counter(box=4), "nonDefaultLayers": set()})
    ok(st == "handled" and allow, "handled + allow-listed")
    st, why, allow = decide({"classes": collections.Counter(ok=2), "kinds": collections.Counter(capsule=2), "nonDefaultLayers": set()})
    ok(st == "handled" and not allow, "handled, no waiver needed")
    st, why, allow = decide({"classes": collections.Counter(trigger=5), "kinds": collections.Counter(), "nonDefaultLayers": set()})
    ok(st == "skip" and "triggers" in why, "triggers only: skipped with a reason")
    st, why, allow = decide({"classes": collections.Counter(layer=2), "kinds": collections.Counter(), "nonDefaultLayers": {"Vehicle"}})
    ok(st == "skip" and "Vehicle" in why, "non-Default only: skipped, the layer named")
    st, why, allow = decide({"classes": collections.Counter({"blocked:creature": 1}), "kinds": collections.Counter(), "nonDefaultLayers": set()})
    ok(st == "skip" and "creature" in why, "creature: skipped")
    ok(decide({"classes": collections.Counter(), "kinds": collections.Counter(), "nonDefaultLayers": set()})[1] == "no colliders", "no colliders")
    # S7.N7
    doc = {"classes": {"a": {"status": "handled", "reason": "x"}, "b": {"status": "skip", "reason": "triggers only"}},
           "allow": [], "summary": {"handled": 1, "skipped": 1}}
    ok(n7_check({"a", "b"}, doc)[0], "N7 pass")
    ok(not n7_check({"a", "b", "c"}, doc)[0], "N7 fails on an unlisted class")
    doc["classes"]["b"]["reason"] = ""
    ok(not n7_check({"a", "b"}, doc)[0], "N7 fails on a skip without a reason")
    # the JSON round trip (sets serialised as sorted lists)
    import tempfile
    p = os.path.join(tempfile.mkdtemp(prefix="s7census"), "allowlist.json")
    write_doc(p, {"version": 1, "allow": ["b", "a"], "x": {"s"}})
    with open(p, "r", encoding="utf-8") as fh:
        back = json.load(fh)
    ok(back["allow"] == ["b", "a"] and back["x"] == ["s"], "allow-list JSON round trip")
    ok(default_out().endswith(os.path.join("DeepEngineers", "census", "allowlist.json")), "default output in %LOCALAPPDATA%")
    for f in fails:
        print("FAIL", f)
    if fails:
        print("sn_prefab_colliders selftest: %d failure(s)" % len(fails))
        return 1
    print("PASS sn_prefab_colliders selftest (%d checks)" % checks[0])
    return 0

def main(argv):
    def opt(name, default=None):
        if name in argv:
            i = argv.index(name)
            return argv[i + 1] if i + 1 < len(argv) and not argv[i + 1].startswith("--") else default
        return default
    sn_dir = opt("--subnautica", os.environ.get("DEEPENG_SUBNAUTICA", DEFAULT_SN))
    if "--selftest" in argv:
        return selftest()
    if "--census" in argv:
        out = opt("--out", default_out())
        limit = opt("--limit")
        limit_files = opt("--limit-files")
        return Census(sn_dir).run(out, int(limit) if limit else None, int(limit_files) if limit_files else None)
    if "--check" in argv:
        return check(opt("--check", default_out()), sn_dir)
    if not argv or argv[0].startswith("--"):
        print(__doc__)
        return 2
    counts(argv[0], len(argv) > 1 and argv[1] == "v")
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
