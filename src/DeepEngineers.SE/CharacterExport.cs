using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Models;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// CONTRACT §5.4 "Character"). Polled every
 /// tick on the update thread: the local character (<c>MySession.Static.LocalCharacter</c>), its definition subtype
 /// (<c>MyCharacter.ModelName</c>), model (<c>MyEntity.Model.AssetName</c>), colour
 /// and skins
 /// A change exports the
 /// model on the <c>DE.ModelLoader</c> thread (<see cref="ModelLoader"/> kind 2: skinned, split by
 /// <c>MyCharacterDefinition.MaterialsDisabledIn1st</c>, skins resolved), then C3 (materials, the body and head1st models with
 /// their SkinnedBind) and C4 lane 1 (EntityRemove of the previous record, CharacterAdd {id, body key, Version1 | Player |
 /// Fallback, 78}, PartAdd Body (class 5, key 1) and Head1st (class 5, key 2, HiddenInFirstPerson), identity, the colour,
 /// MetalnessColorable). A colour-only change re-sends the records without a reload. Fallback: the default Engineer
 /// (<c>MyDefinitionManager.Static.Characters.TryGetValue("Default_Astronaut", …)</c>, its <c>Model</c>) with the player's
 /// colour and no skin, <c>DE.S5 char FALLBACK &lt;reason&gt;</c> once; if that fails too, no character mirror. Never a
 /// Subnautica diver. C1 bones every tick: <c>MyCharacter.BoneAbsoluteTransforms</c>
 /// picked in canon1 order through <c>AnimationController.FindBone(string, out int)</c>, <c>bonesVersion</c> = the low 32
 /// bits of the body model key. Optional held tool: <c>MyCharacter.CurrentWeapon</c> as an entity with a model → PartAdd
 /// class 6, key 3, Dynamic, and a C2 row (its world matrix relative to the character's).
 /// </summary>
    internal static class CharacterExport
    {
        private enum State { None, Loading, Ready, Failed }

        private sealed class Export
        {
            public long CharacterId;
            public string Asset;
            public bool Fallback;
            public string FallbackReason;
            public ulong BodyKey, HeadKey;
            public ExportedModel Body, Head;
            public List<OutMessage> BodyMessages, HeadMessages;
            public bool ModelsSentThisStream;
            public int ModelBones;
            public string SkinText;
            public bool SkinMetal;
        }

        private static State _state;
        private static Export _export;
        private static ulong _sig;          // id, model name, asset, skins
        private static Vector3 _colour;
        private static bool _hasSig;
        private static bool _needSend;
        /// <summary>Review F4: a colour-only change is a PartRecolor of the character's parts, never a re-send.</summary>
        private static bool _needRecolor;
        private static ulong _loadingBodyKey;   // review F12: a skin change during a load supersedes the older request
        private static long _sentId;
        private static long _sentTicket = -1;
        private static bool _loggedFallback, _loggedFailed;
        private static long _loadingId;
        private static string _loadingAsset;
        private static ulong _loadRequestedMs;
        private static long _exports, _fallbacks, _resends;

        // tool
        private static long _toolEntity;
        private static ulong _toolModelKey;
        private static bool _toolSent;
        private static MyEntity _tool;
        private static long _toolCharacter;
        // S7p-character (P9): the held tool's own skin (its MyAssetModifierComponent), the variant wait, the identity counters
        private static string _toolSkin;
        private static long _toolWaitEntity;
        private static string _toolWaitSkin;
        private static ulong _toolWaitSinceMs;
        private static long _toolSkinSends, _toolSkinFallbacks, _identityLines;
        private static string _lastIdentity;
        private static readonly List<string> _skinNames = new List<string>();
        private static readonly List<string> _toolSkinNames = new List<string>();

        // C1 bones
        private static long _bonesCharId = long.MinValue;
        private static readonly int[] _c1Map = new int[MirrorSkeleton.BoneCount];
        private static bool _c1MapOk;
        private static string _c1MapWhy;
        private static ulong _nextCharRowMs;
        public static ulong TraceUntilMs;

        public static string StateName { get { return _state.ToString() + (_export != null && _export.Fallback ? "+fallback" : ""); } }

        /// <summary>
 /// S6 (damage consumer, CONTRACT §4.1 step 5): the id of the character written to C1 (<c>MySession.LocalCharacter</c>,
 /// as FrameWriter fills it); 0 when there is none.
 /// </summary>
        internal static long CurrentCharacterId
        {
            get
            {
                MySession s = MySession.Static;
                MyCharacter c = s != null ? s.LocalCharacter : null;
                return c != null && !c.Closed ? c.EntityId : 0;
            }
        }

        // ============================================================================================= lifecycle

 /// <summary>Character model keys queued on C3 in this stream.</summary>
        private static readonly HashSet<ulong> _streamModels = new HashSet<ulong>();

        public static void OnStreamStart()
        {
            _streamModels.Clear();
            if (_export != null) { _export.ModelsSentThisStream = false; _needSend = true; }
            _sentId = 0;
            _sentTicket = -1;
            _toolSent = false;
            _toolEntity = 0;
        }

        /// <summary>Forgets the character (world change, S5 or the export switched off): an EntityRemove when its records were sent.</summary>
        public static void Forget(string why)
        {
            if (_state == State.None && _export == null && _sentId == 0) return;
            if (_sentId != 0 && GridMirrorProducer.StreamingNow)
            {
                GridMirrorProducer.PurgeC4(_sentId);
                GridMirrorProducer.EnqueueControl(S3Codec.EntityRemoveMsg(_sentId));
                CharRow("remove", _sentId, why);
            }
            _state = State.None;
            _export = null;
            _hasSig = false;
            _streamModels.Clear();
            _needSend = false;
            _sentId = 0;
            _sentTicket = -1;
            _toolSent = false;
            _toolEntity = 0;
            _tool = null;
            _toolSkin = null;
            _toolWaitEntity = 0;
            _bonesCharId = long.MinValue;
            SkinnedExport.Forget(why); // S7: the bone plan goes with the records
        }

        /// <summary>The consumer audit's view: +1 entity once CharacterAdd was written; false while the character is not settled.</summary>
        public static bool AuditView(ref uint entities, ref uint expectPending)
        {
            if (_state == State.Loading || _needSend) return false;
            if (_sentId != 0)
            {
                if (_sentTicket >= 0 && !GridMirrorProducer.IsC4Done(_sentTicket)) return false;
                entities++;
            }
            return true;
        }

        // ============================================================================================= per tick

        public static void Tick(ulong nowMs, uint worldEpoch)
        {
            try { TickCore(nowMs); }
            catch (Exception e) { DeLog.Info("DE.Error S5 character " + e.GetType().Name + ": " + e.Message); }
        }

        private static void TickCore(ulong nowMs)
        {
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || c.Closed || c.MarkedForClose)
            {
                if (_sentId != 0 || _export != null) Forget("no local character");
                return;
            }
            string asset = c.Model != null ? c.Model.AssetName : null;
            ulong sig = Signature(c, asset);
            Vector3 colour = c.ColorMask;
            if (!_hasSig || sig != _sig)
            {
                _hasSig = true;
                _sig = sig;
                _colour = colour;
                StartExport(nowMs, c, asset, false, null);
            }
            else if (colour != _colour)
            {
                _colour = colour;
                if (_state == State.Ready) { _needRecolor = true; _resends++; }
                Identity(c, asset, "colour");
            }
            if (_state == State.Ready && _needSend && GridMirrorProducer.StreamingNow) Send(c);
            else if (_state == State.Ready && _needRecolor && GridMirrorProducer.StreamingNow && _sentId != 0) Recolor(c);
            if (_state == State.Ready && GridMirrorProducer.StreamingNow && !_needSend) TickTool(nowMs, c);
        }

        /// <summary>id, definition subtype, model asset and the skin list (colour excluded: a colour change re-sends without a reload).</summary>
        private static ulong Signature(MyCharacter c, string asset)
        {
            ulong h = Fnv.AddU64(Fnv.Offset, (ulong)c.EntityId);
            h = Fnv.AddU64(h, StrHash(c.ModelName, ref _nameRef, ref _nameHash));
            h = Fnv.AddU64(h, StrHash(asset, ref _assetRef, ref _assetHash));
            MyAssetModifierComponent comp;
            if (c.Components.TryGet<MyAssetModifierComponent>(out comp) && comp != null && comp.AssetModifiers != null)
                foreach (MyDefinitionId id in comp.AssetModifiers) h = Fnv.AddU64(h, (ulong)(uint)id.SubtypeId.GetHashCode());
            return h;
        }

        private static string _nameRef, _assetRef;
        private static ulong _nameHash, _assetHash;

        /// <summary>FNV of a string, recomputed only when the reference changes (no per-tick allocation).</summary>
        private static ulong StrHash(string s, ref string cachedRef, ref ulong cachedHash)
        {
            if (ReferenceEquals(s, cachedRef) && s != null) return cachedHash;
            cachedRef = s;
            cachedHash = Fnv.Of(s ?? "");
            return cachedHash;
        }

        private static void StartExport(ulong nowMs, MyCharacter c, string asset, bool fallback, string reason)
        {
            MyCharacterDefinition def = c.Definition;
            string[] head = def != null ? def.MaterialsDisabledIn1st : null;
            string skinText = null;
            bool metal = false;
            Dictionary<string, SkinTextureChange> skin = null;
            if (!fallback)
            {
                var skins = new List<MyStringHash>();
                MyAssetModifierComponent comp;
                if (c.Components.TryGet<MyAssetModifierComponent>(out comp) && comp != null && comp.AssetModifiers != null)
                    foreach (MyDefinitionId id in comp.AssetModifiers) skins.Add(id.SubtypeId);
                if (skins.Count > 0)
                {
                    skin = GridMirrorProducer.SkinChanges(skins, out skinText, out metal);
                    if (skin.Count == 0) { skin = null; skinText = null; }
                }
            }
            else
            {
                // S7p-character (P9, "it must work for both"): the fallback is the default Engineer of the player's own gender
                MyCharacterDefinition dd;
                string gender = def != null ? def.Gender : null;
                if (!TryDefault(gender, out dd)) { Fail("the default Engineer definitions " + S7pCharacterExportRules.FallbackSubtype(gender) + " and " + S5Defaults.DefaultCharacter + " are missing"); return; }
                asset = dd.Model;
                head = dd.MaterialsDisabledIn1st;
            }
            if (string.IsNullOrEmpty(asset))
            {
                if (!fallback) { StartExport(nowMs, c, null, true, "the character has no model"); return; }
                Fail("the default Engineer has no model");
                return;
            }
            ulong bodyKey = S5Conv.ModelKey(asset, 0, 1f, skinText, "body");
            ulong headKey = S5Conv.ModelKey(asset, 0, 1f, skinText, "head1st");
            _state = State.Loading;
            _loadingId = c.EntityId;
            _loadingAsset = asset;
            _loadingBodyKey = bodyKey;
            _loadRequestedMs = nowMs;
            GridMirrorProducer.RequestLoad(new ModelLoadRequest
            {
                Kind = ModelLoadRequest.KindCharacter, ModelKey = bodyKey, AssetName = asset, Scale = 1f, WorldEpoch = GridMirrorProducer.WorldEpochNow,
                BodyKey = bodyKey, HeadKey = headKey, HeadMaterials = head, Skin = skin, SkinText = skinText, CharacterId = c.EntityId,
                Fallback = fallback, FallbackReason = reason,
            });
            DeLog.Info("DE.S5 char export requested: character " + c.EntityId + " model " + asset + (skinText != null ? " skin " + skinText : "") +
                       (fallback ? " FALLBACK (" + reason + ")" : "") + " body=" + S3Rows.Hex(bodyKey) + " head1st=" + S3Rows.Hex(headKey) +
                       " headMaterials=[" + (head != null ? string.Join(",", head) : "") + "] (DE.ModelLoader thread)");
            _skinMetalPending = metal;
            Identity(c, asset, fallback ? "export FALLBACK" : "export"); // S7p-character (P9)
        }

        private static bool _skinMetalPending;

        /// <summary>
 /// S7p-character (P9): the default definition of the player's gender
 /// the male Engineer when that one is missing.
 /// </summary>
        private static bool TryDefault(string gender, out MyCharacterDefinition def)
        {
            def = null;
            try
            {
                string want = S7pCharacterExportRules.FallbackSubtype(gender);
                if (MyDefinitionManager.Static.Characters.TryGetValue(want, out def) && def != null && !string.IsNullOrEmpty(def.Model)) return true;
                def = null;
                return MyDefinitionManager.Static.Characters.TryGetValue(S5Defaults.DefaultCharacter, out def) && def != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
 /// S7p-character (P9): one <c>DE.S7p char identity</c> line (definition subtype <c>MyCharacter.ModelName</c>, the
 /// definition's gender, the model asset, <c>ColorMask</c> = <c>Render.ColorMaskHsv</c>, the asset modifier subtypes and
 /// the held tool's skin) whenever it changes, and an <c>s5.char</c> row <c>op: identity</c> with the same fields: the

 /// </summary>
        private static void Identity(MyCharacter c, string asset, string why)
        {
            try
            {
                _skinNames.Clear();
                MyAssetModifierComponent comp;
                if (c.Components.TryGet<MyAssetModifierComponent>(out comp) && comp != null && comp.AssetModifiers != null)
                    foreach (MyDefinitionId id in comp.AssetModifiers) _skinNames.Add(id.SubtypeName);
                MyCharacterDefinition def = c.Definition;
                string gender = def != null ? def.Gender : null;
                Vector3 col = c.ColorMask;
                string text = S7pCharacterExportRules.IdentityText(c.EntityId, c.ModelName, gender, asset, col.X, col.Y, col.Z, _skinNames, _toolSkin);
                if (string.Equals(text, _lastIdentity, StringComparison.Ordinal)) return;
                _lastIdentity = text;
                _identityLines++;
                DeLog.Info("DE.S7p char identity (" + why + "): " + text);
                var sb = S5Rows.Head(Utc(), "char");
                S5Rows.Str(sb, "op", "identity");
                S5Rows.Str(sb, "why", why);
                S5Rows.Id(sb, "characterId", c.EntityId);
                S5Rows.Str(sb, "modelName", c.ModelName);
                S5Rows.Str(sb, "gender", gender);
                S5Rows.Str(sb, "asset", asset);
                S5Rows.Floats(sb, "colorMaskHsv", new[] { col.X, col.Y, col.Z });
                S5Rows.Str(sb, "skins", string.Join(",", _skinNames.ToArray()));
                S5Rows.Str(sb, "toolSkin", _toolSkin);
                Jsonl.WriteLine(S5Rows.End(sb));
            }
            catch (Exception e) { DeLog.Info("DE.S7p char identity not logged: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Fail(string why)
        {
            _state = State.Failed;
            _export = null;
            if (!_loggedFailed)
            {
                _loggedFailed = true;
                DeLog.Info("DE.S5 char FAIL " + why + ": no character mirror (the proxy capsule stays; never a Subnautica diver)");
            }
            CharRow("failed", _loadingId, why);
        }

        /// <summary>The loader's answer (update thread, from the producer's DrainLoader).</summary>
        public static void OnLoaded(ulong nowMs, ModelLoadResult r)
        {
            ModelLoadRequest q = r.Request;
            if (_state != State.Loading || q.CharacterId != _loadingId || !string.Equals(q.AssetName, _loadingAsset, StringComparison.Ordinal) || q.BodyKey != _loadingBodyKey) return; // superseded
            if (r.Error != null)
            {
                if (!q.Fallback)
                {
                    _fallbacks++;
                    if (!_loggedFallback) { _loggedFallback = true; DeLog.Info("DE.S5 char FALLBACK " + r.Error + " (model " + q.AssetName + "): exporting the default Engineer"); }
                    MySession s = MySession.Static;
                    MyCharacter c = s != null ? s.LocalCharacter : null;
                    if (c != null && c.EntityId == q.CharacterId) StartExport(nowMs, c, null, true, r.Error);
                    else Fail("the character changed during the export");
                    return;
                }
                Fail("the default Engineer failed too: " + r.Error);
                return;
            }
            _export = new Export
            {
                CharacterId = q.CharacterId, Asset = q.AssetName, Fallback = q.Fallback, FallbackReason = q.FallbackReason,
                BodyKey = q.BodyKey, HeadKey = r.Head != null ? q.HeadKey : 0,
                Body = r.Model, Head = r.Head, BodyMessages = r.Messages, HeadMessages = r.HeadMessages, ModelBones = r.ModelBones,
                SkinText = q.SkinText, SkinMetal = _skinMetalPending,
            };
            _state = State.Ready;
            _needSend = true;
            _loggedFailed = false;
            _exports++;
            DeLog.Info("DE.S5 char exported: character " + q.CharacterId + " model " + q.AssetName + (q.Fallback ? " (FALLBACK: " + q.FallbackReason + ")" : "") +
                       " bones " + r.ModelBones + " -> canon1 78; body verts=" + r.Model.VertexCount + " tris=" + r.Model.TriangleCount +
                       (r.Head != null ? "; head1st verts=" + r.Head.VertexCount + " tris=" + r.Head.TriangleCount : "; no head1st part") +
                       " loadMs=" + Math.Round(r.Model.LoadMs, 1).ToString(CultureInfo.InvariantCulture) + (r.Notes != null ? "; " + r.Notes : ""));
            var sb = S5Rows.Head(Utc(), "model");
            S5Rows.Hex(sb, "modelKey", q.BodyKey);
            S5Rows.Str(sb, "asset", q.AssetName);
            S5Rows.Str(sb, "char", "body");
            S5Rows.Int(sb, "verts", r.Model.VertexCount);
            S5Rows.Int(sb, "tris", r.Model.TriangleCount);
            S5Rows.Bool(sb, "skinned", true);
            S5Rows.Num(sb, "loadMs", Math.Round(r.Model.LoadMs, 3));
            Jsonl.WriteLine(S5Rows.End(sb));
        }

        /// <summary>C3 models (once per stream), then lane 1: EntityRemove of the previous record, CharacterAdd and the parts.</summary>
 /// <summary>Review F4: the colour mask changed on a sent character: PartRecolor of its parts (same id, same models).</summary>
        private static void Recolor(MyCharacter c)
        {
            Export x = _export;
            _needRecolor = false;
            if (x == null || c.EntityId != x.CharacterId || _sentId != x.CharacterId) return;
            Vector3 col = c.ColorMask;
            uint partExtra = S5Conv.PartFlags.MetalnessColorable;
            uint skinId = x.SkinText != null ? S5Conv.SkinId(x.SkinText) : 0u;
            GridMirrorProducer.EnqueueControl(S3Codec.PartRecolorMsg(x.CharacterId, Part(S5Conv.CharPart.Body, x.BodyKey, x.Asset, col, skinId, S5Conv.Part(S5Conv.PartClass.CharacterBody, partExtra))));
            if (x.Head != null)
                GridMirrorProducer.EnqueueControl(S3Codec.PartRecolorMsg(x.CharacterId, Part(S5Conv.CharPart.Head1st, x.HeadKey, x.Asset, col, skinId,
                                                   S5Conv.Part(S5Conv.PartClass.CharacterBody, partExtra | S5Conv.PartFlags.HiddenInFirstPerson))));
            DeLog.Info("DE.S5 char recolour: PartRecolor x" + (x.Head != null ? 2 : 1) + " for character 0x" + x.CharacterId.ToString("x"));
        }

        private static void Send(MyCharacter c)
        {
            Export x = _export;
            if (x == null) return;
            if (c.EntityId != x.CharacterId) return; // a new id: a new export is under way
 // S7 (SkinnedExport.cs): ExtraBones (Model.Bones.Length rows, 81) and the SkinnedBind patched before C3; 78 without
            uint flags = S5Conv.Character(x.Fallback);
            uint addBones = SkinnedExport.CharacterAddBones(c, x.CharacterId, x.BodyKey, x.Asset, x.Fallback, x.Body, x.BodyMessages, x.Head, x.HeadMessages, ref flags);
            if (!x.ModelsSentThisStream)
            {
                // a respawn re-exports the same models: they go over C3 once per stream
                if (_streamModels.Add(x.BodyKey)) GridMirrorProducer.QueueCharacterModels(x.Body, x.BodyMessages);
                if (x.Head != null && _streamModels.Add(x.HeadKey)) GridMirrorProducer.QueueCharacterModels(x.Head, x.HeadMessages);
                x.ModelsSentThisStream = true;
            }
            if (_sentId != 0)
            {
                GridMirrorProducer.PurgeC4(_sentId);
                GridMirrorProducer.EnqueueControl(S3Codec.EntityRemoveMsg(_sentId));
            }
            Vector3 col = c.ColorMask;
            GridMirrorProducer.EnqueueControl(S3Codec.CharacterAddMsg(x.CharacterId, x.BodyKey, flags, addBones));
            uint partExtra = S5Conv.PartFlags.MetalnessColorable;
            uint skinId = x.SkinText != null ? S5Conv.SkinId(x.SkinText) : 0u;
            var body = Part(S5Conv.CharPart.Body, x.BodyKey, x.Asset, col, skinId, S5Conv.Part(S5Conv.PartClass.CharacterBody, partExtra));
            _sentTicket = GridMirrorProducer.EnqueueControl(S3Codec.PartAddMsg(x.CharacterId, body));
            if (x.Head != null)
            {
                var head = Part(S5Conv.CharPart.Head1st, x.HeadKey, x.Asset, col, skinId,
                                S5Conv.Part(S5Conv.PartClass.CharacterBody, partExtra | S5Conv.PartFlags.HiddenInFirstPerson));
                _sentTicket = GridMirrorProducer.EnqueueControl(S3Codec.PartAddMsg(x.CharacterId, head));
            }
            _sentId = x.CharacterId;
            _needSend = false;
            _toolSent = false;
            _toolEntity = 0;
            CharRow("add", x.CharacterId, x.Fallback ? "fallback: " + x.FallbackReason : null);
        }

        private static PartSnapshot Part(ulong key, ulong modelKey, string asset, Vector3 hsv, uint skinId, uint flags)
        {
            return new PartSnapshot
            {
                PartKey = key, ModelKey = modelKey, AssetName = asset, ModelScale = 1f,
                Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
                H = hsv.X, S = hsv.Y, V = hsv.Z, SkinId = skinId, Flags = flags,
            };
        }

        // ============================================================================================= tool (optional)

 /// <summary>
 /// The held tool (class 6, key 3). S7p-character (P9): with its own skin: the tool entity's
 /// <c>MyAssetModifierComponent.AssetModifiers</c>
 /// through the S5 skin resolver
 /// (<see cref="GridExporter.Models"/>: the |skin= variant once the base model is loaded, the base key when the skin
 /// changes none of its materials); a skin that appears later re-sends the part (same part key, PartAdd replaces it);
 /// the plain tool goes after <see cref="S7pCharacterExportRules.ToolSkinWaitMs"/> if the variant is still undecided.
 /// </summary>
        private static void TickTool(ulong nowMs, MyCharacter c)
        {
            var w = c.CurrentWeapon as MyEntity;
            long id = w != null && !w.Closed && w.Model != null ? w.EntityId : 0;
            string skin = id != 0 ? ToolSkinOf(w) : null;
            ulong variantKey = 0;
            uint skinId = 0;
            bool metal = false, decided = true;
            if (id != 0 && skin != null && !(id == _toolEntity && _toolSent && string.Equals(skin, _toolSkin, StringComparison.Ordinal)))
                decided = ResolveToolSkin(w, skin, out variantKey, out skinId, out metal);
            if (id != _toolWaitEntity || !string.Equals(skin, _toolWaitSkin, StringComparison.Ordinal)) { _toolWaitEntity = id; _toolWaitSkin = skin; _toolWaitSinceMs = nowMs; }
            S7pCharacterExportRules.ToolStep step = S7pCharacterExportRules.DecideTool(id, _toolEntity, _toolSent, skin, _toolSkin, decided, nowMs - _toolWaitSinceMs);
            if (step == S7pCharacterExportRules.ToolStep.Keep || step == S7pCharacterExportRules.ToolStep.Wait) return;
            ToolSubparts.OnToolChange(c.EntityId, _toolSent); // S7p-scene-above (P6b): the old tool's subparts go with it
            if (_toolSent)
            {
                GridMirrorProducer.EnqueueControl(S3Codec.PartRemoveMsg(c.EntityId, S5Conv.CharPart.Tool));
                _toolSent = false;
            }
            _toolEntity = id;
            _tool = null;
            _toolSkin = null;
            if (step == S7pCharacterExportRules.ToolStep.Remove || id == 0) return;
            string asset = w.Model.AssetName;
            ulong key = S3Keys.ModelKey(asset, 0, w.Model.ScaleFactor);
            uint flags = S5Conv.PartFlags.Dynamic;
            if (skin != null && decided && variantKey != 0)
            {
                key = variantKey;
                if (metal) flags |= S5Conv.PartFlags.MetalnessColorable;
                _toolSkinSends++;
            }
            else if (skin != null)
            {
                skinId = 0;
                _toolSkinFallbacks++;
                DeLog.Info("DE.S7p char tool skin " + skin + " of " + asset + (GridExporter.Models == null ? " (no S5 skin resolver)" : " not decided within " + S7pCharacterExportRules.ToolSkinWaitMs + " ms") +
                           ": the plain tool is sent");
            }
            // S13 hook (point 3): the tool entity's own colour mask (SE's OldGray for hand tools), not (0, 0, 0) = red in Subnautica
            var p = Part(S5Conv.CharPart.Tool, key, asset, S13Se.ToolColour(w, asset, skin), skin != null && decided ? skinId : 0u, S5Conv.Part(S5Conv.PartClass.Tool, flags));
            p.ModelScale = w.Model.ScaleFactor;
            GridMirrorProducer.EnsureModelFor(nowMs, p);
            GridMirrorProducer.EnqueueControl(S3Codec.PartAddMsg(c.EntityId, p));
            _toolSent = true;
            _tool = w;
            _toolCharacter = c.EntityId;
            _toolModelKey = key;
            _toolSkin = skin;
            ToolSubparts.Send(nowMs, c, w); // S7p-scene-above (P6b): the tool's own subparts (the hand drill's spike, the grinder disc)
            if (skin != null) Identity(c, c.Model != null ? c.Model.AssetName : null, "tool skin");
        }

        /// <summary>S7p-character: the first skin subtype of the tool entity's own asset modifiers, else null.</summary>
        private static string ToolSkinOf(MyEntity w)
        {
            _toolSkinNames.Clear();
            MyAssetModifierComponent comp;
            if (w.Components == null || !w.Components.TryGet<MyAssetModifierComponent>(out comp) || comp == null || comp.AssetModifiers == null) return null;
            foreach (MyDefinitionId id in comp.AssetModifiers) _toolSkinNames.Add(id.SubtypeName);
            return S7pCharacterExportRules.ToolSkin(_toolSkinNames);
        }

        /// <summary>S7p-character: the tool's model key under its skin (the S5 resolver); false while the base model is loading.</summary>
        private static bool ResolveToolSkin(MyEntity w, string skin, out ulong modelKey, out uint skinId, out bool metal)
        {
            modelKey = 0;
            skinId = 0;
            metal = false;
            IS5ModelResolver r = GridExporter.Models;
            if (r == null) return true; // no S5 resolver: the plain tool
            string asset;
            float scale;
            return r.Resolve(w.Model, MyStringHash.GetOrCompute(skin), out modelKey, out asset, out scale, out skinId, out metal);
        }

        /// <summary>The held tool's C2 row: ownerId = the character, partKey 3, its world matrix relative to the character's.</summary>
        public static bool ToolRow(out SeSubpart row)
        {
            row = default(SeSubpart);
            MyEntity t = _tool;
            if (!_toolSent || t == null || t.Closed) return false;
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || c.EntityId != _toolCharacter) return false;
            row = InstanceMath.MakeSubpartRow((ulong)c.EntityId, S5Conv.CharPart.Tool, SubgridPoses.Relative(t, c));
            return true;
        }

        // ============================================================================================= C1 bones

 /// <summary>
 /// C1 bones of this tick (PrepareForDraw postfix, from the C1 writer): 78 canonical rows from
 /// <c>BoneAbsoluteTransforms</c>, <c>boneCount</c> 78 and <c>bonesVersion</c> = low 32 bits of the body model key, when
 /// the export is ready for this character; else 0 and 0. Never throws; allocation-free except the log rows.
 /// </summary>
        public static int FillBones(MyCharacter c, ref SeFrame f, float[] rows)
        {
            AudioPolish.ObserveFrame(ref f); // S7 audio polish (AudioPolish.cs): this C1's camera-in-room bit, before any early return
            f.boneCount = 0;
            f.bonesVersion = 0;
            try
            {
                Export x = _export;
                if (_state != State.Ready || x == null || c == null || c.EntityId != x.CharacterId || rows == null || rows.Length < 12 * MirrorSkeleton.BoneCount) return 0;
                var ac = c.AnimationController;
                if (ac == null) return 0;
                if (c.EntityId != _bonesCharId) BuildC1Map(c, ac);
                if (!_c1MapOk) return 0;
                Matrix[] abs = c.BoneAbsoluteTransforms;
                if (abs == null) return 0;
                for (int i = 0; i < MirrorSkeleton.BoneCount; i++)
                {
                    int m = _c1Map[i];
                    if (m < 0 || m >= abs.Length) return 0;
                    Matrix b = abs[m];
                    int o = 12 * i;
                    rows[o] = b.M11; rows[o + 1] = b.M12; rows[o + 2] = b.M13;
                    rows[o + 3] = b.M21; rows[o + 4] = b.M22; rows[o + 5] = b.M23;
                    rows[o + 6] = b.M31; rows[o + 7] = b.M32; rows[o + 8] = b.M33;
                    rows[o + 9] = b.M41; rows[o + 10] = b.M42; rows[o + 11] = b.M43;
                }
                int sent = SkinnedExport.FillExtra(c, f.tick, x.BodyKey, abs, rows); // S7: rows 78.. with ExtraBones (81), 64 under S7.M9
                f.boneCount = (uint)sent;
                f.bonesVersion = MirrorConventions.BonesVersion(x.BodyKey);
                BoneRow(c, f.tick, rows, x);
                return sent;
            }
            catch (Exception)
            {
                f.boneCount = 0;
                f.bonesVersion = 0;
                return 0;
            }
        }

        private static void BuildC1Map(MyCharacter c, VRage.Game.Components.MyAnimationControllerComponent ac)
        {
            _bonesCharId = c.EntityId;
            _c1MapOk = true;
            _c1MapWhy = null;
            for (int i = 0; i < MirrorSkeleton.BoneCount; i++)
            {
                int idx;
                var bone = ac.FindBone(MirrorSkeleton.BoneName(i), out idx);
                if (bone == null) { _c1MapOk = false; _c1MapWhy = "bone " + MirrorSkeleton.BoneName(i) + " not in the character's skeleton"; _c1Map[i] = -1; continue; }
                _c1Map[i] = idx;
            }
            DeLog.Info("DE.S5 char " + c.EntityId + " C1 bones: " + (_c1MapOk ? "78 canon1 bones mapped by name (AnimationController.FindBone)" : "NONE (" + _c1MapWhy + "; bonesVersion 0)"));
        }

        /// <summary>s5.char bone rows (S5.N7a): pelvis, head and right palm absolute bones with the character's world matrix, every tick while traced, else once per second.</summary>
 /// <summary>The bones S5.N7a compares (pelvis, head, right palm) and the first-person anchor (HeadDummy).</summary>
        private static readonly int[] BoneRowBones = { MirrorSkeleton.Pelvis, MirrorSkeleton.Head, MirrorSkeleton.RightPalm, MirrorSkeleton.HeadDummy };

        private static void BoneRow(MyCharacter c, ulong tick, float[] rows, Export x)
        {
            ulong now = Win32.TickCount64();
            bool trace = now < TraceUntilMs;
            if (!trace && now < _nextCharRowMs) return;
            _nextCharRowMs = now + 1000;
            var sb = S5Rows.Head(Utc(), "char");
            S5Rows.Str(sb, "op", "bones");
            S5Rows.Int(sb, "tick", (long)tick);
            S5Rows.Id(sb, "characterId", c.EntityId);
            S5Rows.Hex(sb, "bodyKey", x.BodyKey);
            S5Rows.Int(sb, "bonesVersion", (uint)x.BodyKey);
            S5Rows.Matrix(sb, "world", FrameWriter.ToSe(c.WorldMatrix));
            foreach (int i in BoneRowBones)
            {
                var f = new float[12];
                Array.Copy(rows, 12 * i, f, 0, 12);
                S5Rows.Floats(sb, MirrorSkeleton.BoneName(i), f);
            }
            S5Rows.Bool(sb, "dead", c.IsDead);
            S5Rows.Bool(sb, "seated", c.IsSitting);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // ============================================================================================= rows

        private static void CharRow(string op, long id, string reason)
        {
            Export x = _export;
            var sb = S5Rows.Head(Utc(), "char");
            S5Rows.Str(sb, "op", op);
            S5Rows.Id(sb, "characterId", id);
            S5Rows.Str(sb, "state", StateName);
            if (x != null)
            {
                S5Rows.Str(sb, "asset", x.Asset);
                S5Rows.Hex(sb, "bodyKey", x.BodyKey);
                S5Rows.Hex(sb, "headKey", x.HeadKey);
                S5Rows.Int(sb, "bonesVersion", (uint)x.BodyKey);
                S5Rows.Bool(sb, "fallback", x.Fallback);
                S5Rows.Str(sb, "skin", x.SkinText);
                S5Rows.Int(sb, "modelBones", x.ModelBones);
                if (x.Body != null) { S5Rows.Int(sb, "bodyVerts", x.Body.VertexCount); S5Rows.Int(sb, "bodyTris", x.Body.TriangleCount); }
                if (x.Head != null) { S5Rows.Int(sb, "headVerts", x.Head.VertexCount); S5Rows.Int(sb, "headTris", x.Head.TriangleCount); }
            }
            // S7p-character (P9): the colour sent with the parts (C4 PartAdd colorMaskHSV) on every add/remove row
            S5Rows.Floats(sb, "colorMaskHsv", new[] { _colour.X, _colour.Y, _colour.Z });
            if (reason != null) S5Rows.Str(sb, "reason", reason);
            Jsonl.WriteLine(S5Rows.End(sb));
        }

        public static Dictionary<string, object> Status()
        {
            Export x = _export;
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "state", StateName }, { "sentId", _sentId }, { "needSend", _needSend }, { "exports", _exports }, { "fallbacks", _fallbacks },
                { "resends", _resends }, { "c1Bones", _c1MapOk }, { "c1Why", _c1MapWhy }, { "tool", _toolSent ? (object)_toolEntity : null },
                // S7p-character (P9)
                { "toolSkin", _toolSkin }, { "toolSkinSends", _toolSkinSends }, { "toolSkinFallbacks", _toolSkinFallbacks },
                { "colorMaskHsv", new List<object> { (double)_colour.X, (double)_colour.Y, (double)_colour.Z } }, { "identity", _lastIdentity }, { "identityLines", _identityLines },
            };
            if (x != null)
            {
                d["asset"] = x.Asset;
                d["bodyKey"] = S3Rows.Hex(x.BodyKey);
                d["headKey"] = S3Rows.Hex(x.HeadKey);
                d["fallback"] = x.Fallback;
                d["skin"] = x.SkinText;
            }
            return d;
        }

        private static string Utc() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    }
}
