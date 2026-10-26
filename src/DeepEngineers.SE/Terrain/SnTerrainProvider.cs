using System;
using System.Collections.Generic;
using System.IO;
using DeepEngineers.Terrain;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// The terrain provider of the 34 DE tiles (CONTRACT §3.1 "Identity and registration", member table; ARCHITECTURE
 /// §5.6). Id 45460 (<c>VRage.Voxels.MyStorageDataProviderAttribute(int)</c>, VRage.Game.dll). One instance per tile
 /// over one shared, ref-counted <see cref="SnOctreeSource"/>; the logic is <see cref="ProviderCore"/> (VRage-free,
 /// unit-tested), this class only unpacks SE's types.
 /// <para>Members, exact signatures VERIFIED against <c>VRage.Voxels.IMyStorageDataProvider</c>
 /// <c>SerializedSize</c>, <c>WriteTo(Stream)</c>,
 /// <c>ReadFrom(int, Stream, int, ref bool)</c>, <c>ReadRange(MyStorageData, MyStorageDataTypeFlags, ref Vector3I, int,
 /// ref Vector3I, ref Vector3I)</c>, <c>ReadRange(ref MyVoxelDataRequest, bool)</c>, <c>DebugDraw(ref MatrixD)</c>,
 /// <c>ReindexMaterials(Dictionary&lt;byte, byte&gt)</c>, <c>Intersect(BoundingBoxI, int)</c>, <c>Intersect(ref LineD,
 /// out double, out double)</c>, <c>Close</c>, <c>PostProcess(VrVoxelMesh, MyStorageDataTypeFlags)</c> (VrVoxelMesh:
 /// VRage.NativeWrapper.dll).</para>
 /// <para>The public parameterless constructor makes an inert provider (<c>MyOctreeStorage.ReadDataProvider</c> uses
 /// <c>Activator.CreateInstance</c>; no DE tile is ever saved, so it never runs for us): every read answers empty, every
 /// <c>Intersect</c> Disjoint/false. Never throws into SE.</para>
 /// </summary>
    [MyStorageDataProvider(ProviderId)]
    public sealed class SnTerrainProvider : IMyStorageDataProvider
    {
        public const int ProviderId = TerrainGate.SnProviderId; // 45460

        private readonly ProviderCore _core;
        private int _errorsLogged;

        /// <summary>Inert (see the class remarks).</summary>
        public SnTerrainProvider() { }

        /// <summary>
 /// A tile's provider. Takes its own reference on <paramref name="src"/> (<c>AddRef</c> here; <c>Release</c> once in
 /// <see cref="Close"/>).
 /// </summary>
        internal SnTerrainProvider(SnOctreeSource src, TileSampler sampler, int i, int j, int k, long entityId)
        {
            if (src != null) src.AddRef();
            Action release = null;
            if (src != null) release = src.Release;
            _core = new ProviderCore(sampler, i, j, k, entityId, release);
        }

        internal ProviderCore Core { get { return _core; } }
        public int I { get { return _core != null ? _core.I : -1; } }
        public int J { get { return _core != null ? _core.J : -1; } }
        public int K { get { return _core != null ? _core.K : -1; } }
        public long EntityId { get { return _core != null ? _core.EntityId : 0L; } }
        public bool IsClosed { get { return _core == null || _core.IsClosed; } }

        // ============================================================================================ serialisation

 /// <summary>Nothing is serialised (tiles are never saved; S4.G2-E2 checks the files).</summary>
        public int SerializedSize { get { return 0; } }

        public void WriteTo(Stream stream) { }

        /// <summary>Skips <paramref name="size"/> bytes and stays inert; never throws.</summary>
        public void ReadFrom(int storageVersion, Stream stream, int size, ref bool isOldFormat)
        {
            try
            {
                if (stream == null || size <= 0) return;
                if (stream.CanSeek) stream.Seek(size, SeekOrigin.Current);
                else
                {
                    var buf = new byte[Math.Min(size, 4096)];
                    int left = size;
                    while (left > 0)
                    {
                        int n = stream.Read(buf, 0, Math.Min(left, buf.Length));
                        if (n <= 0) break;
                        left -= n;
                    }
                }
            }
            catch (Exception e) { Error("ReadFrom", e); }
        }

        // ===================================================================================================== reads

 /// <summary>SE's real read path (<c>MyProviderLeaf.ReadRange</c>, <c>TryGetUniformValue</c> with detectOnly).</summary>
        public void ReadRange(ref MyVoxelDataRequest request, bool detectOnly = false)
        {
            try
            {
                MyStorageData t = request.Target;
                byte[] content = null, material = null;
                int baseIndex = 0, sY = 0, sZ = 0;
                if (!detectOnly && t != null)
                {
                    if ((request.RequestedData & MyStorageDataTypeFlags.Content) != 0) content = t[MyStorageDataTypeEnum.Content];
                    if ((request.RequestedData & MyStorageDataTypeFlags.Material) != 0) material = t[MyStorageDataTypeEnum.Material];
                    Vector3I off = request.Offset;
                    baseIndex = t.ComputeLinear(ref off);
                    sY = t.StepY;
                    sZ = t.StepZ;
                }
                int flags = ProviderCore.Read(_core, request.Lod,
                    request.MinInLod.X, request.MinInLod.Y, request.MinInLod.Z, request.MaxInLod.X, request.MaxInLod.Y, request.MaxInLod.Z,
                    (int)request.RequestedData, (int)request.RequestFlags, content, material, baseIndex, sY, sZ, detectOnly || t == null);
                request.Flags = (MyVoxelRequestFlags)flags;
            }
            catch (Exception e)
            {
                // the target may hold a partial write that SE now meshes: counted (STATS readErrors), and the plugin's
 // update turns the fail-safe on at the first one
                request.Flags = request.RequestFlags & ~(MyVoxelRequestFlags)ProviderCore.RfOutputMask;
                ProviderCore.CountReadError();
                Error("ReadRange", e);
            }
        }

        /// <summary>The <c>MyStorageData</c> overload (<c>MyProviderLeaf.GetFilteredValue</c>, our tools): the request path, no flags.</summary>
        public void ReadRange(MyStorageData target, MyStorageDataTypeFlags dataType, ref Vector3I writeOffset, int lodIndex, ref Vector3I minInLod, ref Vector3I maxInLod)
        {
            var req = new MyVoxelDataRequest
            {
                Target = target,
                Offset = writeOffset,
                Lod = lodIndex,
                MinInLod = minInLod,
                MaxInLod = maxInLod,
                RequestedData = dataType,
                RequestFlags = (MyVoxelRequestFlags)0,
            };
            ReadRange(ref req, false);
        }

        /// <summary>The box is in LOD-0 storage voxels, inclusive (VERIFIED <c>MyOctreeStorage.IntersectInternal</c>); <paramref name="lod"/> is a hint.</summary>
        public ContainmentType Intersect(BoundingBoxI box, int lod)
        {
            try
            {
                return (ContainmentType)ProviderCore.IntersectBox(_core, box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z);
            }
            catch (Exception e)
            {
                Error("Intersect(box)", e);
                return ContainmentType.Intersects; // never guess Disjoint/Contains
            }
        }

        /// <summary>Storage-local metres; offsets are fractions of the given line; <paramref name="line"/> becomes the sub-segment.</summary>
        public bool Intersect(ref LineD line, out double startOffset, out double endOffset)
        {
            startOffset = 0;
            endOffset = 0;
            try
            {
                Vector3D f = line.From, t = line.To;
                double s, e;
                if (!ProviderCore.IntersectLine(_core, f.X, f.Y, f.Z, t.X, t.Y, t.Z, out s, out e)) return false;
                startOffset = s;
                endOffset = e;
                Vector3D d = t - f;
                line = new LineD(f + d * s, f + d * e);
                return true;
            }
            catch (Exception ex)
            {
                Error("Intersect(line)", ex);
                startOffset = 0;
                endOffset = 1;
                return true; // conservative: blocked over the whole given line
            }
        }

        // ================================================================================================== no-ops

 /// <summary>Indices are resolved by name every session; never remapped.</summary>
        public void ReindexMaterials(Dictionary<byte, byte> oldToNewIndexMap) { }

        public void PostProcess(VrVoxelMesh mesh, MyStorageDataTypeFlags dataTypes) { }

        public void DebugDraw(ref MatrixD worldMatrix) { }

        /// <summary>Idempotent; releases this instance's reference on the shared source.</summary>
        public void Close()
        {
            try { if (_core != null) _core.Close(); }
            catch (Exception e) { Error("Close", e); }
        }

        private void Error(string where, Exception e)
        {
            if (System.Threading.Interlocked.Increment(ref _errorsLogged) > 3) return;
            DeLog.Info("DE.Terrain WARN provider " + I + "," + J + "," + K + " " + where + " " + e.GetType().Name + ": " + e.Message + " (at most 3 per tile)");
        }
    }
}
