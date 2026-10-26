using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DeepEngineers.Wire;
using VRage;
using VRage.FileSystem;
using VRage.Game.Models;
using VRage.Import;
using VRageMath;
using VRageMath.PackedVector;
using VRageRender.Import;
using VRageRender.Models;

namespace DeepEngineers.SE
{
    /// <summary>A model the producer asked for (one per model key).</summary>
    internal sealed class ModelLoadRequest
    {
        public const int KindModel = 0, KindSkinVariant = 1, KindCharacter = 2;

        public ulong ModelKey;
        public string AssetName;
        public float Scale = 1f;
        public uint WorldEpoch;
        /// <summary>S5: 0 a model (S3), 1 a skin variant of <see cref="BaseModel"/> (no reload), 2 the player's character.</summary>
        public int Kind;
        /// <summary>S5 kind 1: the loaded base model whose geometry the variant shares.</summary>
        public ExportedModel BaseModel;
        /// <summary>S5 kinds 1 and 2: the skin's texture changes by material name (raw names); null = none.</summary>
        public Dictionary<string, SkinTextureChange> Skin;
        public string SkinText;
        /// <summary>S5 kind 2: MyCharacterDefinition.MaterialsDisabledIn1st, the |char=body and |char=head1st keys.</summary>
        public string[] HeadMaterials;
        public ulong BodyKey, HeadKey;
        public long CharacterId;
        public bool Fallback;
        public string FallbackReason;
    }

    /// <summary>The worker's answer: the model and its C3 messages, ready to queue on the update thread.</summary>
    internal sealed class ModelLoadResult
    {
        public ModelLoadRequest Request;
        public ExportedModel Model;
        public List<OutMessage> Messages;
        public long PayloadBytes;
        public string Error;
        /// <summary>S5 character: the head part (null when the model has no head material) and the bone map.</summary>
        public ExportedModel Head;
        public List<OutMessage> HeadMessages;
        public long HeadPayloadBytes;
        public int[] CanonToModel;
        public int ModelBones;
        /// <summary>Non-fatal notes (skin overrides dropped, scale,...), logged by the update thread.</summary>
        public string Notes;
    }

    /// <summary>
 /// The model loader (ROADMAP S3 "SE plugin (S3b)", ARCHITECTURE §5.8): one dedicated background thread
 /// ("DE.ModelLoader", below normal priority) that loads each requested model through SE's own loader and serialises
 /// it into its C3 messages, so the update thread only copies bytes into the ring (never waits: requests and results
 /// go through queues).
 /// <para>Game members: <c>new MyModel(string
 /// assetName)</c>; <c>LoadUV</c> (setter, read by <c>LoadGeometryData</c>); <c>LoadData(bool forceFullDetail)</c> with
 /// <c>true</c>: LOD0 geometry, from the <c>GeometryDataAsset</c> the stub.mwm names (SquarePlate.mwm →
 /// SquarePlate_LOD0.mwm, read with tools); Havok collision shapes are only built when
 /// <c>HkBaseSystem.IsThreadInitialized</c>, which is false on this thread, so none are created (VERIFIED: the check
 /// precedes <c>HkShapeLoader.LoadShapesListFromBuffer</c>); <c>GetVerticesCount</c>, <c>GetTrianglesCount</c>,
 /// <c>GetVertex(int)</c>, <c>GetVertexNormal(int)</c> (model space, unpacked), <c>TexCoords</c> (<c>HalfVector2[]</c>,
 /// <c>HalfVector2.ToVector2</c>), <c>Triangles</c> (<c>MyTriangleVertexIndices</c> I0/I1/I2: the file's second and
 /// third index swapped by <c>CopyTriangleIndices</c>; <c>Indices</c>/<c>Indices16</c> are null after loading),
 /// <c>GetMeshList</c> (<c>MyMesh.TriStart</c>, <c>TriCount</c>, <c>Material</c>: <c>MyMeshMaterial.Name</c>,
 /// <c>DrawTechnique</c> (<c>MyMeshDrawTechnique</c>, byte enum), <c>GlassCW</c>, <c>GlassCCW</c>, <c>Textures</c> with the
 /// keys ColorMetalTexture, NormalGlossTexture, AddMapsTexture, AlphamaskTexture), <c>PatternScale</c>,
 /// <c>AssetName</c>. Tangents and binormals: <c>MyModel.GetVertexTangent</c> imports "Tangents" from the stub asset,
 /// which has none for SquarePlate (VERIFIED with tools: the tags live in SquarePlate_LOD0.mwm), so this loader reads
 /// "Tangents" and "Binormals" itself from the geometry asset with its own <c>MyModelImporter</c>
 /// (<c>ImportData(string, string[])</c>, <c>GetTagData</c> → <c>Byte4[]</c>, unpacked with
 /// <c>VF_Packer.UnpackNormal(Byte4)</c>, the unpacking MyModel uses for normals and tangents); the geometry asset path
 /// is resolved the way <c>LoadData(true)</c> does (tag "GeometryDataAsset" → <c>MyLODDescriptor.GetModelAbsoluteFilePath</c>).
 /// Fallback when the asset has no such tags: <c>GetVertexTangent</c>, and binormal = normal × tangent (logged).
 /// Texture paths: rooted names as they are, else <c>Path.Combine(MyFileSystem.ContentPath, name)</c>, checked with
 /// <c>MyFileSystem.FileExists</c>.</para>
 /// <para>Never touches the shared <c>MyModels</c> instances (they hold LOD1 geometry and may be unloaded by SE).</para>
 /// <para>S5 (CONTRACT §5.4 "Model keys and skins", "Character"): a request of kind 1 builds a skin variant from an
 /// already loaded model (no reload; the skin's texture changes by material name, an unresolved texture keeping the
 /// original), kind 2 exports the player's character: <c>LoadData(true)</c>, <c>LoadAnimationData(true)</c> when
 /// <c>Bones</c> is null, the bones mapped to canon1 by name (Wire <c>SkeletonMap</c>), the blend weights of
 /// <c>GetBoneIndicesWeights(int)</c> remapped (Wire <c>SkinMath.RemapInfluences</c>), SkinnedBind = inverse absolute
 /// binds (<c>SkinMath.AbsoluteFromLocal</c>/<c>SkinnedBindRows</c>), skins applied, split into |char=body and
 /// |char=head1st (<see cref="CharacterSplit"/>). Members: <c>MyModel.Bones</c> (<c>MyModelBone[]</c>: <c>Name</c>,
 /// <c>Parent</c>, <c>Transform</c>), <c>LoadAnimationData(bool)</c>, <c>GetBoneIndicesWeights(int)</c> →
 /// <c>MyTriangle_BoneIndicesWeigths?</c> (<c>Vertex0/1/2</c>: <c>Indices</c> <c>Vector4UByte</c>, <c>Weights</c>
 /// <c>Vector4</c>), <c>ScaleFactor</c>.</para>
 /// </summary>
    internal sealed class ModelLoader
    {
        private readonly object _lock = new object();
        private readonly Queue<ModelLoadRequest> _requests = new Queue<ModelLoadRequest>();
        private readonly ConcurrentQueue<ModelLoadResult> _results = new ConcurrentQueue<ModelLoadResult>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private MyModelImporter _importer;
        private int _nextGroup = 1;

        public int ChunkBytes = S3Defaults.ChunkBytes;
        public long Loaded { get; private set; }
        public long Failed { get; private set; }
        public int Pending { get { lock (_lock) return _requests.Count; } }

        /// <summary>Queues a load (update thread; never blocks). The thread starts on the first request.</summary>
        public void Request(ModelLoadRequest r)
        {
            lock (_lock)
            {
                _requests.Enqueue(r);
                if (_thread == null)
                {
                    _thread = new Thread(Run) { Name = "DE.ModelLoader", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                    _thread.Start();
                }
            }
            _wake.Set();
        }

        public bool TryTakeResult(out ModelLoadResult r) { return _results.TryDequeue(out r); }

        private void Run()
        {
            DeLog.Info("DE.S3 loader thread started (managed id " + Thread.CurrentThread.ManagedThreadId + "; not Havok-initialised: MyModel.LoadData builds no collision shapes here)");
            while (true)
            {
                ModelLoadRequest r = null;
                lock (_lock) { if (_requests.Count > 0) r = _requests.Dequeue(); }
                if (r == null) { _wake.WaitOne(1000); continue; }
                var res = new ModelLoadResult { Request = r };
                try
                {
                    if (r.Kind == ModelLoadRequest.KindCharacter) LoadCharacter(r, res);
                    else
                    {
                        res.Model = r.Kind == ModelLoadRequest.KindSkinVariant ? SkinVariant(r, res) : Load(r);
                        long payloadBytes;
                        res.Messages = S3Codec.ModelMessages(res.Model, ChunkBytes, NextGroup(), out payloadBytes);
                        res.PayloadBytes = payloadBytes;
                    }
                    Loaded++;
                }
                catch (Exception e)
                {
                    res.Error = e.GetType().Name + ": " + e.Message;
                    res.Model = null;
                    res.Messages = null;
                    Failed++;
                }
                _results.Enqueue(res);
            }
        }

        private int NextGroup()
        {
            int g = _nextGroup++;
            if (_nextGroup == int.MaxValue) _nextGroup = 1;
            return g;
        }

        /// <summary>Worker thread: one model through SE's loader into SE-space arrays.</summary>
        private ExportedModel Load(ModelLoadRequest r)
        {
            MyModel model;
            string full;
            return LoadGeometry(r.AssetName, r.ModelKey, r.Scale, out model, out full);
        }

        /// <summary>Worker thread: the geometry, UVs, tangents, triangles and submeshes (with material names) of one model.</summary>
        private ExportedModel LoadGeometry(string assetName, ulong modelKey, float scale, out MyModel model, out string full)
        {
            long t0 = Stopwatch.GetTimestamp();
            string asset = assetName;
            if (string.IsNullOrEmpty(asset)) throw new ArgumentException("empty asset name");
            full = Path.IsPathRooted(asset) ? asset : Path.Combine(MyFileSystem.ContentPath, asset);
            if (!MyFileSystem.FileExists(full)) throw new FileNotFoundException("model file not found (MyModel would load Models\\Debug\\Error.mwm instead)", full);

            model = new MyModel(asset) { LoadUV = true };
            model.LoadData(true);
            int v = model.GetVerticesCount();
            MyTriangleVertexIndices[] tris = model.Triangles;
            if (v <= 0 || tris == null || tris.Length == 0) throw new InvalidDataException("no geometry (vertices " + v + ", triangles " + (tris == null ? "null" : tris.Length.ToString()) + ")");

            var m = new ExportedModel
            {
                AssetName = asset,
                ModelKey = modelKey,
                Lod = 0,
                PatternScale = model.PatternScale,
                Scale = scale,
                VertexCount = v,
                SeVertexCount = v,
                SeTriangleCount = model.GetTrianglesCount(),
                Positions = new float[3 * v],
                Normals = new float[3 * v],
                Tangents = new float[3 * v],
                Binormals = new float[3 * v],
                Uvs = new float[2 * v],
            };
            for (int i = 0; i < v; i++)
            {
                Vector3 p = model.GetVertex(i);
                Vector3 n = model.GetVertexNormal(i);
                m.Positions[3 * i] = p.X * scale; m.Positions[3 * i + 1] = p.Y * scale; m.Positions[3 * i + 2] = p.Z * scale;
                m.Normals[3 * i] = n.X; m.Normals[3 * i + 1] = n.Y; m.Normals[3 * i + 2] = n.Z;
            }
            HalfVector2[] uv = model.TexCoords;
            if (uv != null && uv.Length == v)
                for (int i = 0; i < v; i++)
                {
                    Vector2 t = uv[i].ToVector2();
                    m.Uvs[2 * i] = t.X; m.Uvs[2 * i + 1] = t.Y;
                }
            else DeLog.Info("DE.S3 loading " + asset + ": no UVs (TexCoords " + (uv == null ? "null" : uv.Length.ToString()) + " for " + v + " vertices): zeros sent");

            FillTangents(model, full, m);

            m.Indices = new int[3 * tris.Length];
            for (int t = 0; t < tris.Length; t++)
            {
                m.Indices[3 * t] = tris[t].I0;
                m.Indices[3 * t + 1] = tris[t].I1;
                m.Indices[3 * t + 2] = tris[t].I2;
            }

            List<MyMesh> meshes = model.GetMeshList();
            var subs = new List<ExportedSubmesh>();
            var mats = new List<MaterialInfo>();
            var byKey = new Dictionary<ulong, MaterialInfo>();
            if (meshes != null)
                foreach (MyMesh mesh in meshes)
                {
                    MaterialInfo mi = ResolveMaterial(mesh.Material, asset);
                    if (!byKey.ContainsKey(mi.Key)) { byKey[mi.Key] = mi; mats.Add(mi); }
                    subs.Add(new ExportedSubmesh { MaterialKey = mi.Key, TriStart = mesh.TriStart, TriCount = mesh.TriCount, DrawTechnique = mi.DrawTechnique,
                                                   MaterialName = mesh.Material != null ? mesh.Material.Name : null });
                }
            if (subs.Count == 0)
            {
                // a model without mesh parts (should not happen after LoadData): one default submesh over everything
                var mi = ResolveMaterial(null, asset);
                mats.Add(mi);
                subs.Add(new ExportedSubmesh { MaterialKey = mi.Key, TriStart = 0, TriCount = tris.Length, DrawTechnique = mi.DrawTechnique });
            }
            m.Submeshes = subs.ToArray();
            m.Materials = mats.ToArray();
            string bad = m.Validate();
            if (bad != null) throw new InvalidDataException(bad);
            m.LoadMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            return m;
        }

        // ------------------------------------------------------------------------------------------------- S5

 /// <summary>
 /// Worker thread, S5 kind 1: a skin variant of an already loaded model (CONTRACT §5.4 "Model keys and skins"): the
 /// same geometry arrays, each submesh whose material name the skin changes gets a new material (a new content key);
 /// a texture that does not resolve drops that override only (noted).
 /// </summary>
        private ExportedModel SkinVariant(ModelLoadRequest r, ModelLoadResult res)
        {
            if (r.BaseModel == null) throw new ArgumentException("skin variant without its base model");
            long t0 = Stopwatch.GetTimestamp();
            var notes = new List<string>();
            ExportedModel m = SkinResolve.Variant(r.BaseModel, r.ModelKey, r.Skin, (orig, ch) => ApplySkin(orig, ch, notes));
            m.Scale = r.BaseModel.Scale;
            m.LoadMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (notes.Count > 0) res.Notes = "skin " + r.SkinText + ": " + string.Join("; ", notes.ToArray());
            string bad = m.Validate();
            if (bad != null) throw new InvalidDataException(bad);
            return m;
        }

        /// <summary>One material under a skin's texture change; an unresolved texture keeps the original (noted).</summary>
        internal static MaterialInfo ApplySkin(MaterialInfo orig, SkinTextureChange ch, List<string> notes)
        {
            string cm = orig.ColorMetalRaw, ng = orig.NormalGlossRaw, add = orig.AddMapsRaw, alpha = orig.AlphaMaskRaw;
            int changed = 0;
            Pick(ref cm, ch.ColorMetal, "cm", orig.Name, notes, ref changed);
            Pick(ref ng, ch.NormalGloss, "ng", orig.Name, notes, ref changed);
            Pick(ref add, ch.Extensions, "add", orig.Name, notes, ref changed);
            Pick(ref alpha, ch.AlphaMask, "alpha", orig.Name, notes, ref changed);
            if (changed == 0) return orig;
            uint flags = orig.Flags;
            if (!string.IsNullOrEmpty(alpha)) flags |= MaterialFlags.AlphaMasked;
            return FromRaw(orig.Name, cm, ng, add, alpha, orig.DrawTechnique, flags);
        }

        private static void Pick(ref string raw, string over, string what, string material, List<string> notes, ref int changed)
        {
            if (string.IsNullOrEmpty(over)) return;
            string missing = "";
            string resolved = Resolve(over, ref missing);
            if (resolved.Length == 0)
            {
                if (notes != null) notes.Add(material + " " + what + " override " + over + " not found (kept " + (raw ?? "none") + ")");
                return;
            }
            raw = over;
            changed++;
        }

        /// <summary>
 /// Worker thread, S5 kind 2: the player's character (CONTRACT §5.4 "Character"): loaded as every model (LoadUV,
 /// LoadData(true); <c>LoadAnimationData(true)</c> when <c>Bones</c> is null, as <c>MySkinnedEntity.ObtainBones</c> does),
 /// its bones mapped to canon1 by name, blend weights from <c>MyModel.GetBoneIndicesWeights(int)</c> per triangle
 /// (Vertex0/1/2 = I0/I1/I2) remapped to canonical indices, SkinnedBind = inverse(absolute bind), the skin's
 /// overrides applied, then split by MaterialsDisabledIn1st into |char=body and |char=head1st. Throws on any failure
 /// (the update thread then exports the default Engineer).
 /// </summary>
        private void LoadCharacter(ModelLoadRequest r, ModelLoadResult res)
        {
            MyModel model;
            string full;
            ExportedModel all = LoadGeometry(r.AssetName, r.BodyKey, 1f, out model, out full);
            if (model.Bones == null) model.LoadAnimationData(true);
            MyModelBone[] bones = model.Bones;
            if (bones == null || bones.Length == 0) throw new InvalidDataException("no bones (Bones null after LoadAnimationData)");
            int nb = bones.Length;
            var names = new string[nb];
            var parents = new int[nb];
            var local = new SeMatrix[nb];
            for (int i = 0; i < nb; i++)
            {
                MyModelBone b = bones[i];
                if (b == null) throw new InvalidDataException("bone " + i + " is null");
                names[i] = b.Name;
                parents[i] = b.Parent;
                Matrix t = b.Transform;
                local[i] = new SeMatrix(new Vec3(t.M11, t.M12, t.M13), new Vec3(t.M21, t.M22, t.M23), new Vec3(t.M31, t.M32, t.M33), new Vec3(t.M41, t.M42, t.M43));
            }
            // canon1 by name with the canonical parents (Wire SkinMath, CONTRACT §5.4 "Character")
            SkeletonMap map;
            string why;
            if (!SkeletonMap.TryBuild(names, parents, out map, out why)) throw new InvalidDataException("skeleton: " + why);

            int v = all.VertexCount;
            var mIdx = new byte[4 * v];
            var mW = new float[4 * v];
            MyTriangleVertexIndices[] tris = model.Triangles;
            for (int t = 0; t < tris.Length; t++)
            {
                MyTriangle_BoneIndicesWeigths? bw = model.GetBoneIndicesWeights(t);
                if (!bw.HasValue) throw new InvalidDataException("no blend data (GetBoneIndicesWeights returned null)");
                MyTriangle_BoneIndicesWeigths tw = bw.Value;
                PutWeights(mIdx, mW, tris[t].I0, tw.Vertex0);
                PutWeights(mIdx, mW, tris[t].I1, tw.Vertex1);
                PutWeights(mIdx, mW, tris[t].I2, tw.Vertex2);
            }
            // weights on a non-canonical bone move to its nearest canonical ancestor; merged, 4 largest kept, renormalised
            all.BoneIndices = new byte[4 * v];
            all.BoneWeights = new float[4 * v];
            for (int i = 0; i < v; i++)
                if (!SkinMath.RemapInfluences(map, mIdx, mW, 4 * i, 4, all.BoneIndices, all.BoneWeights, 4 * i))
                    throw new InvalidDataException("vertex " + i + " weights a bone outside the model's " + nb + " bones");
            // SkinnedBind[c] = inverse(absolute bind of canonical bone c) = SE's SkinTransform (abs = local × parentAbs)
            var abs = new SeMatrix[nb];
            SkinMath.AbsoluteFromLocal(local, parents, abs);
            all.SkinnedBind = SkinMath.SkinnedBindRows(map, abs);
            int[] canonToModel = map.CanonicalToModel;

            var notes = new List<string>();
            if (model.ScaleFactor != 1f)
                notes.Add("model ScaleFactor " + model.ScaleFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " ignored (bones and vertices are both unscaled)");
            if (r.Skin != null && r.Skin.Count > 0)
            {
                if (SkinResolve.Overrides(all, r.Skin)) all = SkinResolve.Variant(all, r.BodyKey, r.Skin, (orig, ch) => ApplySkin(orig, ch, notes));
                else notes.Add("skin " + r.SkinText + " changes no material of " + r.AssetName);
            }
            ExportedModel body, head;
            CharacterSplit.Split(all, r.HeadMaterials, out body, out head);
            if (body == null) throw new InvalidDataException("no body submeshes (every material is in MaterialsDisabledIn1st)");
            body.ModelKey = r.BodyKey;
            string bad = body.Validate();
            if (bad != null) throw new InvalidDataException("body: " + bad);
            long bytes;
            res.Model = body;
            res.Messages = S3Codec.ModelMessages(body, ChunkBytes, NextGroup(), out bytes);
            res.PayloadBytes = bytes;
            if (head != null)
            {
                head.ModelKey = r.HeadKey;
                bad = head.Validate();
                if (bad != null) throw new InvalidDataException("head: " + bad);
                res.Head = head;
                res.HeadMessages = S3Codec.ModelMessages(head, ChunkBytes, NextGroup(), out bytes);
                res.HeadPayloadBytes = bytes;
            }
            else notes.Add("no MaterialsDisabledIn1st material in the model: no head1st part");
            res.CanonToModel = canonToModel;
            res.ModelBones = nb;
            if (notes.Count > 0) res.Notes = string.Join("; ", notes.ToArray());
        }

        private static void PutWeights(byte[] idx, float[] w, int vertex, MyVertex_BoneIndicesWeights b)
        {
            int o = 4 * vertex;
            if (o < 0 || o + 3 >= idx.Length) return;
            idx[o] = b.Indices.X; idx[o + 1] = b.Indices.Y; idx[o + 2] = b.Indices.Z; idx[o + 3] = b.Indices.W;
            w[o] = b.Weights.X; w[o + 1] = b.Weights.Y; w[o + 2] = b.Weights.Z; w[o + 3] = b.Weights.W;
        }

        /// <summary>Tangents and binormals from the geometry asset (see the class remarks), else the fallbacks.</summary>
        private void FillTangents(MyModel model, string fullAsset, ExportedModel m)
        {
            int v = m.VertexCount;
            Byte4[] tan = null, bin = null;
            string geom = fullAsset;
            try
            {
                if (_importer == null) _importer = new MyModelImporter();
                _importer.ImportData(fullAsset, new[] { "GeometryDataAsset" });
                Dictionary<string, object> tags = _importer.GetTagData();
                object g;
                if (tags != null && tags.TryGetValue("GeometryDataAsset", out g) && g is string && ((string)g).Length > 0)
                {
                    string resolved = new MyLODDescriptor { Model = (string)g }.GetModelAbsoluteFilePath(fullAsset);
                    if (!string.IsNullOrEmpty(resolved)) geom = resolved;
                }
                _importer.Clear();
                _importer.ImportData(geom, new[] { "Tangents", "Binormals" });
                tags = _importer.GetTagData();
                object o;
                if (tags != null && tags.TryGetValue("Tangents", out o)) tan = o as Byte4[];
                if (tags != null && tags.TryGetValue("Binormals", out o)) bin = o as Byte4[];
                _importer.Clear();
            }
            catch (Exception e)
            {
                DeLog.Info("DE.S3 loading " + m.AssetName + ": tangent import from " + geom + " failed (" + e.GetType().Name + ": " + e.Message + "); fallback");
                try { if (_importer != null) _importer.Clear(); } catch (Exception) { }
                tan = null; bin = null;
            }

            if (tan != null && tan.Length == v)
            {
                for (int i = 0; i < v; i++)
                {
                    Vector3 t = VF_Packer.UnpackNormal(tan[i]);
                    m.Tangents[3 * i] = t.X; m.Tangents[3 * i + 1] = t.Y; m.Tangents[3 * i + 2] = t.Z;
                }
                m.HasTangents = true;
                m.TangentSource = "geometry asset " + Path.GetFileName(geom);
            }
            else
            {
                // the model file itself, imported once
                Byte4[] own = null;
                if (!string.Equals(geom, fullAsset, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _importer.ImportData(fullAsset, new[] { "Tangents" });
                        Dictionary<string, object> tags2 = _importer.GetTagData();
                        object o2;
                        if (tags2 != null && tags2.TryGetValue("Tangents", out o2)) own = o2 as Byte4[];
                        _importer.Clear();
                    }
                    catch (Exception e)
                    {
                        DeLog.Info("DE.S3 loading " + m.AssetName + ": tangent import from the model file failed (" + e.GetType().Name + ": " + e.Message + ")");
                        try { _importer.Clear(); } catch (Exception) { }
                        own = null;
                    }
                }
                if (own != null && own.Length == v)
                {
                    for (int i = 0; i < v; i++)
                    {
                        Vector3 t = VF_Packer.UnpackNormal(own[i]);
                        m.Tangents[3 * i] = t.X; m.Tangents[3 * i + 1] = t.Y; m.Tangents[3 * i + 2] = t.Z;
                    }
                    m.HasTangents = true;
                    m.TangentSource = "model file " + Path.GetFileName(fullAsset);
                }
                else
                {
                    for (int i = 0; i < 3 * v; i++) m.Tangents[i] = 0f;
                    m.HasTangents = false;
                    m.TangentSource = "none (zeros)";
                }
            }

            if (bin != null && bin.Length == v)
            {
                for (int i = 0; i < v; i++)
                {
                    Vector3 b = VF_Packer.UnpackNormal(bin[i]);
                    m.Binormals[3 * i] = b.X; m.Binormals[3 * i + 1] = b.Y; m.Binormals[3 * i + 2] = b.Z;
                }
                m.TangentSource += ", binormals from the asset";
            }
            else
            {
                for (int i = 0; i < v; i++)
                {
                    var n = new Vector3(m.Normals[3 * i], m.Normals[3 * i + 1], m.Normals[3 * i + 2]);
                    var t = new Vector3(m.Tangents[3 * i], m.Tangents[3 * i + 1], m.Tangents[3 * i + 2]);
                    Vector3 b = Vector3.Cross(n, t);
                    m.Binormals[3 * i] = b.X; m.Binormals[3 * i + 1] = b.Y; m.Binormals[3 * i + 2] = b.Z;
                }
                m.TangentSource += ", binormals derived (normal x tangent; handedness HYPOTHESIS)";
            }
        }

        /// <summary>A mesh material with absolute texture paths (vanilla names are Content-relative).</summary>
        internal static MaterialInfo ResolveMaterial(MyMeshMaterial mat, string asset)
        {
            var mi = new MaterialInfo { Name = mat != null ? (mat.Name ?? "") : "(none)" };
            Dictionary<string, string> tex = mat != null ? mat.Textures : null;
            mi.ColorMetalRaw = Get(tex, "ColorMetalTexture");
            mi.NormalGlossRaw = Get(tex, "NormalGlossTexture");
            mi.AddMapsRaw = Get(tex, "AddMapsTexture");
            mi.AlphaMaskRaw = Get(tex, "AlphamaskTexture");
            MyMeshDrawTechnique tech = mat != null ? mat.DrawTechnique : MyMeshDrawTechnique.MESH;
            uint flags = 0;
            if (mat != null && !string.IsNullOrEmpty(mat.GlassCW)) flags |= MaterialFlags.GlassCw;
            if (mat != null && !string.IsNullOrEmpty(mat.GlassCCW)) flags |= MaterialFlags.GlassCcw;

            // GLASS technique with no GlassCW/GlassCCW names, so the pane never reached Subnautica as glass; the technique
 // alone marks glass now (Subnautica then clones its vanilla glass material for the pane)
            if (tech == MyMeshDrawTechnique.GLASS && (flags & (MaterialFlags.GlassCw | MaterialFlags.GlassCcw)) == 0) flags |= MaterialFlags.GlassCw;
            if (!string.IsNullOrEmpty(mi.AlphaMaskRaw) || tech == MyMeshDrawTechnique.ALPHA_MASKED || tech == MyMeshDrawTechnique.ALPHA_MASKED_SINGLE_SIDED)
                flags |= MaterialFlags.AlphaMasked;
            return FromRaw(mi.Name, mi.ColorMetalRaw, mi.NormalGlossRaw, mi.AddMapsRaw, mi.AlphaMaskRaw, (uint)tech, flags, S13SeRules.MwmContentPath(asset)); // S13 hook (point 3): a mod's model (custom skins) finds its textures in its own content root, as SE does
        }

        /// <summary>A material from its raw texture names (Content-relative or absolute), resolved and keyed (S3 rules; S5 skins).</summary>
        internal static MaterialInfo FromRaw(string name, string cmRaw, string ngRaw, string addRaw, string alphaRaw, uint technique, uint flags, string contentRoot = null)
        {
            var mi = new MaterialInfo
            {
                Name = name ?? "",
                ColorMetalRaw = cmRaw, NormalGlossRaw = ngRaw, AddMapsRaw = addRaw, AlphaMaskRaw = alphaRaw,
                DrawTechnique = technique,
                Flags = flags,
            };
            string missing = "";
            bool viaRoot = false;
            mi.ColorMetal = Resolve(mi.ColorMetalRaw, ref missing, contentRoot, ref viaRoot);
            mi.NormalGloss = Resolve(mi.NormalGlossRaw, ref missing, contentRoot, ref viaRoot);
            mi.AddMaps = Resolve(mi.AddMapsRaw, ref missing, contentRoot, ref viaRoot);
            mi.AlphaMask = Resolve(mi.AlphaMaskRaw, ref missing, contentRoot, ref viaRoot);
            mi.Missing = missing;
            // S13: a material resolved through a mod's root is keyed by its full paths (two mods with the same relative names differ)
            mi.Key = viaRoot ? S3Keys.MaterialKey(mi.ColorMetal, mi.NormalGloss, mi.AddMaps, mi.AlphaMask, mi.DrawTechnique, flags)
                             : S3Keys.MaterialKey(mi.ColorMetalRaw, mi.NormalGlossRaw, mi.AddMapsRaw, mi.AlphaMaskRaw, mi.DrawTechnique, flags);
            return mi;
        }

        private static string Get(Dictionary<string, string> tex, string key)
        {
            string v;
            return tex != null && tex.TryGetValue(key, out v) && !string.IsNullOrEmpty(v) ? v : null;
        }

        /// <summary>S13 (point 3): SE's renderer rule for a model with its own content root (S13SeRules.TextureFullPath); else the Content rule.</summary>
        private static string Resolve(string raw, ref string missing, string contentRoot, ref bool viaRoot)
        {
            if (string.IsNullOrEmpty(contentRoot) || string.IsNullOrEmpty(raw)) return Resolve(raw, ref missing);
            bool v;
            string full = S13SeRules.TextureFullPath(raw, contentRoot, MyFileSystem.ContentPath, MyFileSystem.FileExists, out v);
            if (full == null) { missing += (missing.Length > 0 ? ";" : "") + raw; return ""; }
            if (v) viaRoot = true;
            try { return Path.GetFullPath(full); } catch (Exception) { return full; }
        }

        private static string Resolve(string raw, ref string missing)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string full;
            try { full = Path.IsPathRooted(raw) ? raw : Path.Combine(MyFileSystem.ContentPath, raw); }
            catch (Exception) { missing += (missing.Length > 0 ? ";" : "") + raw; return ""; }
            if (MyFileSystem.FileExists(full)) return Path.GetFullPath(full);
            missing += (missing.Length > 0 ? ";" : "") + raw;
            return "";
        }
    }
}
