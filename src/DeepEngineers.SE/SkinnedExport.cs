using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Game.Entities.Character;
using VRage.Game.Components;
using VRageMath;
using VRageRender.Animations;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7 skinned astronaut, SE side (CONTRACT §5.4 "S7 conventions", "Character bones (S7; S7Bones)"; ROADMAP S7.N6,
 /// S7.M9). S5 already exports SE's own astronaut mesh skinned on the 78 canonical bones (<see cref="CharacterExport"/>,
 /// the S5 model path through <c>ModelLoader</c> kind 2); S7 adds <c>ExtraBones</c> so that, for every character model
 /// of the session, CharacterAdd <c>boneCount</c> = C3 SkinnedBind <c>boneCount</c> = C1 <c>boneCount</c> =
 /// <c>Model.Bones.Length</c> (81 for both astronauts):
 /// <list type="bullet">
 /// <item>the plan, once per export, at CharacterExport.Send before the models go to C3 (<see cref="CharacterAddBones"/>):
 /// the live character's <c>AnimationController.CharacterBones</c>
 /// gives the row order of Wire
 /// <see cref="S7Bones.TryRowOrder"/> (rows 0..77 canon1, 78.. the others in model order); ExtraBones only when the
 /// export is not the fallback, the live model is the exported asset, <c>CharacterBones.Length == Model.Bones.Length
 /// &lt;= 160</c> and canon1 maps (else S5's 78-row export, logged once per model);</item>
 /// <item>SkinnedBind: the loader's 78 canonical rows (inverse absolute binds from <c>MyModelBone.Transform</c>) plus
 /// the extra bones' <c>SkinTransform</c>, re-encoded in place of the model's C3 SkinnedBind message (same group) for
 /// the body and, when it has one, the head1st part; the loader's rows are compared with the canonical bones'
 /// <c>SkinTransform</c> once (<c>bindDiff</c> in the <c>DE.S7 bones</c> line);</item>
 /// <item>C1 (<see cref="FillExtra"/>, the C1 writer through CharacterExport.FillBones): rows 78.. =
 /// <c>BoneAbsoluteTransforms</c> of the extra bones, <c>boneCount</c> = the plan's count; S7.M9 truncates the count to
 /// 64 (CharacterAdd and C1);</item>
 /// <item>rows <c>s7.bones</c> {tick, model, boneCount, modelBones, extra, characterId, world[[x,y,z]…]} (SN-world
 /// through F, all sent rows) for N ticks on request; C11 <c>de.bones {action: status | rows, frames}</c>
 /// (<see cref="Command"/>, routed by the S7 dispatcher, or <see cref="Execute"/>).</item>
 /// </list>
 /// Keyed by character id (v3: no "the player"); C1 carries the local viewer's character only (CONTRACT O24). Update
 /// thread; never throws into the C1 writer.
 /// </summary>
    internal static class SkinnedExport
    {
        public const string Cmd = "de.bones";
        public const int MaxRowFrames = 600;

        private sealed class Plan
        {
            public long CharacterId;
            public ulong BodyKey;
            public string Asset;
            public bool Fallback;
            public bool Extra;
            public int ModelBones;
            public int[] RowToModel;
            public string[] RowNames;
            public float[] ExtraBind;
            public double BindDiff = double.NaN;
            public string Why;
            public bool HeadPatched;
            public List<OutMessage> Body;
        }

        private static Plan _plan;
        private static readonly Dictionary<string, string> _models = new Dictionary<string, string>(StringComparer.Ordinal);
        private static int _rowFrames;
        private static long _rows, _plans, _extraPlans, _filled, _fillErrors;
        private static string _fillError;

        /// <summary>The C1 bone count of the last <see cref="FillExtra"/> (78, the model's count, or 64 under S7.M9).</summary>
        public static int LastSent { get; private set; }

        // ============================================================================================== CharacterExport.Send

 /// <summary>
 /// CharacterExport.Send, before the C3 models are queued: the plan of this export (once; the same message lists
 /// are re-queued on every stream), the SkinnedBind patched to the plan's rows, <c>ExtraBones</c> added to
 /// <paramref name="flags"/>; returns CharacterAdd <c>boneCount</c> (the model's bones, 78 without the plan, 64
 /// under S7.M9). Never throws (78 and S5's export on any error).
 /// </summary>
        public static uint CharacterAddBones(MyCharacter c, long characterId, ulong bodyKey, string asset, bool fallback, ExportedModel body,
                                             List<OutMessage> bodyMessages, ExportedModel head, List<OutMessage> headMessages, ref uint flags)
        {
            Plan p;
            try { p = Prepare(c, characterId, bodyKey, asset, fallback, body, bodyMessages, head, headMessages); }
            catch (Exception e)
            {
                p = new Plan { CharacterId = characterId, BodyKey = bodyKey, Asset = asset, Body = bodyMessages, Why = "plan failed: " + e.GetType().Name + ": " + e.Message };
                _plan = p;
                DeLog.Info("DE.S7 bones " + asset + " plan FAILED " + e.GetType().Name + ": " + e.Message + " (S5's canon1 export)");
            }
            if (p.Extra) flags = S7Bones.WithExtraBones(flags);
            int count = S7Bones.SentCount(p.Extra ? p.ModelBones : S7Bones.CanonicalRows, S7Mutants.M9);
            return (uint)count;
        }

        private static Plan Prepare(MyCharacter c, long characterId, ulong bodyKey, string asset, bool fallback, ExportedModel body,
                                    List<OutMessage> bodyMessages, ExportedModel head, List<OutMessage> headMessages)
        {
            Plan old = _plan;
            if (old != null && old.CharacterId == characterId && old.BodyKey == bodyKey && ReferenceEquals(old.Body, bodyMessages)) return old;
            var p = new Plan { CharacterId = characterId, BodyKey = bodyKey, Asset = asset, Fallback = fallback, Body = bodyMessages, ModelBones = -1 };
            _plans++;
            MyCharacterBone[] bones = null;
            try
            {
                MyAnimationControllerComponent ac = c != null ? c.AnimationController : null;
                bones = ac != null ? ac.CharacterBones : null;
                p.ModelBones = c != null && c.Model != null && c.Model.Bones != null ? c.Model.Bones.Length : -1;
            }
            catch (Exception e) { p.Why = "bones unreadable: " + e.GetType().Name; }
            if (p.Why != null) { }
            else if (fallback) p.Why = "fallback model (the default Engineer): S5's canon1 export";
            else if (c == null || c.Model == null || !string.Equals(c.Model.AssetName, asset, StringComparison.Ordinal)) p.Why = "the live model is not the exported asset";
            else if (bones == null) p.Why = "no CharacterBones";
            else if (bones.Length != p.ModelBones) p.Why = "CharacterBones " + bones.Length + " != Model.Bones " + p.ModelBones;
            else if (p.ModelBones > S7Bones.MaxBones) p.Why = "the model has " + p.ModelBones + " bones (more than " + S7Bones.MaxBones + ")";
            else
            {
                int n = bones.Length;
                var names = new string[n];
                var parents = new int[n];
                for (int i = 0; i < n; i++)
                {
                    MyCharacterBone b = bones[i];
                    if (b == null) { p.Why = "bone " + i + " is null"; break; }
                    if (b.Index != i) { p.Why = "bone " + b.Name + " index " + b.Index + " != " + i; break; }
                    names[i] = b.Name;
                    parents[i] = b.Parent != null ? b.Parent.Index : -1;
                }
                int[] rows = null;
                string why = null;
                if (p.Why == null && !S7Bones.TryRowOrder(names, parents, out rows, out why)) p.Why = why;
                else if (p.Why == null && rows != null)
                {
                    p.RowToModel = rows;
                    p.RowNames = new string[rows.Length];
                    for (int r = 0; r < rows.Length; r++) p.RowNames[r] = names[rows[r]];
                    int extra = rows.Length - S7Bones.CanonicalRows;
                    p.ExtraBind = new float[12 * extra];
                    for (int k = 0; k < extra; k++) Put(bones[rows[S7Bones.CanonicalRows + k]].SkinTransform, p.ExtraBind, 12 * k);
                    p.BindDiff = BindDiff(body, bones, rows);
                    if (extra == 0) p.Why = "no non-canonical bones (78 rows are the whole model)";
                    else if (!PatchBind(body, bodyMessages, p.ExtraBind, out why)) p.Why = "body SkinnedBind not patched: " + why;
                    else
                    {
                        p.Extra = true;
                        string hw = null;
                        p.HeadPatched = head == null || PatchBind(head, headMessages, p.ExtraBind, out hw);
                        if (!p.HeadPatched) p.Why = "head1st SkinnedBind kept at 78 rows: " + hw;
                    }
                }
            }
            if (p.Extra) _extraPlans++;
            _plan = p;
            LogModel(p);
            return p;
        }

        private static void LogModel(Plan p)
        {
            int sent = S7Bones.SentCount(p.Extra ? p.ModelBones : S7Bones.CanonicalRows, S7Mutants.M9);
            string why = p.Why;
            if (S7Mutants.M9) why = (why != null ? why + "; " : "") + "MUTANT S7.M9 truncated to " + S7Bones.MutantTruncatedCount;
            string line = S7Bones.BonesLine(p.Asset, p.ModelBones, sent, p.Extra, why);
            if (!double.IsNaN(p.BindDiff)) line += " bindDiff=" + p.BindDiff.ToString("0.###E+0", CultureInfo.InvariantCulture);
            if (p.Extra && p.RowNames != null)
            {
                var extra = new StringBuilder();
                for (int r = S7Bones.CanonicalRows; r < p.RowNames.Length; r++) extra.Append(r == S7Bones.CanonicalRows ? "" : ",").Append(p.RowNames[r]);
                line += " extra=[" + extra + "] character " + p.CharacterId;
            }
            string key = (p.Asset ?? "?") + "|" + p.Extra + "|" + sent;
            string prev;
            if (_models.TryGetValue(key, out prev)) return;   // once per model and outcome in the session
            _models[key] = line;
            DeLog.Info(line);
        }

        /// <summary>Largest |loader row − SkinTransform| over the canonical bones (both are the inverse absolute bind).</summary>
        private static double BindDiff(ExportedModel body, MyCharacterBone[] bones, int[] rows)
        {
            if (body == null || body.SkinnedBind == null || body.SkinnedBind.Length < 12 * S7Bones.CanonicalRows) return double.NaN;
            var tmp = new float[12];
            double max = 0;
            for (int r = 0; r < S7Bones.CanonicalRows; r++)
            {
                Put(bones[rows[r]].SkinTransform, tmp, 0);
                for (int k = 0; k < 12; k++) max = Math.Max(max, Math.Abs(tmp[k] - body.SkinnedBind[12 * r + k]));
            }
            return max;
        }

        /// <summary>The model's SkinnedBind with the extra rows appended, re-encoded in place of its C3 message.</summary>
        private static bool PatchBind(ExportedModel m, List<OutMessage> messages, float[] extra, out string why)
        {
            why = null;
            if (m == null || m.SkinnedBind == null) { why = "no SkinnedBind"; return false; }
            if (messages == null) { why = "no messages"; return false; }
            int want = 12 * S7Bones.CanonicalRows + extra.Length;
            if (m.SkinnedBind.Length == want) return true;   // already patched (a re-plan of the same export)
            if (m.SkinnedBind.Length != 12 * S7Bones.CanonicalRows) { why = "SkinnedBind of " + m.SkinnedBind.Length / 12 + " rows"; return false; }
            int idx = -1;
            for (int i = 0; i < messages.Count; i++) if (messages[i].Type == C3Type.SkinnedBind) { idx = i; break; }
            if (idx < 0) { why = "no SkinnedBind message"; return false; }
            var rows = new float[want];
            Array.Copy(m.SkinnedBind, rows, m.SkinnedBind.Length);
            Array.Copy(extra, 0, rows, m.SkinnedBind.Length, extra.Length);
            OutMessage msg = S3Codec.SkinnedBindMsg(m.ModelKey, rows, messages[idx].Group, m.AssetName + " (S7 ExtraBones " + want / 12 + ")");
            msg.Entity = messages[idx].Entity;
            messages[idx] = msg;
            m.SkinnedBind = rows;
            return true;
        }

        private static void Put(Matrix b, float[] dst, int o)
        {
            dst[o] = b.M11; dst[o + 1] = b.M12; dst[o + 2] = b.M13;
            dst[o + 3] = b.M21; dst[o + 4] = b.M22; dst[o + 5] = b.M23;
            dst[o + 6] = b.M31; dst[o + 7] = b.M32; dst[o + 8] = b.M33;
            dst[o + 9] = b.M41; dst[o + 10] = b.M42; dst[o + 11] = b.M43;
        }

        // ============================================================================================== C1 (CharacterExport.FillBones)

 /// <summary>
 /// CharacterExport.FillBones after the 78 canonical rows (the C1 writer, update thread): the extra rows 78.. from
 /// <c>BoneAbsoluteTransforms</c> when this export's plan has ExtraBones; returns the C1 <c>boneCount</c> (the
 /// plan's count, 78 without it, 64 under S7.M9) and writes the requested <c>s7.bones</c> rows. Never throws.
 /// </summary>
        public static int FillExtra(MyCharacter c, ulong tick, ulong bodyKey, Matrix[] abs, float[] rows)
        {
            int count = S7Bones.CanonicalRows;
            Plan p = _plan;
            try
            {
                if (p != null && p.Extra && c != null && p.CharacterId == c.EntityId && p.BodyKey == bodyKey && p.RowToModel != null &&
                    rows != null && rows.Length >= 12 * p.RowToModel.Length)
                {
                    bool ok = true;
                    for (int r = S7Bones.CanonicalRows; r < p.RowToModel.Length; r++)
                    {
                        int m = p.RowToModel[r];
                        if (m < 0 || m >= abs.Length) { ok = false; break; }
                        Put(abs[m], rows, 12 * r);
                    }
                    if (ok) count = p.RowToModel.Length;
                }
                _filled++;
            }
            catch (Exception e)
            {
                count = S7Bones.CanonicalRows;
                _fillErrors++;
                if (_fillError == null) { _fillError = e.GetType().Name + ": " + e.Message; DeLog.Info("DE.Error S7 bones C1 extra rows " + _fillError + " (78 rows from now on this tick)"); }
            }
            count = S7Bones.SentCount(count, S7Mutants.M9);
            LastSent = count;
            if (_rowFrames > 0)
            {
                _rowFrames--;
                try { BonesRow(c, tick, rows, count, p); }
                catch (Exception e) { DeLog.Info("DE.Error S7 bones row " + e.GetType().Name + ": " + e.Message); _rowFrames = 0; }
            }
            return count;
        }

        /// <summary>CharacterExport.Forget: the character's records are gone (a new export makes a new plan).</summary>
        public static void Forget(string why)
        {
            _plan = null;
            LastSent = 0;
        }

        // ============================================================================================== rows and commands

        private static void BonesRow(MyCharacter c, ulong tick, float[] rows, int count, Plan p)
        {
            SiteMap site = SiteBinding.Map;
            SeMatrix world = FrameWriter.ToSe(c.WorldMatrix);
            var sb = new StringBuilder(256 + count * 40);
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s7.bones\",\"tick\":").Append(tick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"characterId\":").Append(c.EntityId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"characterIdStr\":\"").Append(c.EntityId.ToString(CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"model\":");
            JsonText.AppendString(sb, p != null ? p.Asset : (c.Model != null ? c.Model.AssetName : null));
            sb.Append(",\"boneCount\":").Append(count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"addBoneCount\":").Append(S7Bones.SentCount(p != null && p.Extra ? p.ModelBones : S7Bones.CanonicalRows, S7Mutants.M9).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"bindBones\":").Append((p != null && p.Extra ? p.ModelBones : S7Bones.CanonicalRows).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"modelBones\":").Append((p != null ? p.ModelBones : -1).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"extra\":").Append(p != null && p.Extra ? "true" : "false");
            sb.Append(",\"mutantS7M9\":").Append(S7Mutants.M9 ? "true" : "false");
            if (site == null) sb.Append(",\"world\":null,\"why\":\"site not bound\"");
            else
            {
                sb.Append(",\"world\":[");
                for (int i = 0; i < count; i++)
                {
                    Vec3 w = site.SeToSn(S7Bones.WorldPoint(world, rows, 12 * i));
                    if (i > 0) sb.Append(',');
                    sb.Append('[');
                    JsonText.AppendNumber(sb, w.X); sb.Append(',');
                    JsonText.AppendNumber(sb, w.Y); sb.Append(',');
                    JsonText.AppendNumber(sb, w.Z);
                    sb.Append(']');
                }
                sb.Append(']');
            }
            sb.Append('}');
            _rows++;
            Jsonl.WriteLineBuffered(sb.ToString());
        }

        public static Dictionary<string, object> Status()
        {
            Plan p = _plan;
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "plans", _plans }, { "extraPlans", _extraPlans }, { "lastSent", LastSent }, { "rows", _rows }, { "rowFramesLeft", _rowFrames },
                { "filled", _filled }, { "fillErrors", _fillErrors }, { "fillError", _fillError }, { "mutantS7M9", S7Mutants.M9 },
                { "models", new List<string>(_models.Values) },
            };
            if (p != null)
            {
                d["characterId"] = p.CharacterId;
                d["model"] = p.Asset;
                d["extraBones"] = p.Extra;
                d["modelBones"] = p.ModelBones;
                d["boneCount"] = S7Bones.SentCount(p.Extra ? p.ModelBones : S7Bones.CanonicalRows, S7Mutants.M9);
                d["headPatched"] = p.HeadPatched;
                d["why"] = p.Why;
                d["bindDiff"] = double.IsNaN(p.BindDiff) ? null : (object)p.BindDiff;
                if (p.RowNames != null && p.RowNames.Length > S7Bones.CanonicalRows)
                {
                    var extra = new List<string>();
                    for (int r = S7Bones.CanonicalRows; r < p.RowNames.Length; r++) extra.Add(p.RowNames[r]);
                    d["extraNames"] = extra;
                }
            }
            return d;
        }

        /// <summary>A dispatcher entry: <c>de.bones</c> only (null for any other command).</summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            return cmd == Cmd ? Command(args) : null;
        }

        /// <summary><c>de.bones {action: status | rows, frames}</c>: rows = one <c>s7.bones</c> row per C1 tick for <c>frames</c> ticks (default 60, ≤ 600).</summary>
        public static Dictionary<string, object> Command(IDictionary<string, object> args)
        {
            string action = "status";
            object v;
            if (args != null && args.TryGetValue("action", out v) && v is string) action = ((string)v).Trim().ToLowerInvariant();
            Dictionary<string, object> d;
            switch (action)
            {
                case "status":
                    d = Status();
                    d["ok"] = true;
                    break;
                case "rows":
                {
                    int frames = 60;
                    if (args.TryGetValue("frames", out v) && v != null)
                    {
                        try { frames = Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch (Exception) { frames = 60; }
                    }
                    _rowFrames = Math.Max(1, Math.Min(MaxRowFrames, frames));
                    d = Status();
                    d["ok"] = true;
                    break;
                }
                default:
                    d = new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", "de.bones: action status | rows (frames)" } };
                    d["line"] = d["error"];
                    return d;
            }
            Plan p = _plan;
            d["line"] = "DE.S7 bones " + (p != null ? (p.Asset ?? "?") + " " + p.ModelBones + " sent=" + LastSent + (p.Extra ? " ExtraBones" : " canon1-only (" + p.Why + ")") : "no plan (no character export sent yet)") +
                        (action == "rows" ? " rows for " + _rowFrames + " ticks" : "") + (S7Mutants.M9 ? " MUTANT S7.M9" : "");
            return d;
        }
    }
}
