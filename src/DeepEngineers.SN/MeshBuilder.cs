using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // S3 Subnautica side, Unity-free part 2 of 4: the mirrored mesh build pass (CONTRACT §2.3, §2.5; ARCHITECTURE §5.4,
 // §5.8). Runs on the mirror worker thread only; its output is plain arrays the main thread uploads with
 // Mesh.SetVertexBufferParams / SetVertexBufferData / SetIndexBufferData / SetSubMesh.

 /// <summary>
 /// The vertex the plugin uploads (stream 0, 48 B): Position f32×3, Normal f32×3, Tangent f32×4, TexCoord0 f32×2, in
 /// Unity's fixed attribute order. Unity-local space of the entity (= Mirror of the grid's SE-local space).
 /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct MirrorVertex
    {
        public float Px, Py, Pz;
        public float Nx, Ny, Nz;
        public float Tx, Ty, Tz, Tw;
        public float U, V;

        public const int Size = 48;
    }

    /// <summary>
 /// A C3 model converted once to Unity-local conventions (worker thread): positions, normals and tangents mirrored
 /// (<c>(x, y, −z)</c>), tangent handedness w = sign(dot(Cross(n′, t′), b′)) on the mirrored vectors (Unity's own
 /// binormal formula), triangles rewritten (I0, I1, I2) → (I0, I2, I1), and the C3 winding measure: the share of
 /// non-degenerate triangles whose Unity-space geometric normal Cross(p1 − p0, p2 − p0) agrees with the sum of their
 /// three vertex normals. <see cref="RawAgreement"/> is measured on the rewritten order, before any flip: it is the
 /// S3.C3 number (> 99 % expected). Below 1 % the model is flipped automatically (<see cref="Flipped"/>) and logged;
 /// S3.M2 (rewrite dropped) therefore shows a raw value near 0 % with the safety net engaged.
 /// </summary>
    internal sealed class PreparedModel
    {
        public ulong Key;
        public int VertexCount, TriangleCount;
        public float[] Pos, Nrm;   // 3 per vertex, Unity-local
        public float[] Tan;        // 4 per vertex (xyz + w)
        public float[] Uv;         // 2 per vertex, as SE stores them
        public int[] Tris;         // Unity triangle order (after the rewrite and any automatic flip)
 /// <summary>The received SE model-space positions, untouched (3 per vertex): the reference of the S3.C2 drawn-vertex probes.</summary>
        public float[] SePos;
        public SubmeshInfo[] Submeshes;
        public float PatternScale;
        public double RawAgreement;
        public int Degenerate;
        public bool Flipped, Mirrored, Swapped, Skinned;
        /// <summary>S5.M2: the census rule asked for the automatic flip but the mutant skipped it (the measure is still logged).</summary>
        public bool FlipSkipped;
        /// <summary>S5: skinned models only, as received (4 canonical bone indices and weights per vertex; the vertex order is unchanged).</summary>
        public byte[] BoneIdx;
        public float[] Weights;
        public ulong Hash;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        /// <summary>Tangent-frame census (S3.N1t): the share of measured triangles whose stored tangent runs along +dP/du and binormal along +dP/dv.</summary>
        public TangentCensus Tangents;
        /// <summary>S10 round 3 E: <see cref="MeshBuilder.TriangleGroups"/> of this model, computed once (any worker thread;
 /// published whole with Volatile.Write and checked against the submesh array and triangle count it came from).</summary>
        internal MeshBuilder.S10Groups S10TriGroups;

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            return "key=" + DeLog.Hex(Key) + " verts=" + VertexCount + " tris=" + TriangleCount + " submeshes=" + Submeshes.Length +
                   " winding=" + (RawAgreement * 100.0).ToString("0.###", ci) + "% degenerate=" + Degenerate + " flipped=" + (Flipped ? "true" : "false") +
                   (FlipSkipped ? " flipSkipped=true(S5.M2)" : "") +
                   " mirrorZ=" + (Mirrored ? "on" : "OFF(S3.M1)") + " triSwap=" + (Swapped ? "on" : "OFF(S3.M2)") +
                   " patternScale=" + PatternScale.ToString("R", ci) + " skinned=" + (Skinned ? "true" : "false") +
                   " boundsUnity=(" + MinX.ToString("0.###", ci) + "," + MinY.ToString("0.###", ci) + "," + MinZ.ToString("0.###", ci) + ")-(" +
                   MaxX.ToString("0.###", ci) + "," + MaxY.ToString("0.###", ci) + "," + MaxZ.ToString("0.###", ci) + ")" +
                   " " + Tangents.Describe();
        }
    }

    /// <summary>
 /// How a model stores its tangent frame against its UVs: per non-degenerate
 /// triangle (|dP/du|, |dP/dv| and the UV area non-zero), whether the vertex tangents agree in sign with dP/du and the
 /// vertex binormals with dP/dv. The N1 glyph's synthetic cube stores tangent = +u and binormal = +v (DirectX v down);
 /// SE's real armor models logged with the same census tell whether N1's bump verdict carries over to them.
 /// </summary>
    internal struct TangentCensus
    {
        public int Measured, TangentAlongU, BinormalAlongV;

        public double TangentFraction { get { return Measured > 0 ? (double)TangentAlongU / Measured : double.NaN; } }
        public double BinormalFraction { get { return Measured > 0 ? (double)BinormalAlongV / Measured : double.NaN; } }

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            if (Measured == 0) return "tangentU=- binormalV=- tangentTris=0";
            return "tangentU=" + (TangentFraction * 100).ToString("0.###", ci) + "% binormalV=" + (BinormalFraction * 100).ToString("0.###", ci) +
                   "% tangentTris=" + Measured;
        }

        /// <summary>The census over SE model-space arrays (positions, tangents, binormals 3 per vertex; UVs 2; indices).</summary>
        public static TangentCensus Measure(float[] pos, float[] tan, float[] bin, float[] uv, int[] idx)
        {
            var c = new TangentCensus();
            if (pos == null || tan == null || bin == null || uv == null || idx == null) return c;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], d = idx[t + 2];
                double e1x = pos[3 * b] - pos[3 * a], e1y = pos[3 * b + 1] - pos[3 * a + 1], e1z = pos[3 * b + 2] - pos[3 * a + 2];
                double e2x = pos[3 * d] - pos[3 * a], e2y = pos[3 * d + 1] - pos[3 * a + 1], e2z = pos[3 * d + 2] - pos[3 * a + 2];
                double du1 = uv[2 * b] - uv[2 * a], dv1 = uv[2 * b + 1] - uv[2 * a + 1];
                double du2 = uv[2 * d] - uv[2 * a], dv2 = uv[2 * d + 1] - uv[2 * a + 1];
                double det = du1 * dv2 - du2 * dv1;
                if (Math.Abs(det) < 1e-12) continue;
                double r = 1.0 / det;
                // dP/du and dP/dv of the triangle's affine UV parametrisation
                double tx = (e1x * dv2 - e2x * dv1) * r, ty = (e1y * dv2 - e2y * dv1) * r, tz = (e1z * dv2 - e2z * dv1) * r;
                double bx = (e2x * du1 - e1x * du2) * r, by = (e2y * du1 - e1y * du2) * r, bz = (e2z * du1 - e1z * du2) * r;
                double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz), bl = Math.Sqrt(bx * bx + by * by + bz * bz);
                if (tl < 1e-12 || bl < 1e-12) continue;
                double st = 0, sb = 0;
                foreach (int v in new[] { a, b, d })
                {
                    st += tan[3 * v] * tx + tan[3 * v + 1] * ty + tan[3 * v + 2] * tz;
                    sb += bin[3 * v] * bx + bin[3 * v + 1] * by + bin[3 * v + 2] * bz;
                }
                if (Math.Abs(st) < 1e-9 * tl && Math.Abs(sb) < 1e-9 * bl) continue; // no stored frame (hasTangents clear)
                c.Measured++;
                if (st > 0) c.TangentAlongU++;
                if (sb > 0) c.BinormalAlongV++;
            }
            return c;
        }
    }

    /// <summary>How chunk UVs are derived from the model UV and the part's uvOffset.</summary>
    internal enum UvRule
    {
        /// <summary>uv / patternScale + uvOffset (SE's own grid exporter, MyCubeGrid export; ROADMAP S3 "UV = uv / PatternScale + offset").</summary>
        Exporter = 0,
        /// <summary>uv + uvOffset (SE's cube-instance vertex shader adds offset/pattern to the unscaled uv; uvOffset then carries offset/pattern).</summary>
        Renderer = 1,
    }

    /// <summary>Options captured when a build job is created (the worker never reads mutable shared state).</summary>
    internal sealed class BuildOptions
    {
        public UvRule Uv = UvRule.Exporter;
        /// <summary>v′ = 1 − v (the glyph test S3.N1 decides; raw DDS rows keep SE's top-origin V, so off by default).</summary>
        public bool VFlip;
        /// <summary>False under S3.M1 (Z mirror dropped).</summary>
        public bool MirrorZ = true;
        /// <summary>False under S3.M2 (triangle swap dropped).</summary>
        public bool SwapTris = true;

        public BuildOptions Clone() { return (BuildOptions)MemberwiseClone(); }

        public string Describe()
        {
            return "uv=" + (Uv == UvRule.Exporter ? "exporter" : "renderer") + " vFlip=" + (VFlip ? "on" : "off") +
                   " mirrorZ=" + (MirrorZ ? "on" : "OFF") + " triSwap=" + (SwapTris ? "on" : "OFF");
        }
    }

    /// <summary>One Unity mesh: a grid's cell × material × colour (ARCHITECTURE §5.8 "Chunked combined meshes").</summary>
    internal struct ChunkKey : IEquatable<ChunkKey>
    {
        public readonly ulong GridId;
        public readonly CellKey Cell;
        public readonly ulong MaterialKey;
        public readonly long ColourKey;
        /// <summary>S5 (CONTRACT §5.4 "Chunk key"): the parts' flags &amp; (NoShadows | MetalnessColorable); 0 for S3 parts.</summary>
        public readonly uint Bits;

        public ChunkKey(ulong gridId, CellKey cell, ulong materialKey, long colourKey, uint bits = 0)
        {
            GridId = gridId; Cell = cell; MaterialKey = materialKey; ColourKey = colourKey; Bits = bits;
        }

        public bool NoShadows { get { return (Bits & S5Conv.NoShadows) != 0; } }

        public bool Equals(ChunkKey o) { return GridId == o.GridId && Cell.Equals(o.Cell) && MaterialKey == o.MaterialKey && ColourKey == o.ColourKey && Bits == o.Bits; }
        public override bool Equals(object obj) { return obj is ChunkKey && Equals((ChunkKey)obj); }
        public override int GetHashCode() { return GridId.GetHashCode() ^ (Cell.GetHashCode() * 31) ^ (MaterialKey.GetHashCode() * 17) ^ ColourKey.GetHashCode() ^ (int)(Bits * 2654435761u); }
        public override string ToString()
        {
            return "chunk." + Cell + ".m" + MaterialKey.ToString("x", CultureInfo.InvariantCulture) + ".c" + ColourKey.ToString("x", CultureInfo.InvariantCulture) +
                   (Bits != 0 ? ".b" + Bits.ToString("x", CultureInfo.InvariantCulture) : "");
        }
    }

    /// <summary>
 /// One S3.C2 probe of a chunk: the drawn vertex whose SE-local position is extreme along
 /// one of the 8 corner directions (corner i: bit 0 +x, bit 1 +y, bit 2 +z). <see cref="SeLocal"/> is that vertex's
 /// SE-local position computed independently of the mesh build pass (the received model position through the part's rows
 /// in doubles, no mirror); <see cref="Drawn"/> is the position actually uploaded for that vertex (Unity-local). The
 /// plugin maps <see cref="Drawn"/> through the renderer's localToWorldMatrix and Python compares it with Apply(o, q, s)
 /// of <see cref="SeLocal"/> recomputed from SE's matrix and P*: a dropped mirror (S3.M1) or a wrong part composition
 /// moves the drawn vertex even when the box is symmetric.
 /// </summary>
    internal struct PlacementProbe
    {
        public bool Valid;
        public Vec3 SeLocal;
        public Vec3 Drawn;
        public int Vertex;
        public double Score;
    }

    /// <summary>A built chunk: vertex and index arrays ready for upload, bounds, counts and the per-chunk winding share.</summary>
    internal sealed class ChunkMeshData
    {
        public ChunkKey Key;
        public MirrorVertex[] Vertices;
        public int VertexCount;
        /// <summary>16-bit indices when VertexCount ≤ 65,535, else <see cref="Indices32"/>.</summary>
        public ushort[] Indices16;
        public int[] Indices32;
        public int IndexCount;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public int Parts;
        public double Agreement;
        public int Degenerate;
        public float H, S, V;
        /// <summary>The 8 S3.C2 probes of this chunk (index = corner).</summary>
        public readonly PlacementProbe[] Probes = new PlacementProbe[8];

        public bool Use32 { get { return Indices32 != null; } }
        public int TriangleCount { get { return IndexCount / 3; } }
    }

    /// <summary>One part of a cell build (an immutable copy of the C4 part and its prepared model).</summary>
    internal struct PartBuild
    {
        public PartRecord Part;
        public PreparedModel Model;
    }

    /// <summary>Everything a cell build needs, copied on the main thread.</summary>
    internal sealed class CellBuildInput
    {
        public ulong GridId;
        public CellKey Cell;
        public int Version;
        public BuildOptions Options;
        public readonly List<PartBuild> Parts = new List<PartBuild>();
        /// <summary>S7t-sn-altitude (T3): an impostor's cell: every triangle goes to one chunk per colour with the flat impostor
 /// material (<see cref="ImpostorRules.MaterialKey"/>) and no shadows (the parts were filtered by the caller).</summary>
        public bool Impostor;
    }

    /// <summary>The result of a cell build: every chunk of the cell (possibly none: the cell became empty).</summary>
    internal sealed class CellBuildResult
    {
        public ulong GridId;
        public CellKey Cell;
        public int Version;
        public readonly List<ChunkMeshData> Chunks = new List<ChunkMeshData>();
        public int Vertices, Triangles, Parts, ModelVertices, ModelTriangles;
        /// <summary>Triangles outside every submesh range (drawn with material 0) and triangles named again by an overlapping range (drawn once).</summary>
        public int UncoveredTriangles, OverlapTriangles;
        public double MinAgreement = 1.0;
        public string Error;
    }

    /// <summary>The mirrored mesh build pass (worker thread; Unity-free).</summary>
    internal static class MeshBuilder
    {
        /// <summary>CONTRACT §2.5: a model with &lt; 1 % agreement in Unity space is flipped automatically and logged.</summary>
        public const double FlipThreshold = 0.01;
        // Degenerate triangles: Wire's MeshConventions.ClassifyTriangle (the census rule of CONTRACT §2.5: |g| < 1e-9,
 // |Σn| < 1e-6 or |cos| < 0.05), shared with the SE side's tools and wire_ref.py.

 /// <summary>
 /// Converts a parsed model to Unity-local conventions and measures its winding (see <see cref="PreparedModel"/>).
 /// <paramref name="allowFlip"/> false is S5.M2: the per-model flip is skipped (recorded in <see cref="PreparedModel.FlipSkipped"/>).
 /// </summary>
        public static PreparedModel Prepare(ModelData m, bool mirrorZ, bool swapTris, bool allowFlip = true)
        {
            int v = m.VertexCount;
            var r = new PreparedModel
            {
                Key = m.Key, VertexCount = v, TriangleCount = m.TriangleCount, Submeshes = m.Submeshes, PatternScale = m.PatternScale,
                Mirrored = mirrorZ, Swapped = swapTris, Skinned = m.Skinned, Hash = m.PayloadHash,
                Pos = new float[v * 3], Nrm = new float[v * 3], Tan = new float[v * 4], Uv = (float[])m.Uv.Clone(), Tris = new int[m.IndexCount],
                SePos = m.Pos, BoneIdx = m.BoneIdx, Weights = m.Weights,
                Tangents = TangentCensus.Measure(m.Pos, m.Tan, m.Bin, m.Uv, m.Indices),
            };
            float zs = mirrorZ ? -1f : 1f;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < v; i++)
            {
                int o = i * 3;
                float px = m.Pos[o], py = m.Pos[o + 1], pz = m.Pos[o + 2] * zs;
                r.Pos[o] = px; r.Pos[o + 1] = py; r.Pos[o + 2] = pz;
                if (px < minX) minX = px; if (py < minY) minY = py; if (pz < minZ) minZ = pz;
                if (px > maxX) maxX = px; if (py > maxY) maxY = py; if (pz > maxZ) maxZ = pz;
                double nx = m.Nrm[o], ny = m.Nrm[o + 1], nz = m.Nrm[o + 2] * zs;
                Normalise(ref nx, ref ny, ref nz);
                r.Nrm[o] = (float)nx; r.Nrm[o + 1] = (float)ny; r.Nrm[o + 2] = (float)nz;
                double tx = m.Tan[o], ty = m.Tan[o + 1], tz = m.Tan[o + 2] * zs;
                Normalise(ref tx, ref ty, ref tz);
                double bx = m.Bin[o], by = m.Bin[o + 1], bz = m.Bin[o + 2] * zs;
                int t4 = i * 4;
                r.Tan[t4] = (float)tx; r.Tan[t4 + 1] = (float)ty; r.Tan[t4 + 2] = (float)tz;
                r.Tan[t4 + 3] = TangentW(nx, ny, nz, tx, ty, tz, bx, by, bz);
            }
            r.MinX = minX; r.MinY = minY; r.MinZ = minZ; r.MaxX = maxX; r.MaxY = maxY; r.MaxZ = maxZ;
            RewriteTriangles(m.Indices, r.Tris, swapTris);
            int degenerate;
            r.RawAgreement = Agreement(r.Pos, r.Nrm, r.Tris, 0, r.TriangleCount, out degenerate);
            r.Degenerate = degenerate;
            if (r.RawAgreement < FlipThreshold && degenerate < r.TriangleCount)
            {
                if (allowFlip)
                {
                    RewriteTriangles(r.Tris, r.Tris, true);
                    r.Flipped = true;
                }
                else r.FlipSkipped = true; // S5.M2
            }
            return r;
        }

        /// <summary>
 /// CONTRACT §2.3 MirrorTri over a whole list: (I0, I1, I2) → (I0, I2, I1) when <paramref name="swap"/>, else a copy
 /// (S3.M2). <paramref name="dst"/> may be <paramref name="src"/>.
 /// </summary>
        public static void RewriteTriangles(int[] src, int[] dst, bool swap)
        {
            if (src.Length % 3 != 0 || dst.Length < src.Length) throw new ArgumentException("index lists must hold whole triangles");
            for (int i = 0; i < src.Length; i += 3)
            {
                int a = src[i], b = src[i + 1], c = src[i + 2];
                dst[i] = a;
                dst[i + 1] = swap ? c : b;
                dst[i + 2] = swap ? b : c;
            }
        }

        /// <summary>
 /// Share of non-degenerate triangles (Unity order) whose geometric normal Cross(p1 − p0, p2 − p0) has a positive
 /// dot product with n0 + n1 + n2 (Unity front faces are clockwise seen from the front: this cross product points
 /// at the viewer). 1 when every triangle is degenerate. Wire's <see cref="MeshConventions.MeasureWinding(float[], float[], int[], int, int)"/>
 /// (one rule on both sides: CONTRACT §2.5).
 /// </summary>
        public static double Agreement(float[] pos, float[] nrm, int[] tris, int triStart, int triCount, out int degenerate)
        {
            WindingStats w = MeshConventions.MeasureWinding(pos, nrm, tris, triStart, triCount);
            degenerate = w.Degenerate;
            return w.Fraction;
        }

        /// <summary>The same measure over a built chunk (positions and normals interleaved in the vertex array).</summary>
        public static double Agreement(MirrorVertex[] v, ushort[] i16, int[] i32, int indexCount, out int degenerate)
        {
            degenerate = 0;
            long agree = 0;
            int tris = indexCount / 3;
            for (int t = 0; t < tris; t++)
            {
                int ia = i32 != null ? i32[t * 3] : i16[t * 3];
                int ib = i32 != null ? i32[t * 3 + 1] : i16[t * 3 + 1];
                int ic = i32 != null ? i32[t * 3 + 2] : i16[t * 3 + 2];
                MirrorVertex a = v[ia], b = v[ib], c = v[ic];
                int k = MeshConventions.ClassifyTriangle(new Vec3(a.Px, a.Py, a.Pz), new Vec3(b.Px, b.Py, b.Pz), new Vec3(c.Px, c.Py, c.Pz),
                                                         new Vec3(a.Nx, a.Ny, a.Nz), new Vec3(b.Nx, b.Ny, b.Nz), new Vec3(c.Nx, c.Ny, c.Nz));
                if (k == 0) { degenerate++; continue; }
                if (k > 0) agree++;
            }
            int n = tris - degenerate;
            return n <= 0 ? 1.0 : (double)agree / n;
        }

        /// <summary>
 /// CONTRACT §2.5 tangent handedness: w = sign(dot(Cross(n′, t′), b′)) on the mirrored vectors, so that Unity's
 /// binormal Cross(normal, tangent.xyz)·w equals the mirrored SE binormal's direction; +1 when undefined.
 /// </summary>
        public static float TangentW(double nx, double ny, double nz, double tx, double ty, double tz, double bx, double by, double bz)
        {
            double cx = ny * tz - nz * ty, cy = nz * tx - nx * tz, cz = nx * ty - ny * tx;
            double d = cx * bx + cy * by + cz * bz;
            return d < 0 ? -1f : 1f;
        }

        private static void Normalise(ref double x, ref double y, ref double z)
        {
            double n = Math.Sqrt(x * x + y * y + z * z);
            if (n > 1e-12) { x /= n; y /= n; z /= n; }
        }

        /// <summary>
 /// A part matrix (SE-local rows Right, Up, Backward, Translation) in the space the prepared vertices live in: with
 /// the mirror, M·A·M and M·T (M = diag(1, 1, −1)), i.e. compose in SE space then mirror once; without it (S3.M1),
 /// A and T. Output: column-major 3×3 <paramref name="a"/> (a[col*3 + row]) and <paramref name="t"/>; returns det(A).
 /// </summary>
        public static double PartTransform(float[] local, bool mirrorZ, double[] a, double[] t)
        {
            // columns of A are the rows Right, Up, Backward of the SE matrix: p = v.x*R + v.y*U + v.z*B + T
            for (int col = 0; col < 3; col++)
            {
                for (int row = 0; row < 3; row++)
                {
                    double x = local[col * 3 + row];
                    if (mirrorZ && (row == 2) != (col == 2)) x = -x; // s_row * s_col with s = (1, 1, -1)
                    a[col * 3 + row] = x;
                }
            }
            t[0] = local[9]; t[1] = local[10]; t[2] = mirrorZ ? -local[11] : local[11];
            return a[0] * (a[4] * a[8] - a[7] * a[5]) - a[3] * (a[1] * a[8] - a[7] * a[2]) + a[6] * (a[1] * a[5] - a[4] * a[2]);
        }

        /// <summary>The chunk UV of a model UV for a part (see <see cref="UvRule"/>).</summary>
        public static void ChunkUv(float u, float v, float patternScale, float offU, float offV, BuildOptions o, out float cu, out float cv)
        {
            if (o.Uv == UvRule.Exporter)
            {
                float ps = (patternScale > 0 && !float.IsNaN(patternScale) && !float.IsInfinity(patternScale)) ? patternScale : 1f;
                cu = u / ps + offU;
                cv = v / ps + offV;
            }
            else
            {
                cu = u + offU;
                cv = v + offV;
            }
            if (o.VFlip) cv = 1f - cv;
        }

        private sealed class ChunkAccum
        {
            public ChunkKey Key;
            public readonly List<MirrorVertex> Vertices = new List<MirrorVertex>();
            public readonly List<int> Indices = new List<int>();
            public int Parts;
            public float H, S, V;
            public readonly PlacementProbe[] Probes = new PlacementProbe[8];
            /// <summary>The last part counted in <see cref="Parts"/> (a part with several submeshes of one material counts once).</summary>
            public int LastPart = -1;
        }

        /// <summary>S10 round 3 E: a model's triangle groups with the inputs they came from.</summary>
        internal sealed class S10Groups
        {
            public SubmeshInfo[] Subs;
            public int Tris, Uncovered, Overlap;
            public List<KeyValuePair<ulong, List<int>>> Groups;
        }

        public static long S10GroupsBuilt, S10GroupsReused;

        /// <summary>The model's <see cref="TriangleGroups"/>, computed on its first part and reused (read-only) after; a model
 /// whose submesh array or triangle count changed gets them again.</summary>
        private static List<KeyValuePair<ulong, List<int>>> GroupsOf(PreparedModel m, out int uncovered, out int overlap)
        {
            S10Groups g = System.Threading.Volatile.Read(ref m.S10TriGroups);
            if (g == null || !ReferenceEquals(g.Subs, m.Submeshes) || g.Tris != m.TriangleCount)
            {
                int u, o;
                List<KeyValuePair<ulong, List<int>>> list = TriangleGroups(m.Submeshes, m.TriangleCount, out u, out o);
                g = new S10Groups { Subs = m.Submeshes, Tris = m.TriangleCount, Groups = list, Uncovered = u, Overlap = o };
                System.Threading.Volatile.Write(ref m.S10TriGroups, g);
                System.Threading.Interlocked.Increment(ref S10GroupsBuilt);
            }
            else System.Threading.Interlocked.Increment(ref S10GroupsReused);
            uncovered = g.Uncovered;
            overlap = g.Overlap;
            return g.Groups;
        }

        /// <summary>
 /// S10 round 3 E: one worker thread's BuildCell scratch (thread-static; one cell at a time per thread). Accumulators
 /// come back cleared with their list capacity; one that grew beyond <see cref="KeepVertices"/> vertices is dropped so
 /// a single huge cell does not stay resident.
 /// </summary>
        private sealed class S10CellScratch
        {
            public const int KeepVertices = 1 << 16, KeepAccums = 256;
            [ThreadStatic] private static S10CellScratch _mine;
            public readonly Dictionary<ChunkKey, ChunkAccum> Chunks = new Dictionary<ChunkKey, ChunkAccum>();
            public readonly List<ChunkAccum> Order = new List<ChunkAccum>();
            public readonly double[] A = new double[9], T = new double[3];
            private readonly Stack<ChunkAccum> _free = new Stack<ChunkAccum>();
            private int[] _remap = new int[0];
            private bool _busy;

            public static S10CellScratch Take()
            {
                S10CellScratch s = _mine ?? (_mine = new S10CellScratch());
                if (s._busy) return new S10CellScratch { _busy = true }; // re-entered on this thread: a private one
                s._busy = true;
                return s;
            }

            public ChunkAccum Accum(ChunkKey key, float h, float sat, float v)
            {
                ChunkAccum acc = _free.Count > 0 ? _free.Pop() : new ChunkAccum();
                acc.Key = key;
                acc.H = h;
                acc.S = sat;
                acc.V = v;
                return acc;
            }

            public int[] Remap(int n)
            {
                if (_remap.Length < n) _remap = new int[Math.Max(n, _remap.Length * 2)];
                return _remap;
            }

            public void Release()
            {
                foreach (ChunkAccum acc in Order)
                {
                    if (acc.Vertices.Capacity > KeepVertices || _free.Count >= KeepAccums) continue;
                    acc.Vertices.Clear();
                    acc.Indices.Clear();
                    acc.Parts = 0;
                    acc.LastPart = -1;
                    Array.Clear(acc.Probes, 0, acc.Probes.Length);
                    _free.Push(acc);
                }
                Order.Clear();
                Chunks.Clear();
                if (_remap.Length > KeepVertices * 4) _remap = new int[0];
                _busy = false;
            }
        }

        /// <summary>The 8 corner directions of the S3.C2 probes (corner i: bit 0 +x, bit 1 +y, bit 2 +z).</summary>
        public static Vec3 CornerSign(int i)
        {
            return new Vec3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1);
        }

        /// <summary>
 /// The SE-local position of a model vertex under a part, computed independently of <see cref="PartTransform"/>
 /// (the PartAdd rows Right, Up, Backward, Translation applied in doubles, never mirrored): the S3.C2 reference.
 /// </summary>
        public static Vec3 SeLocalOf(float[] local, float[] sePos, int vi)
        {
            double x = sePos[vi * 3], y = sePos[vi * 3 + 1], z = sePos[vi * 3 + 2];
            return new Vec3(x * local[0] + y * local[3] + z * local[6] + local[9],
                            x * local[1] + y * local[4] + z * local[7] + local[10],
                            x * local[2] + y * local[5] + z * local[8] + local[11]);
        }

        /// <summary>
 /// The triangle ranges of a model as Wire's <c>UnityMeshBuilder</c> draws them: each
 /// triangle exactly once, in the first submesh whose range holds it (overlapping ranges: the first wins); triangles
 /// outside every range go to a material-0 group. Returns (material, triangle list) groups in first-seen order.
 /// </summary>
        public static List<KeyValuePair<ulong, List<int>>> TriangleGroups(SubmeshInfo[] subs, int triangleCount, out int uncovered, out int overlap)
        {
            uncovered = 0;
            overlap = 0;
            var groups = new List<KeyValuePair<ulong, List<int>>>();
            var index = new Dictionary<ulong, int>();
            var covered = new bool[triangleCount];
            Func<ulong, List<int>> group = key =>
            {
                int g;
                if (!index.TryGetValue(key, out g)) { g = groups.Count; index[key] = g; groups.Add(new KeyValuePair<ulong, List<int>>(key, new List<int>())); }
                return groups[g].Value;
            };
            if (subs != null)
            {
                foreach (SubmeshInfo sm in subs)
                {
                    if (sm.TriCount <= 0) continue;
                    List<int> dst = group(sm.MaterialKey);
                    int end = Math.Min(triangleCount, sm.TriStart + sm.TriCount);
                    for (int tri = Math.Max(0, sm.TriStart); tri < end; tri++)
                    {
                        if (covered[tri]) { overlap++; continue; }
                        covered[tri] = true;
                        dst.Add(tri);
                    }
                }
            }
            List<int> rest = null;
            for (int tri = 0; tri < triangleCount; tri++)
            {
                if (covered[tri]) continue;
                uncovered++;
                if (rest == null) rest = group(0UL);
                rest.Add(tri);
            }
            // a model without submeshes goes to material 0 whole: not "uncovered" in the log's sense
            if (subs == null || subs.Length == 0) uncovered = 0;
            return groups;
        }

        /// <summary>
 /// Builds every chunk of one cell: for each part, its triangles go to the chunk of (material, colour) by
 /// <see cref="TriangleGroups"/> (each triangle once: Wire's coverage rule); vertices are transformed by the part
 /// matrix in the prepared (mirrored) space, normals and tangents rotated and renormalised; a part with det(A) &lt; 0
 /// (an improper matrix) gets its triangles reversed and w negated. Only the vertices a chunk references are copied.
 /// Each chunk keeps its 8 S3.C2 probes (<see cref="PlacementProbe"/>).
 /// </summary>
        public static CellBuildResult BuildCell(CellBuildInput input)
        {
            // S10 round 3 E (S10AllocCuts, env DEEPENGINEERS_S10_ALLOCCUTS off restores): the worker's scratch (chunk
 // accumulators with their lists, the vertex remap, the dictionaries) is reused across cells on its thread, and a
 // model's triangle groups are computed once per model instead of once per part; the result is byte-identical
 // (S10 tests in S3Tests build the same cells both ways)
            S10CellScratch sc = S10AllocCuts.On ? S10CellScratch.Take() : null;
            try { return BuildCellCore(input, sc); }
            finally { if (sc != null) sc.Release(); }
        }

        private static CellBuildResult BuildCellCore(CellBuildInput input, S10CellScratch sc)
        {
            var res = new CellBuildResult { GridId = input.GridId, Cell = input.Cell, Version = input.Version };
            var chunks = sc != null ? sc.Chunks : new Dictionary<ChunkKey, ChunkAccum>();
            var order = sc != null ? sc.Order : new List<ChunkAccum>();
            var a = sc != null ? sc.A : new double[9];
            var t = sc != null ? sc.T : new double[3];
            int partIndex = -1;
            foreach (PartBuild pb in input.Parts)
            {
                PreparedModel m = pb.Model;
                PartRecord p = pb.Part;
                if (m == null || p == null || !p.Chunked) continue;
                partIndex++;
                res.Parts++;
                res.ModelVertices += m.VertexCount;
                res.ModelTriangles += m.TriangleCount;
                double det = PartTransform(p.Local, input.Options.MirrorZ, a, t);
                bool improper = det < 0;
                long ck = p.ColourKey;
                uint bits = p.ChunkBits; // S5: NoShadows | MetalnessColorable enter the chunk key
                int uncovered, overlap;
                List<KeyValuePair<ulong, List<int>>> groups = sc != null ? GroupsOf(m, out uncovered, out overlap) : TriangleGroups(m.Submeshes, m.TriangleCount, out uncovered, out overlap);
                res.UncoveredTriangles += uncovered;
                res.OverlapTriangles += overlap;
                int[] remap = null;
                foreach (KeyValuePair<ulong, List<int>> grp in groups)
                {
                    if (grp.Value.Count == 0) continue;
                    // S7t-sn-altitude (T3): an impostor's triangles share one flat material per colour, without shadows
                    var key = input.Impostor ? new ChunkKey(input.GridId, input.Cell, ImpostorRules.MaterialKey, ck, S5Conv.NoShadows)
                                             : new ChunkKey(input.GridId, input.Cell, grp.Key, ck, bits);
                    ChunkAccum acc;
                    if (!chunks.TryGetValue(key, out acc))
                    {
                        acc = sc != null ? sc.Accum(key, p.H, p.S, p.V) : new ChunkAccum { Key = key, H = p.H, S = p.S, V = p.V };
                        chunks[key] = acc;
                        order.Add(acc);
                    }
                    if (acc.LastPart != partIndex) { acc.Parts++; acc.LastPart = partIndex; }
                    if (remap == null) remap = sc != null ? sc.Remap(m.VertexCount) : new int[m.VertexCount];
                    for (int i = 0; i < m.VertexCount; i++) remap[i] = -1;
                    foreach (int tri in grp.Value)
                    {
                        int i0 = m.Tris[tri * 3], i1 = m.Tris[tri * 3 + 1], i2 = m.Tris[tri * 3 + 2];
                        int u0 = Emit(acc, m, p, i0, a, t, improper, remap, input.Options);
                        int u1 = Emit(acc, m, p, i1, a, t, improper, remap, input.Options);
                        int u2 = Emit(acc, m, p, i2, a, t, improper, remap, input.Options);
                        acc.Indices.Add(u0);
                        acc.Indices.Add(improper ? u2 : u1);
                        acc.Indices.Add(improper ? u1 : u2);
                    }
                }
            }
            foreach (ChunkAccum acc in order)
            {
                if (acc.Indices.Count == 0) continue;
                var c = new ChunkMeshData
                {
                    Key = acc.Key, Vertices = acc.Vertices.ToArray(), VertexCount = acc.Vertices.Count, IndexCount = acc.Indices.Count,
                    Parts = acc.Parts, H = acc.H, S = acc.S, V = acc.V,
                };
                for (int k = 0; k < 8; k++)
                {
                    PlacementProbe pr = acc.Probes[k];
                    if (pr.Valid)
                    {
                        MirrorVertex dv = c.Vertices[pr.Vertex];
                        pr.Drawn = new Vec3(dv.Px, dv.Py, dv.Pz); // the uploaded position of that vertex
                    }
                    c.Probes[k] = pr;
                }
                if (c.VertexCount > 65535) c.Indices32 = acc.Indices.ToArray();
                else
                {
                    c.Indices16 = new ushort[acc.Indices.Count];
                    for (int i = 0; i < acc.Indices.Count; i++) c.Indices16[i] = (ushort)acc.Indices[i];
                }
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (int i = 0; i < c.VertexCount; i++)
                {
                    MirrorVertex mv = c.Vertices[i];
                    if (mv.Px < minX) minX = mv.Px; if (mv.Py < minY) minY = mv.Py; if (mv.Pz < minZ) minZ = mv.Pz;
                    if (mv.Px > maxX) maxX = mv.Px; if (mv.Py > maxY) maxY = mv.Py; if (mv.Pz > maxZ) maxZ = mv.Pz;
                }
                c.MinX = minX; c.MinY = minY; c.MinZ = minZ; c.MaxX = maxX; c.MaxY = maxY; c.MaxZ = maxZ;
                int deg;
                c.Agreement = Agreement(c.Vertices, c.Indices16, c.Indices32, c.IndexCount, out deg);
                c.Degenerate = deg;
                if (c.Agreement < res.MinAgreement) res.MinAgreement = c.Agreement;
                res.Vertices += c.VertexCount;
                res.Triangles += c.TriangleCount;
                res.Chunks.Add(c);
            }
            return res;
        }

        private static int Emit(ChunkAccum acc, PreparedModel m, PartRecord p, int vi, double[] a, double[] t, bool improper, int[] remap, BuildOptions o)
        {
            int existing = remap[vi];
            if (existing >= 0) return existing;
            int o3 = vi * 3, o4 = vi * 4, o2 = vi * 2;
            double x = m.Pos[o3], y = m.Pos[o3 + 1], z = m.Pos[o3 + 2];
            double nx = m.Nrm[o3], ny = m.Nrm[o3 + 1], nz = m.Nrm[o3 + 2];
            double tx = m.Tan[o4], ty = m.Tan[o4 + 1], tz = m.Tan[o4 + 2];
            var v = new MirrorVertex
            {
                Px = (float)(a[0] * x + a[3] * y + a[6] * z + t[0]),
                Py = (float)(a[1] * x + a[4] * y + a[7] * z + t[1]),
                Pz = (float)(a[2] * x + a[5] * y + a[8] * z + t[2]),
            };
            double rnx = a[0] * nx + a[3] * ny + a[6] * nz, rny = a[1] * nx + a[4] * ny + a[7] * nz, rnz = a[2] * nx + a[5] * ny + a[8] * nz;
            Normalise(ref rnx, ref rny, ref rnz);
            double rtx = a[0] * tx + a[3] * ty + a[6] * tz, rty = a[1] * tx + a[4] * ty + a[7] * tz, rtz = a[2] * tx + a[5] * ty + a[8] * tz;
            Normalise(ref rtx, ref rty, ref rtz);
            v.Nx = (float)rnx; v.Ny = (float)rny; v.Nz = (float)rnz;
            v.Tx = (float)rtx; v.Ty = (float)rty; v.Tz = (float)rtz;
            v.Tw = improper ? -m.Tan[o4 + 3] : m.Tan[o4 + 3];
            float cu, cv;
            ChunkUv(m.Uv[o2], m.Uv[o2 + 1], m.PatternScale, p.OffU, p.OffV, o, out cu, out cv);
            v.U = cu; v.V = cv;
            int idx = acc.Vertices.Count;
            acc.Vertices.Add(v);
            remap[vi] = idx;
            if (m.SePos != null)
            {
                // S3.C2 probes: the vertex most extreme along each corner direction, by its independent SE-local position
                Vec3 l = SeLocalOf(p.Local, m.SePos, vi);
                for (int k = 0; k < 8; k++)
                {
                    double score = Vec3.Dot(l, CornerSign(k));
                    if (!acc.Probes[k].Valid || score > acc.Probes[k].Score + 1e-9)
                        acc.Probes[k] = new PlacementProbe { Valid = true, SeLocal = l, Vertex = idx, Score = score };
                }
            }
            return idx;
        }

        /// <summary>
 /// The S3.C2 corners of a set of chunks (main thread, Unity-free): for corner i the probe with the largest SE-local
 /// score over every chunk; <paramref name="toWorld"/> maps the probe's drawn (uploaded, Unity-local) position with
 /// the chunk's renderer matrix. False when no chunk has probes.
 /// </summary>
        public static bool ProbeCorners(IList<PlacementProbe[]> chunkProbes, IList<Func<Vec3, Vec3>> toWorld, Vec3[] seLocal, Vec3[] drawnWorld)
        {
            bool any = false;
            for (int k = 0; k < 8; k++)
            {
                int bestChunk = -1;
                PlacementProbe best = default(PlacementProbe);
                for (int c = 0; c < chunkProbes.Count; c++)
                {
                    PlacementProbe pr = chunkProbes[c][k];
                    if (!pr.Valid) continue;
                    if (bestChunk < 0 || pr.Score > best.Score + 1e-9) { best = pr; bestChunk = c; }
                }
                if (bestChunk < 0) return false;
                seLocal[k] = best.SeLocal;
                drawnWorld[k] = toWorld[bestChunk](best.Drawn);
                any = true;
            }
            return any;
        }
    }
}
