using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>
 /// On every mirror root (the Frame): reports its destruction (quit to menu takes the scene's roots with it, S3.C12),
 /// so the consumer destroys the meshes it created for that entity and forgets the view.
 /// </summary>
    internal sealed class MirrorRootMarker : MonoBehaviour
    {
        public ulong EntityId;
        public Action<MirrorRootMarker> Destroyed;

        private void OnDestroy()
        {
            try
            {
                Action<MirrorRootMarker> d = Destroyed;
                Destroyed = null;
                if (d != null) d(this);
            }
            catch (Exception)
            {
                // never throw from a Unity callback
            }
        }
    }

    /// <summary>One chunk renderer under an Entity (a Unity mesh for one cell × material × colour).</summary>
    internal sealed class ChunkView
    {
        public ChunkKey Key;
        public GameObject Go;
        public MeshFilter Filter;
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public SkyApplier Sky;
        public MaterialSlot Slot;
        public int CreatedFrame, MaterialFrame;
        public int Vertices, Triangles;
        public double Agreement;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public bool HasMesh, C4Logged, C4FinalLogged, FreshBlockSet;
        /// <summary>The chunk's S3.C2 probes as uploaded (drawn Unity-local position + independent SE-local position).</summary>
        public PlacementProbe[] Probes;
        /// <summary>S5: beyond the cull distance (renderer disabled while it has a material); index bytes of the uploaded mesh.</summary>
        public bool Culled;
        public int IndexBytes;
    }

    /// <summary>
 /// A mirrored entity in Unity (CONTRACT §2.5): <c>Frame</c> (root; position o, rotation identity, localScale (s, 1, s);
 /// no SceneCleanerPreserve, so a quit to menu removes it with the scene; S3.M6 adds SceneCleanerPreserve and
 /// DontDestroyOnLoad) → <c>Entity</c> (localRotation q, localScale (k, k, k)) → one child per chunk at the identity,
 /// carrying the mesh in Unity-local (= mirrored SE-local) space, a MeshRenderer on layer 0 with shadows on, and a
 /// SkyApplier whose renderers are filled before its Start (S3.M3: none, and a fresh property block instead).
 /// Main thread only.
 /// </summary>
    internal sealed class MirrorGridView
    {
        public ulong Id;
        public GameObject Frame;
        public Transform FrameT, EntityT;
        public MirrorRootMarker Marker;
        public readonly Dictionary<ChunkKey, ChunkView> Chunks = new Dictionary<ChunkKey, ChunkView>();
        public bool HasPose, Preserved, PlacementLogged, Visible = true;
        public EntityPose LastPose;
        public ulong LastTick;
        public PoseHow LastHow;
        public int CreatedFrame;
        /// <summary>The frame the Frame first became active (a SkyApplier starts only on an active object).</summary>
        public int ActiveFrame = int.MaxValue;
        public ulong NoPoseSinceMs;
        public bool NoPoseLogged;
        /// <summary>
 /// S5: a subgrid's view (D8): its Frame is a child of the root grid's Entity, posed from C2's subpart table
 /// (ownerId = the root, partKey = this id), hidden until its first row.
 /// </summary>
        public bool IsSubgrid;
        public ulong ParentId;
        /// <summary>S5: the Child parts (Dynamic subparts, floating objects) under this view's Entity, by part key.</summary>
        public readonly Dictionary<ulong, ChildPartView> Children = new Dictionary<ulong, ChildPartView>();
        /// <summary>S5: the SE-local row last applied to a subgrid Frame (its tick and alpha), for s5.sub.</summary>
        public readonly float[] LastRow = new float[12];
        public double LastAlpha = 1;

        public bool Alive { get { return Frame != null; } }

        /// <summary>
 /// S5 subgrid: the Frame becomes a child of the root's Entity (Unity-local = Mirror(root SE-local)), posed by the
 /// mirrored relative matrix; the Entity stays at the identity.
 /// </summary>
        public void AttachTo(Transform parent)
        {
            if (FrameT != null && FrameT.parent != parent) FrameT.SetParent(parent, false);
        }

        public void ApplyLocal(Trs t, ulong tick, PoseHow how)
        {
            FrameT.localPosition = new Vector3((float)t.P.X, (float)t.P.Y, (float)t.P.Z);
            FrameT.localRotation = new Quaternion((float)t.Q.X, (float)t.Q.Y, (float)t.Q.Z, (float)t.Q.W);
            FrameT.localScale = new Vector3((float)t.S.X, (float)t.S.Y, (float)t.S.Z);
            EntityT.localPosition = Vector3.zero;
            EntityT.localRotation = Quaternion.identity;
            EntityT.localScale = Vector3.one;
            LastTick = tick;
            LastHow = how;
            if (!HasPose)
            {
                HasPose = true;
                ActiveFrame = Time.frameCount;
                if (Visible && !Frame.activeSelf) Frame.SetActive(true);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static MirrorGridView Create(ulong id, bool preserve, Action<MirrorRootMarker> onDestroyed)
        {
            var v = new MirrorGridView { Id = id, Preserved = preserve, CreatedFrame = Time.frameCount };
            v.Frame = new GameObject("DE.Mirror.Frame." + id.ToString("x", CultureInfo.InvariantCulture));
            v.FrameT = v.Frame.transform;
            v.Frame.layer = 0;
            var entity = new GameObject("Entity");
            entity.layer = 0;
            v.EntityT = entity.transform;
            v.EntityT.SetParent(v.FrameT, false);
            v.Marker = v.Frame.AddComponent<MirrorRootMarker>();
            v.Marker.EntityId = id;
            v.Marker.Destroyed = onDestroyed;
            if (preserve)
            {
                UnityEngine.Object.DontDestroyOnLoad(v.Frame);
                v.Frame.AddComponent<SceneCleanerPreserve>();
            }
            v.Frame.SetActive(false); // shown once a C2 pose arrives
            return v;
        }

        /// <summary>The Frame/Entity split of a pose: Frame (o, identity, (s, 1, s)), Entity (q, (k, k, k)).</summary>
        public void ApplyPose(EntityPose p, ulong tick, PoseHow how, bool rigidFrame)
        {
            float s = rigidFrame ? 1f : (float)p.S;
            FrameT.localPosition = new Vector3((float)p.Ox, (float)p.Oy, (float)p.Oz);
            FrameT.localRotation = Quaternion.identity;
            FrameT.localScale = new Vector3(s, 1f, s);
            EntityT.localPosition = Vector3.zero;
            EntityT.localRotation = new Quaternion((float)p.Qx, (float)p.Qy, (float)p.Qz, (float)p.Qw);
            float k = (float)p.K;
            EntityT.localScale = new Vector3(k, k, k);
            LastPose = p;
            LastTick = tick;
            LastHow = how;
            if (!HasPose)
            {
                HasPose = true;
                ActiveFrame = Time.frameCount;
                if (Visible && !Frame.activeSelf) Frame.SetActive(true);
            }
        }

        public void SetVisible(bool on)
        {
            Visible = on;
            if (Frame != null) Frame.SetActive(on && HasPose);
        }

        /// <summary>: whether the cell already has drawn chunks (a part added there is an edit, not the grid streaming in).</summary>
        public bool HasCellChunks(CellKey cell)
        {
            foreach (KeyValuePair<ChunkKey, ChunkView> kv in Chunks) if (kv.Value.HasMesh && kv.Key.Cell.Equals(cell)) return true;
            return false;
        }

        /// <summary>The chunk's GameObject, renderer and (unless S3.M3) its SkyApplier, created together in this frame.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public ChunkView EnsureChunk(ChunkKey key, bool shadows, bool skyApplier)
        {
            ChunkView c;
            if (Chunks.TryGetValue(key, out c) && c.Go != null) return c;
            c = new ChunkView { Key = key, CreatedFrame = Time.frameCount };
            c.Go = new GameObject(key.ToString());
            c.Go.layer = 0;
            c.Go.transform.SetParent(EntityT, false);
            c.Filter = c.Go.AddComponent<MeshFilter>();
            c.Renderer = c.Go.AddComponent<MeshRenderer>();
            c.Renderer.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            c.Renderer.receiveShadows = true;
            c.Renderer.enabled = false; // until it has a mesh and a material
            if (skyApplier)
            {
                // SkyApplier.Start (next frame) applies the biome sky to `renderers`: fill them now, before Start
                c.Sky = c.Go.AddComponent<SkyApplier>();
                c.Sky.renderers = new Renderer[] { c.Renderer };
                c.Sky.dynamic = true; // the Frame follows P*: SkyApplierUpdater re-applies after a 2 m move
            }
            Chunks[key] = c;
            return c;
        }

        public void RemoveChunk(ChunkKey key, MirrorObjects owned)
        {
            ChunkView c;
            if (!Chunks.TryGetValue(key, out c)) return;
            Chunks.Remove(key);
            DestroyChunk(c, owned);
        }

        private static void DestroyChunk(ChunkView c, MirrorObjects owned)
        {
            try { if (c.Go != null) UnityEngine.Object.Destroy(c.Go); } catch (Exception) { }
            owned.DestroyMesh(c.Mesh);
            c.Mesh = null;
            c.Go = null;
        }

        /// <summary>Destroys every chunk mesh and, when it still exists, the Frame (its children go with it).</summary>
        public void Destroy(MirrorObjects owned)
        {
            foreach (ChunkView c in Chunks.Values) { owned.DestroyMesh(c.Mesh); c.Mesh = null; }
            Chunks.Clear();
            Children.Clear(); // their GameObjects go with the Frame; shared meshes are released by the S5 mirror
            if (Marker != null) Marker.Destroyed = null;
            try { if (Frame != null) UnityEngine.Object.Destroy(Frame); } catch (Exception) { }
            Frame = null;
        }

        /// <summary>The scene took the Frame: forget the chunk objects (already destroyed) and destroy our meshes.</summary>
        public void OnRootDestroyed(MirrorObjects owned)
        {
            foreach (ChunkView c in Chunks.Values) { owned.DestroyMesh(c.Mesh); c.Mesh = null; c.Go = null; }
            Chunks.Clear();
            Children.Clear();
            Frame = null;
        }

        /// <summary>Union of the chunk meshes' Unity-local bounds; false when nothing is built.</summary>
        public bool LocalBounds(out Vec3 min, out Vec3 max)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            bool any = false;
            foreach (ChunkView c in Chunks.Values)
            {
                if (!c.HasMesh) continue;
                any = true;
                x0 = Math.Min(x0, c.MinX); y0 = Math.Min(y0, c.MinY); z0 = Math.Min(z0, c.MinZ);
                x1 = Math.Max(x1, c.MaxX); y1 = Math.Max(y1, c.MaxY); z1 = Math.Max(z1, c.MaxZ);
            }
            min = new Vec3(x0, y0, z0);
            max = new Vec3(x1, y1, z1);
            return any;
        }

        /// <summary>A renderer with a mesh (the first), for its localToWorldMatrix.</summary>
        public MeshRenderer AnyRenderer()
        {
            foreach (ChunkView c in Chunks.Values) if (c.HasMesh && c.Renderer != null) return c.Renderer;
            return null;
        }

        /// <summary>
 /// S3.C2: the 8 corners as drawn: per corner the probe vertex with the most extreme
 /// independent SE-local position over the built chunks, its uploaded position mapped by that chunk's own
 /// renderer.localToWorldMatrix. False when nothing is built.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool DrawnCorners(Vec3[] seLocal, Vec3[] world)
        {
            var probes = new List<PlacementProbe[]>();
            var maps = new List<Func<Vec3, Vec3>>();
            foreach (ChunkView c in Chunks.Values)
            {
                if (!c.HasMesh || c.Renderer == null || c.Probes == null) continue;
                Matrix4x4 m = c.Renderer.localToWorldMatrix;
                probes.Add(c.Probes);
                maps.Add(u =>
                {
                    Vector3 w = m.MultiplyPoint3x4(new Vector3((float)u.X, (float)u.Y, (float)u.Z));
                    return new Vec3(w.x, w.y, w.z);
                });
            }
            return probes.Count > 0 && MeshBuilder.ProbeCorners(probes, maps, seLocal, world);
        }

        public int Vertices
        {
            get { int n = 0; foreach (ChunkView c in Chunks.Values) if (c.HasMesh) n += c.Vertices; return n; }
        }

        public int Triangles
        {
            get { int n = 0; foreach (ChunkView c in Chunks.Values) if (c.HasMesh) n += c.Triangles; return n; }
        }
    }

    /// <summary>
 /// Every Unity object the mirror creates (meshes, materials, textures, render textures), so that teardown and the
 /// S3.C12 count ("our mesh count back to baseline") are exact: created, destroyed explicitly, counted alive.
 /// </summary>
    internal sealed class MirrorObjects
    {
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<UnityEngine.Object> _other = new List<UnityEngine.Object>();
        public long MeshesCreated, MeshesDestroyed, OthersCreated, OthersDestroyed;

        public Mesh NewMesh(string name)
        {
            var m = new Mesh { name = name };
            _meshes.Add(m);
            MeshesCreated++;
            return m;
        }

        public T Track<T>(T o) where T : UnityEngine.Object
        {
            if (o != null) { _other.Add(o); OthersCreated++; }
            return o;
        }

        public void DestroyMesh(Mesh m)
        {
            if (m == null) { if (!ReferenceEquals(m, null)) _meshes.Remove(m); return; }
            _meshes.Remove(m);
            MeshesDestroyed++;
            try { UnityEngine.Object.Destroy(m); } catch (Exception) { }
        }

        public void Destroy(UnityEngine.Object o)
        {
            if (ReferenceEquals(o, null)) return;
            _other.Remove(o);
            if (o == null) return;
            OthersDestroyed++;
            try { UnityEngine.Object.Destroy(o); } catch (Exception) { }
        }

        public int MeshesAlive
        {
            get { int n = 0; foreach (Mesh m in _meshes) if (m != null) n++; return n; }
        }

        public int OthersAlive
        {
            get { int n = 0; foreach (UnityEngine.Object o in _other) if (o != null) n++; return n; }
        }

        public void DestroyAllMeshes()
        {
            foreach (Mesh m in _meshes.ToArray()) DestroyMesh(m);
            _meshes.Clear();
        }

        public void DestroyAllOthers()
        {
            foreach (UnityEngine.Object o in _other.ToArray()) Destroy(o);
            _other.Clear();
        }
    }

    /// <summary>
 /// Uploads a built chunk with the Unity 2019.4 mesh API (ARCHITECTURE §5.8; verified present in Subnautica's
 /// UnityEngine.CoreModule): SetVertexBufferParams (Position f32×3, Normal f32×3, Tangent f32×4, TexCoord0 f32×2 in
 /// stream 0), SetVertexBufferData, SetIndexBufferParams (UInt16, or UInt32 above 65,535 vertices),
 /// SetIndexBufferData, one SetSubMesh (triangles; Default flags so Unity fills the submesh's vertex range and
 /// bounds), then the mesh bounds from the build pass.
 /// </summary>
    internal static class MeshUpload
    {
        private static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4, 0),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0),
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Upload(Mesh mesh, ChunkMeshData c)
        {
            // S5: the Mesh object is reused (Clear keeps the object; CONTRACT §5.4 "Subnautica uploads")
            mesh.Clear();
            mesh.SetVertexBufferParams(c.VertexCount, Layout);
            mesh.SetVertexBufferData(c.Vertices, 0, 0, c.VertexCount, 0, MeshUpdateFlags.DontRecalculateBounds);
            if (c.Use32)
            {
                mesh.SetIndexBufferParams(c.IndexCount, IndexFormat.UInt32);
                mesh.SetIndexBufferData(c.Indices32, 0, 0, c.IndexCount, MeshUpdateFlags.DontRecalculateBounds);
            }
            else
            {
                mesh.SetIndexBufferParams(c.IndexCount, IndexFormat.UInt16);
                mesh.SetIndexBufferData(c.Indices16, 0, 0, c.IndexCount, MeshUpdateFlags.DontRecalculateBounds);
            }
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, c.IndexCount, MeshTopology.Triangles), MeshUpdateFlags.Default); // computes the submesh vertex range and bounds
            var centre = new Vector3((c.MinX + c.MaxX) * 0.5f, (c.MinY + c.MaxY) * 0.5f, (c.MinZ + c.MaxZ) * 0.5f);
            var size = new Vector3(c.MaxX - c.MinX, c.MaxY - c.MinY, c.MaxZ - c.MinZ);
            mesh.bounds = new Bounds(centre, size);
        }
    }
}
