"""WM-1 in-game subsets (docs/ROADMAP.md 5.9, S0.G1-E4a; docs/CONTRACT.md 2.9).

Takes the contract-site WM-1 records (tools/worldmap/wire_ref.py, site SITE_V1, N = 10000, the same records the
full vector file holds) and writes, deterministically:

 tests/wm1/subset-se.jsonl header + 50 'pose' records (the SE plugin recomputes P*, o, q, s at Init)
 tests/wm1/subset-sn.jsonl header + 50 'local' + 20 'box' records (10 grid-aligned, 10 rotated); each record
 carries the (o, q, s) of the pose it references inline ("pose_o", "pose_q", "pose_s"),
 which is what the Subnautica plugin receives on the wire
 src/DeepEngineers.Wire/Wm1Subset.cs the same numbers as C# double arrays (shortest round-trip literals) with
 the SHA-256 of their little-endian IEEE-754 bytes, checked by SelfTest at load

Usage: python -I tests/wm1/make_subset.py [--check]
 --check regenerate in memory and compare with the files on disk (exit 1 when they differ)
"""
import hashlib
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tools", "worldmap"))  # python -I does not add any script directory
import wire_ref  # noqa: E402

SITE = "v1"
N = 10000
N_SE_POSE = 50
N_SN_LOCAL = 50
N_SN_BOX_ALIGNED = 10
N_SN_BOX_ROTATED = 10

SE_STRIDE = 32     # matrix rows 12, box_min 3, box_max 3, camera 3, pivot 3, o 3, q 4, s 1
LOCAL_STRIDE = 14  # o 3, q 4, s 1, local_se 3, sn 3
BOX_STRIDE = 40    # o 3, q 4, s 1, c_sec 3, root_sn 3, b 3, h 3, axes 9, centre_u 3, half_u 3, rot_u 4, rotated 1

OUT_SE = os.path.join(HERE, "subset-se.jsonl")
OUT_SN = os.path.join(HERE, "subset-sn.jsonl")
OUT_CS = os.path.join(ROOT, "src", "DeepEngineers.Wire", "Wm1Subset.cs")

def dumps(rec):
    return json.dumps(rec, separators=(",", ":"))

def build():
    records = wire_ref.vector_records(SITE, N)
    full_text = "".join(dumps(rec) + "\n" for rec in records)
    full_sha = hashlib.sha256(full_text.encode()).hexdigest()
    header = records[0]
    poses = [rec for rec in records if rec["kind"] == "pose"]
    locals_ = [rec for rec in records if rec["kind"] == "local"][:N_SN_LOCAL]
    boxes_a = [rec for rec in records if rec["kind"] == "box" and not rec["rotated"]][:N_SN_BOX_ALIGNED]
    boxes_r = [rec for rec in records if rec["kind"] == "box" and rec["rotated"]][:N_SN_BOX_ROTATED]
    se = poses[:N_SE_POSE]
    sn = []
    for rec in locals_ + boxes_a + boxes_r:
        p = poses[rec["pose"]]
        sn.append(dict(rec, pose_o=p["o"], pose_q=p["q"], pose_s=p["s"]))

    se_vals = []
    for rec in se:
        m = rec["se_matrix"]
        row = m["right"] + m["up"] + m["backward"] + m["translation"] + rec["box_min"] + rec["box_max"] + \
            rec["camera_se"] + rec["pivot_se"] + rec["o"] + rec["q"] + [rec["s"]]
        assert len(row) == SE_STRIDE
        se_vals.extend(row)
    local_vals, box_vals = [], []
    for rec in sn:
        head = rec["pose_o"] + rec["pose_q"] + [rec["pose_s"]]
        if rec["kind"] == "local":
            row = head + rec["local_se"] + rec["sn"]
            assert len(row) == LOCAL_STRIDE
            local_vals.extend(row)
        else:
            row = head + rec["section_centre_se"] + rec["section_root_sn"] + rec["box_centre_se"] + rec["half_extents_se"] + \
                rec["axes_se"][0] + rec["axes_se"][1] + rec["axes_se"][2] + rec["centre_section_local_unity"] + \
                rec["half_extents_unity"] + rec["rotation_section_local_unity"] + [1.0 if rec["rotated"] else 0.0]
            assert len(row) == BOX_STRIDE
            box_vals.extend(row)
    se_vals = [float(v) for v in se_vals]
    local_vals = [float(v) for v in local_vals]
    box_vals = [float(v) for v in box_vals]

    def sha_doubles(vals):
        return hashlib.sha256(struct.pack("<%dd" % len(vals), *vals)).hexdigest()
    se_data_sha = sha_doubles(se_vals)
    sn_data_sha = sha_doubles(local_vals + box_vals)

    sub_header = {"kind": "subset-header", "generator": "tests/wm1/make_subset.py", "site": header["site"],
                  "source": f"tools/worldmap/wire_ref.py vector_records({SITE!r}, {N})", "source_sha256": full_sha,
                  "tolerances": header["tolerances"]}
    se_text = dumps(dict(sub_header, side="se", counts={"pose": len(se)}, data_sha256=se_data_sha)) + "\n" + \
        "".join(dumps(rec) + "\n" for rec in se)
    sn_text = dumps(dict(sub_header, side="sn", counts={"local": len(locals_), "box": len(boxes_a) + len(boxes_r),
                                                         "box_rotated": len(boxes_r)}, data_sha256=sn_data_sha)) + "\n" + \
        "".join(dumps(rec) + "\n" for rec in sn)
    se_file_sha = hashlib.sha256(se_text.encode()).hexdigest()
    sn_file_sha = hashlib.sha256(sn_text.encode()).hexdigest()
    cs_text = csharp(header, full_sha, se_vals, local_vals, box_vals, se_data_sha, sn_data_sha, se_file_sha, sn_file_sha)
    return se_text, sn_text, cs_text, (full_sha, se_data_sha, sn_data_sha, se_file_sha, sn_file_sha, len(se), len(sn))

def cs_array(name, vals, stride):
    lines = []
    for i in range(0, len(vals), stride):
        row = vals[i:i + stride]
        lines.append("            " + ", ".join(repr(v) for v in row) + ",")
    return (f"        internal static readonly double[] {name} =\n        {{\n" + "\n".join(lines) + "\n        };\n")

def csharp(header, full_sha, se_vals, local_vals, box_vals, se_sha, sn_sha, se_file_sha, sn_file_sha):
    site = header["site"]
    tol = header["tolerances"]
    return f"""// <auto-generated>
// Generated by tests/wm1/make_subset.py from tools/worldmap/wire_ref.py (site {site['name']}, N = {N}). Do not edit:
// change the generator or wire_ref.py and run `python -I tests/wm1/make_subset.py`.
// Source WM-1 file sha256 {full_sha}
// </auto-generated>
namespace DeepEngineers.Wire
{{
    public static partial class Wm1Subset
    {{
        public const string SiteName = "{site['name']}";
        public const double SiteRs = {site['Rs']!r};
        public const double SiteCx = {site['C'][0]!r}, SiteCy = {site['C'][1]!r}, SiteCz = {site['C'][2]!r};
        /// <summary>sha256 of the full WM-1 file text these records come from (wire_ref.py vectors, site {site['name']}).</summary>
        public const string SourceSha256 = "{full_sha}";
        /// <summary>sha256 of tests/wm1/subset-se.jsonl and tests/wm1/subset-sn.jsonl.</summary>
        public const string SeFileSha256 = "{se_file_sha}";
        public const string SnFileSha256 = "{sn_file_sha}";
        /// <summary>sha256 of the little-endian IEEE-754 bytes of SePoseData, and of SnLocalData followed by SnBoxData.</summary>
        public const string SeDataSha256 = "{se_sha}";
        public const string SnDataSha256 = "{sn_sha}";

        public const double TolPosition = {tol['pos_double_m']!r};
        public const double TolPositionFloat32 = {tol['pos_float32_m']!r};
        public const double TolDirection = {tol['dir']!r};
        public const double TolRotation = {tol['quat']!r};
        public const double TolScale = {tol['scale']!r};
        public const double TolLocalRoundTrip = {tol['local_roundtrip_m']!r};

        public const int SePoseStride = {SE_STRIDE};
        public const int SnLocalStride = {LOCAL_STRIDE};
        public const int SnBoxStride = {BOX_STRIDE};
        public const int SePoseCount = {len(se_vals) // SE_STRIDE};
        public const int SnLocalCount = {len(local_vals) // LOCAL_STRIDE};
        public const int SnBoxCount = {len(box_vals) // BOX_STRIDE};

        // pose: right[3] up[3] backward[3] translation[3] box_min[3] box_max[3] camera_se[3] pivot_se[3] o[3] q[4] s
{cs_array("SePoseData", se_vals, SE_STRIDE)}
        // local: pose_o[3] pose_q[4] pose_s local_se[3] sn[3]
{cs_array("SnLocalData", local_vals, LOCAL_STRIDE)}
        // box: pose_o[3] pose_q[4] pose_s c_sec[3] root_sn[3] b[3] h[3] e0[3] e1[3] e2[3] centre_u[3] half_u[3] rot_u[4] rotated
{cs_array("SnBoxData", box_vals, BOX_STRIDE)}    }}
}}
"""

def main(argv):
    se_text, sn_text, cs_text, info = build()
    full_sha, se_sha, sn_sha, se_file_sha, sn_file_sha, n_se, n_sn = info
    outputs = ((OUT_SE, se_text), (OUT_SN, sn_text), (OUT_CS, cs_text))
    if "--check" in argv:
        bad = []
        for path, text in outputs:
            try:
                with open(path, encoding="utf-8", newline="") as fh:
                    cur = fh.read()
            except OSError:
                cur = None
            if cur != text:
                bad.append(os.path.relpath(path, ROOT))
        print("make_subset --check:", "OK" if not bad else "DIFFERS: " + ", ".join(bad))
        return 1 if bad else 0
    for path, text in outputs:
        with open(path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
    print(f"source WM-1 ({wire_ref.SITES[SITE]}, N={N}) sha256 {full_sha}")
    print(f"subset-se.jsonl: {n_se} pose records, file sha256 {se_file_sha}, data sha256 {se_sha}")
    print(f"subset-sn.jsonl: {n_sn} records (local + box), file sha256 {sn_file_sha}, data sha256 {sn_sha}")
    print(f"wrote {os.path.relpath(OUT_CS, ROOT)}")
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
