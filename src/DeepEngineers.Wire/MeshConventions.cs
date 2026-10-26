using System;
using System.Collections.Generic;
using System.Globalization;

// Entity-local mesh conventions of docs/CONTRACT.md §2.5 as pure code (ROADMAP S3): the mirror (x, y, z) -> (x, y, -z),
// Unity triangles (I0, I2, I1) of MyModel.Triangles, the per-model winding measure with the automatic flip below 1 %,
// the tangent handedness, AABB corners in one fixed order, the armor UV transform and a worker-thread build pass of
// Unity-ready vertex arrays (no Unity types: the Subnautica plugin copies the arrays into a Mesh on the main thread).
// Python twin: tools/worldmap/wire_ref.py (mesh_to_unity, winding_measure, tangent_w, aabb_corners, armor_uv,
// pattern_offset).
namespace DeepEngineers.Wire
{
    /// <summary>
 /// Triangle winding agreement of a mesh in Unity space (CONTRACT §2.5, ROADMAP S3.C3): a triangle (a, b, c) agrees
 /// when <c>cross(b − a, c − a) · (n_a + n_b + n_c) &gt; 0</c>. Degenerate triangles (the census rule of
 /// tools/mwm-winding: |g| &lt; 1e-9, |Σn| &lt; 1e-6 or |cos| &lt; 0.05) are counted apart and excluded from the fraction.
 /// </summary>
    public struct WindingStats : IEquatable<WindingStats>
    {
        public readonly int Agree, Disagree, Degenerate;

        public WindingStats(int agree, int disagree, int degenerate)
        {
            Agree = agree; Disagree = disagree; Degenerate = degenerate;
        }

        public int Triangles { get { return Agree + Disagree + Degenerate; } }
        public int Measured { get { return Agree + Disagree; } }

        /// <summary>Agree / (Agree + Disagree); 1 when no triangle is measurable (nothing to flip).</summary>
        public double Fraction { get { return Measured == 0 ? 1.0 : (double)Agree / Measured; } }

        /// <summary>CONTRACT §2.5: flip a model whose agreement is below 1 %.</summary>
        public bool ShouldFlip { get { return Measured > 0 && Fraction < MeshConventions.WindingFlipBelow; } }

        /// <summary>ROADMAP S3.C3: more than 99 % agree.</summary>
        public bool Passes { get { return Measured > 0 && Fraction > MeshConventions.WindingPassAbove; } }

        /// <summary>The statistics after swapping every triangle's last two indices.</summary>
        public WindingStats Flipped() { return new WindingStats(Disagree, Agree, Degenerate); }

        public WindingStats Plus(WindingStats o) { return new WindingStats(Agree + o.Agree, Disagree + o.Disagree, Degenerate + o.Degenerate); }

        public bool Equals(WindingStats o) { return Agree == o.Agree && Disagree == o.Disagree && Degenerate == o.Degenerate; }
        public override bool Equals(object obj) { return obj is WindingStats && Equals((WindingStats)obj); }
        public override int GetHashCode() { return Agree ^ (Disagree * 397) ^ (Degenerate * 7919); }

        public override string ToString()
        {
            return "agree=" + Agree + " disagree=" + Disagree + " degenerate=" + Degenerate + " fraction="
                + Fraction.ToString("0.0000", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One Unity submesh of a built mesh: a material over a range of the triangle index list.</summary>
    public struct UnitySubmesh
    {
        public ulong MaterialKey;
        public uint DrawTechnique;
        /// <summary>First index (not triangle) in <see cref="UnityMeshData.Triangles"/>; Unity <c>SubMeshDescriptor.indexStart</c>.</summary>
        public int IndexStart;
        public int IndexCount;

        public UnitySubmesh(ulong materialKey, uint drawTechnique, int indexStart, int indexCount)
        {
            MaterialKey = materialKey; DrawTechnique = drawTechnique; IndexStart = indexStart; IndexCount = indexCount;
        }
    }

    /// <summary>
 /// Unity-ready arrays of one mesh: positions,
 /// normals (3 floats per vertex), tangents (4: xyz and the handedness w), UVs (2), triangles (Unity order, already
 /// flipped where the winding measure asked for it) grouped into submeshes by material.
 /// </summary>
    public sealed class UnityMeshData
    {
        public int VertexCount;
        public float[] Positions;
        public float[] Normals;
        public float[] Tangents;
        public float[] Uv;
        public int[] Triangles;
        public UnitySubmesh[] Submeshes;
        public Vec3 BoundsMin, BoundsMax;
        /// <summary>Parts added; parts whose model was flipped by the winding rule.</summary>
        public int Parts, FlippedParts;
        /// <summary>Triangles not covered by any submesh of their model (put in the material-key-0 submesh).</summary>
        public int UncoveredTriangles;
        /// <summary>The winding measure of the output triangles (after any flip).</summary>
        public WindingStats Winding;
    }

    /// <summary>Entity-local mesh conventions (CONTRACT §2.5) for the Subnautica side and the tests.</summary>
    public static class MeshConventions
    {
        /// <summary>CONTRACT §2.5: a model with fewer than 1 % agreeing triangles is flipped (and logged).</summary>
        public const double WindingFlipBelow = 0.01;
        /// <summary>ROADMAP S3.C3: more than 99 % of triangles agree.</summary>
        public const double WindingPassAbove = 0.99;
        /// <summary>Census degenerate rule (tools/mwm-winding/mwm_winding.py).</summary>
        public const double DegenerateArea = 1e-9, DegenerateNormal = 1e-6, DegenerateCos = 0.05;

        /// <summary>
 /// SE's normal maps are read with y negated (Content/Shaders/PixelUtils.hlsli <c>Normal</c> calls
 /// <c>adjust_normalmap_precomputed_tangents</c>: <c>normalmap.y *= -1</c>, then <c>(T, B = cross(T, N)·w, N)</c>). With
 /// <see cref="TangentW(Vec3, Vec3, Vec3)"/> Unity's reconstructed binormal is the mirrored SE bitangent, so the
 /// green channel of the repacked <c>_BumpMap</c> must carry <c>−y</c> (G′ = 1 − G) to light like SE (HYPOTHESIS until
 /// the S3.N1 "bump faces the sun" capture).
 /// </summary>
        public const int SeNormalMapGreenSign = -1;

        // ------------------------------------------------------------------------------------------------ mirror and triangles

 /// <summary>The entity-local mirror (x, y, z) → (x, y, −z) (= <see cref="WireMath.Mirror"/>).</summary>
        public static Vec3 Mirror(Vec3 v) { return WireMath.Mirror(v); }

        /// <summary>Mirrors packed xyz triples: dst may be src. Length a multiple of 3.</summary>
        public static void MirrorXyz(float[] src, float[] dst)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (dst == null) throw new ArgumentNullException("dst");
            if (src.Length % 3 != 0 || dst.Length < src.Length) throw new ArgumentException("arrays of xyz triples expected");
            for (int i = 0; i < src.Length; i += 3)
            {
                dst[i] = src[i];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = -src[i + 2];
            }
        }

        /// <summary>
 /// Wire indices (<c>MyModel.Triangles</c> order) to the Unity triangle list (I0, I2, I1); each index must be below
 /// <paramref name="vertexCount"/>.
 /// </summary>
        public static void MirrorTriangles(uint[] wire, int[] unity, int vertexCount)
        {
            if (wire == null) throw new ArgumentNullException("wire");
            if (unity == null) throw new ArgumentNullException("unity");
            if (wire.Length % 3 != 0 || unity.Length < wire.Length) throw new ArgumentException("index lists must hold whole triangles");
            for (int i = 0; i < wire.Length; i += 3)
            {
                uint a = wire[i], b = wire[i + 1], c = wire[i + 2];
                if (a >= (uint)vertexCount || b >= (uint)vertexCount || c >= (uint)vertexCount)
                    throw new ArgumentOutOfRangeException("wire", "index beyond vertexCount at triangle " + (i / 3));
                unity[i] = (int)a;
                unity[i + 1] = (int)c;
                unity[i + 2] = (int)b;
            }
        }

        /// <summary>Swaps the last two indices of every triangle in place (the automatic flip, or its undo).</summary>
        public static void FlipTriangles(int[] tris, int start, int count)
        {
            if (tris == null) throw new ArgumentNullException("tris");
            if (start < 0 || count < 0 || count % 3 != 0 || start > tris.Length - count) throw new ArgumentOutOfRangeException("count");
            for (int i = start; i < start + count; i += 3)
            {
                int t = tris[i + 1];
                tris[i + 1] = tris[i + 2];
                tris[i + 2] = t;
            }
        }

        public static void FlipTriangles(int[] tris) { FlipTriangles(tris, 0, tris == null ? 0 : tris.Length); }

        // ------------------------------------------------------------------------------------------------ tangent handedness

 /// <summary>
 /// Unity tangent w from the mirrored normal, tangent and binormal (CONTRACT §2.5):
 /// <c>sign(dot(Cross(n′, t′), b′))</c>, +1 when zero. Unity rebuilds the binormal as <c>Cross(n′, t′)·w</c>, so it
 /// equals the mirrored SE binormal direction. This is always SE's own stored sign
 /// (<see cref="SeTangentSign"/>): the mirror does not change w.
 /// </summary>
        public static float TangentW(Vec3 normalUnity, Vec3 tangentUnity, Vec3 binormalUnity)
        {
            return Vec3.Dot(Vec3.Cross(normalUnity, tangentUnity), binormalUnity) < 0.0 ? -1f : 1f;
        }

        /// <summary>
 /// SE's tangent sign in SE space (VRage.Render11 MyMeshes: <c>tangent.Cross(normal).Dot(bitangent) &lt; 0 ? −1: 1</c>),
 /// the reference <see cref="TangentW"/> is tested against.
 /// </summary>
        public static float SeTangentSign(Vec3 normalSe, Vec3 tangentSe, Vec3 binormalSe)
        {
            return Vec3.Dot(Vec3.Cross(tangentSe, normalSe), binormalSe) < 0.0 ? -1f : 1f;
        }

        // ------------------------------------------------------------------------------------------------ winding measure

 /// <summary>Classifies one triangle: +1 agree, −1 disagree, 0 degenerate (census rule).</summary>
        public static int ClassifyTriangle(Vec3 a, Vec3 b, Vec3 c, Vec3 na, Vec3 nb, Vec3 nc)
        {
            Vec3 g = Vec3.Cross(b - a, c - a);
            Vec3 avg = na + nb + nc;
            double d = Vec3.Dot(g, avg);
            double gl = g.Length, al = avg.Length;
            if (gl < DegenerateArea || al < DegenerateNormal || Math.Abs(d) < DegenerateCos * gl * al) return 0;
            return d > 0 ? 1 : -1;
        }

        /// <summary>
 /// The C3 measure on Unity-space arrays (positions and normals already mirrored, triangles in Unity order), over
 /// triangles [triStart, triStart + triCount).
 /// </summary>
        public static WindingStats MeasureWinding(float[] positionsUnity, float[] normalsUnity, int[] trianglesUnity, int triStart, int triCount)
        {
            if (positionsUnity == null) throw new ArgumentNullException("positionsUnity");
            if (normalsUnity == null) throw new ArgumentNullException("normalsUnity");
            if (trianglesUnity == null) throw new ArgumentNullException("trianglesUnity");
            if (triStart < 0 || triCount < 0 || ((long)triStart + triCount) * 3 > trianglesUnity.Length) throw new ArgumentOutOfRangeException("triCount");
            int agree = 0, disagree = 0, degenerate = 0;
            for (int t = triStart; t < triStart + triCount; t++)
            {
                int i0 = trianglesUnity[3 * t], i1 = trianglesUnity[3 * t + 1], i2 = trianglesUnity[3 * t + 2];
                int k = ClassifyTriangle(P(positionsUnity, i0), P(positionsUnity, i1), P(positionsUnity, i2),
                                         P(normalsUnity, i0), P(normalsUnity, i1), P(normalsUnity, i2));
                if (k > 0) agree++;
                else if (k < 0) disagree++;
                else degenerate++;
            }
            return new WindingStats(agree, disagree, degenerate);
        }

        public static WindingStats MeasureWinding(float[] positionsUnity, float[] normalsUnity, int[] trianglesUnity)
        {
            if (trianglesUnity == null) throw new ArgumentNullException("trianglesUnity");
            return MeasureWinding(positionsUnity, normalsUnity, trianglesUnity, 0, trianglesUnity.Length / 3);
        }

        /// <summary>
 /// The C3 measure of a wire model as Subnautica will draw it: positions and normals mirrored, triangles (I0, I2, I1).
 /// No allocation. Rigid part matrices (det +1) do not change it, so one measure per model decides the flip.
 /// </summary>
        public static WindingStats MeasureWinding(ModelGeometry model)
        {
            if (model == null) throw new ArgumentNullException("model");
            int agree = 0, disagree = 0, degenerate = 0;
            uint[] ix = model.Indices;
            for (int t = 0; t + 2 < ix.Length; t += 3)
            {
                int i0 = (int)ix[t], i1 = (int)ix[t + 2], i2 = (int)ix[t + 1];   // Unity (I0, I2, I1)
                int k = ClassifyTriangle(Mirror(model.Position(i0)), Mirror(model.Position(i1)), Mirror(model.Position(i2)),
                                         Mirror(model.Normal(i0)), Mirror(model.Normal(i1)), Mirror(model.Normal(i2)));
                if (k > 0) agree++;
                else if (k < 0) disagree++;
                else degenerate++;
            }
            return new WindingStats(agree, disagree, degenerate);
        }

        private static Vec3 P(float[] a, int i) { return new Vec3(a[3 * i], a[3 * i + 1], a[3 * i + 2]); }

        // ------------------------------------------------------------------------------------------------ bounds and corners

 /// <summary>
 /// Corner i of the box [min, max]: bit 0 of i selects max.x, bit 1 max.y, bit 2 max.z (i = 0 is min, 7 is max).
 /// The order of the S3.C2 corner logs (SE-local corners; Subnautica maps each through its own transforms).
 /// </summary>
        public static Vec3 AabbCorner(Vec3 min, Vec3 max, int index)
        {
            if (index < 0 || index > 7) throw new ArgumentOutOfRangeException("index");
            return new Vec3((index & 1) != 0 ? max.X : min.X, (index & 2) != 0 ? max.Y : min.Y, (index & 4) != 0 ? max.Z : min.Z);
        }

        public static Vec3[] AabbCorners(Vec3 min, Vec3 max)
        {
            var c = new Vec3[8];
            for (int i = 0; i < 8; i++) c[i] = AabbCorner(min, max, i);
            return c;
        }

        /// <summary>The 8 corners of a model box [min, max] carried into the entity's SE-local space by a part matrix.</summary>
        public static Vec3[] PartCorners(SeMatrix partLocal, Vec3 min, Vec3 max)
        {
            var c = new Vec3[8];
            for (int i = 0; i < 8; i++) c[i] = partLocal.Transform(AabbCorner(min, max, i));
            return c;
        }

        /// <summary>The Unity-local AABB of an SE-local AABB: z range mirrored ([−max.z, −min.z]).</summary>
        public static void MirrorAabb(Vec3 seMin, Vec3 seMax, out Vec3 unityMin, out Vec3 unityMax)
        {
            unityMin = new Vec3(seMin.X, seMin.Y, -seMax.Z);
            unityMax = new Vec3(seMax.X, seMax.Y, -seMin.Z);
        }

        /// <summary>AABB of packed xyz triples (the first <paramref name="vertexCount"/>); zeros when empty.</summary>
        public static void Bounds(float[] xyz, int vertexCount, out Vec3 min, out Vec3 max)
        {
            if (xyz == null) throw new ArgumentNullException("xyz");
            if (vertexCount < 0 || 3L * vertexCount > xyz.Length) throw new ArgumentOutOfRangeException("vertexCount");
            if (vertexCount == 0) { min = Vec3.Zero; max = Vec3.Zero; return; }
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, z0 = double.PositiveInfinity;
            double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity, z1 = double.NegativeInfinity;
            for (int i = 0; i < vertexCount; i++)
            {
                double x = xyz[3 * i], y = xyz[3 * i + 1], z = xyz[3 * i + 2];
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
                if (z < z0) z0 = z;
                if (z > z1) z1 = z;
            }
            min = new Vec3(x0, y0, z0);
            max = new Vec3(x1, y1, z1);
        }

        // ------------------------------------------------------------------------------------------------ armor UVs

 /// <summary>
 /// The armor UV of a vertex as SE draws it: <c>uv / patternScale + offset</c> (VRage.Render11 MyMeshes divides the
 /// texcoords by <c>MyModel.PatternScale</c> at load; the cube-instancing vertex shader adds the per-part offset).
 /// V is not flipped (raw DDS rows; CONTRACT §2.5). A non-positive or non-finite pattern scale counts as 1.
 /// </summary>
        public static void ArmorUv(float u, float v, float patternScale, float offsetU, float offsetV, out float uo, out float vo)
        {
            if (!(patternScale > 0f) || float.IsInfinity(patternScale) || patternScale == 1f)
            {
                uo = u + offsetU;
                vo = v + offsetV;
                return;
            }
            uo = u / patternScale + offsetU;
            vo = v / patternScale + offsetV;
        }

        /// <summary>
 /// The per-part armor pattern offset of SE's cube-instancing vertex shader (Content/Shaders/Geometry/
 /// VertexTemplateBase.hlsli, USE_DEFORMED_CUBE_INSTANCING): <c>(offsetU / patternU, offsetV / patternV)</c>, from the
 /// <c>Vector4UByte(offsetX, offsetY, patternX, patternY)</c> that <c>MyCubeGrid.GetCubeParts(…, outPatternOffsets, …)</c>
 /// computes per part and <c>MyCubeInstanceData.SetTextureOffset</c> packs. Pattern counts below 1 count as 1.
 /// <c>MyCubeInstanceData.GetTextureOffset</c> does not return this in the installed build
 /// so the SE plugin uses GetCubeParts' value.
 /// </summary>
        public static void ArmorPatternOffset(int offsetX, int offsetY, int patternX, int patternY, out float offsetU, out float offsetV)
        {
            offsetU = (float)offsetX / Math.Max(1, patternX);
            offsetV = (float)offsetY / Math.Max(1, patternY);
        }

        /// <summary>
 /// The same offset decoded from the three packed instance bytes the shader reads: w5 = offset X, w6 = offset Y,
 /// w7 = ((patternX − 1) &lt;&lt; 4) | (patternY − 1); patternU = 1 + (w7 &gt;&gt; 4 &amp; 0xF), patternV = 1 + (w7 &amp; 0xF).
 /// </summary>
        public static void ArmorPatternOffsetPacked(byte w5, byte w6, byte w7, out float offsetU, out float offsetV)
        {
            float pu = 1 + ((w7 >> 4) & 0xF);
            float pv = 1 + (w7 & 0xF);
            offsetU = w5 / pu;
            offsetV = w6 / pv;
        }

        /// <summary><c>SetTextureOffset</c>'s packing of a pattern size into w7 (each count 1..16).</summary>
        public static byte PackPattern(int patternX, int patternY)
        {
            if (patternX < 1 || patternX > 16 || patternY < 1 || patternY > 16) throw new ArgumentOutOfRangeException("patternX", "1..16");
            return (byte)(((patternX - 1) << 4) | (patternY - 1));
        }
    }

    /// <summary>
 /// Builds one Unity mesh (a chunk) from parts on a worker thread (ARCHITECTURE §5.8): each part's model vertices are
 /// transformed by the part matrix in SE space (CONTRACT §2.3 "compose in SE space"), made relative to the chunk origin,
 /// then mirrored; normals, tangents and binormals are rotated the same way (part matrices are rigid; normals use the
 /// inverse transpose for safety) and normalised; tangent w by <see cref="MeshConventions.TangentW"/>; UVs by
 /// <see cref="MeshConventions.ArmorUv"/>; triangles (I0, I2, I1), flipped per part when asked (the per-model
 /// winding rule). Triangles are grouped by material key into submeshes in first-seen order.
 /// </summary>
    public sealed class UnityMeshBuilder
    {
        private readonly Vec3 _origin;
        private readonly List<float> _pos = new List<float>();
        private readonly List<float> _nrm = new List<float>();
        private readonly List<float> _tan = new List<float>();
        private readonly List<float> _uv = new List<float>();
        private readonly List<ulong> _keys = new List<ulong>();
        private readonly Dictionary<ulong, int> _group = new Dictionary<ulong, int>();
        private readonly List<List<int>> _tris = new List<List<int>>();
        private readonly List<uint> _tech = new List<uint>();
        private int _parts, _flipped, _uncovered;

        /// <param name="originSe">the chunk origin in the entity's SE-local space (the chunk GameObject sits at Mirror(originSe) under the Entity)</param>
        public UnityMeshBuilder(Vec3 originSe) { _origin = originSe; }

        public UnityMeshBuilder() : this(Vec3.Zero) { }

        public Vec3 OriginSe { get { return _origin; } }
        public int VertexCount { get { return _pos.Count / 3; } }

        /// <summary>Adds one part from its C4 record (local matrix and UV offset of the PartAdd).</summary>
        public void AddPart(ModelGeometry model, PartAdd part, bool flipWinding)
        {
            AddPart(model, ModelCodec.PartLocal(part), part.uvOffset.x, part.uvOffset.y, flipWinding);
        }

        /// <summary>Adds one part: <paramref name="partLocal"/> carries model space into the entity's SE-local space.</summary>
        public void AddPart(ModelGeometry model, SeMatrix partLocal, float uvOffsetU, float uvOffsetV, bool flipWinding)
        {
            if (model == null) throw new ArgumentNullException("model");
            string err;
            if (!model.Validate(out err)) throw new ArgumentException("invalid model: " + err);
            int baseVertex = VertexCount;
            // normals: inverse transpose of the rotation rows (= the rows for a rigid part)
            Mat3 rot = Mat3.FromColumns(partLocal.Right, partLocal.Up, partLocal.Backward);  // l -> l.x R + l.y U + l.z B
            Mat3 nrm = rot.InverseTranspose();
            for (int i = 0; i < model.VertexCount; i++)
            {
                Vec3 p = MeshConventions.Mirror(partLocal.Transform(model.Position(i)) - _origin);
                Vec3 n = MeshConventions.Mirror(Unit(nrm.Multiply(model.Normal(i))));
                Vec3 t = MeshConventions.Mirror(Unit(rot.Multiply(model.Tangent(i))));
                Vec3 b = MeshConventions.Mirror(Unit(rot.Multiply(model.Binormal(i))));
                _pos.Add((float)p.X); _pos.Add((float)p.Y); _pos.Add((float)p.Z);
                _nrm.Add((float)n.X); _nrm.Add((float)n.Y); _nrm.Add((float)n.Z);
                _tan.Add((float)t.X); _tan.Add((float)t.Y); _tan.Add((float)t.Z); _tan.Add(MeshConventions.TangentW(n, t, b));
                float u, v;
                MeshConventions.ArmorUv(model.Uvs[2 * i], model.Uvs[2 * i + 1], model.PatternScale, uvOffsetU, uvOffsetV, out u, out v);
                _uv.Add(u); _uv.Add(v);
            }
            int tris = model.TriangleCount;
            var covered = new bool[tris];
            for (int s = 0; s < model.SubmeshCount; s++)
            {
                SubmeshRange r = model.Submeshes[s];
                List<int> dst = Group(r.MaterialKey, r.DrawTechnique);
                for (uint t = r.TriStart; t < r.TriStart + r.TriCount; t++)
                {
                    if (covered[t]) continue;   // overlapping ranges: the first submesh wins
                    covered[t] = true;
                    Emit(dst, model, (int)t, baseVertex, flipWinding);
                }
            }
            for (int t = 0; t < tris; t++)
            {
                if (covered[t]) continue;
                _uncovered++;
                Emit(Group(0UL, SeDrawTechnique.Mesh), model, t, baseVertex, flipWinding);
            }
            _parts++;
            if (flipWinding) _flipped++;
        }

        private static Vec3 Unit(Vec3 v)
        {
            double l = v.Length;
            return l > 0 ? v / l : v;
        }

        private List<int> Group(ulong key, uint technique)
        {
            int g;
            if (!_group.TryGetValue(key, out g))
            {
                g = _tris.Count;
                _group[key] = g;
                _keys.Add(key);
                _tech.Add(technique);
                _tris.Add(new List<int>());
            }
            return _tris[g];
        }

        private static void Emit(List<int> dst, ModelGeometry m, int t, int baseVertex, bool flip)
        {
            int i0 = baseVertex + (int)m.Indices[3 * t], i1 = baseVertex + (int)m.Indices[3 * t + 1], i2 = baseVertex + (int)m.Indices[3 * t + 2];
            dst.Add(i0);
            if (flip) { dst.Add(i1); dst.Add(i2); }   // flipped: back to the wire order
            else { dst.Add(i2); dst.Add(i1); }        // Unity (I0, I2, I1)
        }

        public UnityMeshData Build()
        {
            int n = VertexCount;
            var d = new UnityMeshData
            {
                VertexCount = n,
                Positions = _pos.ToArray(),
                Normals = _nrm.ToArray(),
                Tangents = _tan.ToArray(),
                Uv = _uv.ToArray(),
                Parts = _parts,
                FlippedParts = _flipped,
                UncoveredTriangles = _uncovered,
            };
            int total = 0;
            foreach (List<int> l in _tris) total += l.Count;
            d.Triangles = new int[total];
            d.Submeshes = new UnitySubmesh[_tris.Count];
            int o = 0;
            for (int g = 0; g < _tris.Count; g++)
            {
                _tris[g].CopyTo(d.Triangles, o);
                d.Submeshes[g] = new UnitySubmesh(_keys[g], _tech[g], o, _tris[g].Count);
                o += _tris[g].Count;
            }
            MeshConventions.Bounds(d.Positions, n, out d.BoundsMin, out d.BoundsMax);
            d.Winding = MeshConventions.MeasureWinding(d.Positions, d.Normals, d.Triangles);
            return d;
        }
    }
}
