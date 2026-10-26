using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S7t-colour-mask (ROADMAP S7t T6
 // white/yellow ship beige/tan"): the pure part of SE's colour mask on the mirrored grids, Unity-free and compiled into
 // DeepEngineers.SN.Tests (replay-exact against SE's shader, no game types).
 //
 // SE's formula, VERIFIEDand in the game's own shader sources (read in place, never copied):
 // - Content\Shaders\Geometry\Materials\PixelUtilsMaterials.hlsli, FeedOutputInternal: coloring = extras.w (the
 // _add alpha), reduced on metal by RemoveMetalnessFromColoring (threshold 0.4, multiply 0.5) only #ifndef
 // METALNESS_COLORABLE; base_color = ColorizeGray(cm.rgb, pixel.key_color, coloring);
 // - ColorizeGray: coloringHSV += frame_.TextureDebugMultipliers.ColorizeHSV.rgb; hsv = rgb_to_hsv(rgb_to_srgb(texel));
 // hsv.xy = 0; finalHsv = saturate(hsv + coloringHSV); texel = lerp(texel, srgb_to_rgb(hsv_to_rgb(finalHsv)),
 // coloring) (Content\Shaders\Math\Color.hlsli for the four helpers);
 // - frame_.TextureDebugMultipliers = MyRender11.Environment.Data.TextureMultipliers
 // = MySector.SunProperties.TextureMultipliers, whose
 // ColorizeHSV is MyTextureDebugMultipliers.Defaults (0, 0.8, -0.1):
 // the game's Environment.sbc sets only GlossMultiplier and MyStructXmlSerializer starts from the [StructDefault]
 // value (MyStructXmlSerializer.ReadXml). The UI agrees: MyColorPickerConstants.HSVOffsetToHSV shows
 // (h, s + 0.8, v + 0.55 - 0.1), i.e. exactly what the shader makes of a texel of sRGB value 0.55;
 // - the key colour: cube parts MyCubeInstanceData.ColorMaskHSV.xyz = MySlimBlock.ColorMaskHSV
 // fat blocks and subparts Render.ColorMaskHsv = SlimBlock.ColorMaskHSV (
 // MyRenderComponentBase.ColorMaskHsv → UpdateRenderEntity → MyRender11 SetKeyColor(ColorFromMask(hsv)), and
 // ColorFromMask returns its argument); edges Color.ColorToHSVDX11; all already in the PartAdd
 // record (colorMaskHSV at 0x48, three f32);
 // - METALNESS_COLORABLE: the render flag of the grid render component (false) or of a skin
 // unskinned cube parts are NOT colourable (the S5 HYPOTHESIS O23 is now VERIFIED); the PartAdd
 // flag 512 carries it.
 //
 // The S3 tint (PaintRules) and the S7p P9 bake (S7pPaint) took ColorizeHSV as 0: every saturation offset ≤ 0 became
 // Both now go
 // through ShaderKey; the block materials get SE's colour per texel (ColorMaskPaint, Unity side).
 // =================================================================================================================

 /// <summary>The live settings of the block paint (<c>de.s7t {mask, maskSide, maskMb}</c>, env with the same defaults).</summary>
    internal sealed class ColorMaskSettings
    {
        /// <summary>true: SE's colour per texel baked into the block materials; false: the S3 tint (UWE_3COLOR, now on the shader key).</summary>
        public bool Bake = true;
        /// <summary>Largest side of a baked albedo (the set's own mips below it are kept; a 1,024 set bakes at 512 by default).</summary>
        public int MaxSide = ColorMaskRules.DefaultSide;
        /// <summary>Texture memory the bakes may hold (RGBA32 chains, albedo + coloured emission).</summary>
        public int BudgetMb = ColorMaskRules.DefaultBudgetMb;
        public string Note = "defaults";

        public long BudgetBytes { get { return (long)BudgetMb * 1024L * 1024L; } }

        public ColorMaskSettings Clone()
        {
            return new ColorMaskSettings { Bake = Bake, MaxSide = MaxSide, BudgetMb = BudgetMb, Note = Note };
        }

        public string Describe()
        {
            return "mask=" + (Bake ? "bake" : "approx") + " side=" + MaxSide.ToString(CultureInfo.InvariantCulture) + " mb=" + BudgetMb.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A parsed <c>de.s7t</c> request for this feature (null / 0 = unchanged).</summary>
    internal sealed class ColorMaskCommand
    {
        public bool? Bake;
        public int Side, Mb;
        public bool Any { get { return Bake.HasValue || Side > 0 || Mb > 0; } }
    }

    internal static class ColorMaskRules
    {
        /// <summary>SE's run-time <c>TextureDebugMultipliers.ColorizeHSV</c> (MyTextureDebugMultipliers.Defaults), added to every key colour.</summary>
        public const float ColorizeH = 0f, ColorizeS = 0.8f, ColorizeV = -0.1f;

        public const int DefaultSide = 512, MinSide = 64, MaxSideLimit = 2048;
        public const int DefaultBudgetMb = 256, MinBudgetMb = 16, MaxBudgetMb = 4096;
        public const string ModeVar = "DEEPENGINEERS_SN_MASK", SideVar = "DEEPENGINEERS_SN_MASK_SIDE", MbVar = "DEEPENGINEERS_SN_MASK_MB";
        public const string Cmd = "de.s7t";
        public const string Usage = "de.s7t mask bake|approx|status | mask side <64..2048> | mask mb <16..4096> (or mask=…, maskSide=…, maskMb=…)";

        /// <summary>A slot not seen on any block renderer for this many full scans goes back to the S3 maps and frees its bake.</summary>
        public const int StaleCycles = 2;

        /// <summary>The name prefix of P9's character bakes (S7pCharacterPaint): a slot carrying one is the character's, never touched here.</summary>
        public const string CharacterTexturePrefix = "DE.S7p paint";

        /// <summary>SE's standard grey (the comment in PixelUtilsMaterials.hlsli: sRGB 144): the reference texel of the log and the trace.</summary>
        public const double StandardGreySrgb = 144.0 / 255.0;

        // ------------------------------------------------------------------------------------------ SE's formula

 /// <summary>
 /// The key the shader adds to a texel's (0, 0, value): the part's <c>ColorMaskHSV</c> plus SE's default
 /// <c>ColorizeHSV</c>, in float as the shader adds them (<c>coloringHSV += …ColorizeHSV.rgb</c>).
 /// </summary>
        public static void ShaderKey(float h, float s, float v, out float kh, out float ks, out float kv)
        {
            kh = h + ColorizeH;
            ks = s + ColorizeS;
            kv = v + ColorizeV;
        }

        /// <summary>
 /// One texel as SE draws it: <paramref name="r"/>, <paramref name="g"/>, <paramref name="b"/> linear (an sRGB texture
 /// as sampled), <paramref name="paint"/> the <c>_add</c> alpha, <paramref name="metal"/> the <c>_cm</c> alpha; the
 /// result linear. P9's <see cref="S7pPaint"/> helpers do the arithmetic on the shader key.
 /// </summary>
        public static void Texel(double r, double g, double b, double paint, double metal, bool metalnessColorable, float h, float s, float v,
                                 out double or, out double og, out double ob)
        {
            float kh, ks, kv;
            ShaderKey(h, s, v, out kh, out ks, out kv);
            S7pPaint.ColorizeGray(r, g, b, kh, ks, kv, S7pPaint.Coloring(paint, metal, metalnessColorable), out or, out og, out ob);
        }

        /// <summary>
 /// SE's paint baked into a level-0 RGBA8 sRGB image (see <see cref="S7pPaint.Bake"/>: <c>_cm</c> as sRGB bytes with
 /// the metalness in A, <c>_add</c> with the paint mask in A; unpainted texels copied byte for byte).
 /// </summary>
        public static byte[] Bake(byte[] cmSrgb, byte[] add, int texels, float h, float s, float v, bool metalnessColorable, byte[] illum, out PaintBakeStats stats)
        {
            float kh, ks, kv;
            ShaderKey(h, s, v, out kh, out ks, out kv);
            return S7pPaint.Bake(cmSrgb, add, texels, kh, ks, kv, metalnessColorable, illum, out stats);
        }

        /// <summary>The sRGB bytes SE's standard grey takes fully painted with this colour (what SE's colour picker shows); log and trace.</summary>
        public static void GreyPreview(float h, float s, float v, out byte r, out byte g, out byte b)
        {
            float kh, ks, kv;
            ShaderKey(h, s, v, out kh, out ks, out kv);
            double lr, lg, lb;
            S7pPaint.Colourised(StandardGreySrgb, kh, ks, kv, out lr, out lg, out lb);
            r = S7pPaint.SrgbByteOf(lr);
            g = S7pPaint.SrgbByteOf(lg);
            b = S7pPaint.SrgbByteOf(lb);
        }

        public static string PreviewText(float h, float s, float v)
        {
            byte r, g, b;
            GreyPreview(h, s, v, out r, out g, out b);
            return r + "," + g + "," + b;
        }

        // ------------------------------------------------------------------------------------------ sizes and budget

 /// <summary>Top mip levels left out so that the baked albedo's largest side is ≤ <paramref name="maxSide"/> (≥ 0; never below 1×1).</summary>
        public static int BakeLevel(int w, int h, int maxSide)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            if (maxSide < 1) maxSide = 1;
            int k = 0;
            while (Math.Max(Math.Max(1, w >> k), Math.Max(1, h >> k)) > maxSide && ((w >> k) > 1 || (h >> k) > 1)) k++;
            return k;
        }

        /// <summary>
 /// Where level <paramref name="level"/> starts in a full RGBA32 chain of a w×h image (<see cref="TextureRepack.BuildMips"/>
 /// layout) and the size, level count and bytes of the chain from there.
 /// </summary>
        public static void SubChain(int w, int h, int level, out long offset, out int w2, out int h2, out int mips2, out long bytes)
        {
            int full = TextureRepack.FullMipCount(w, h);
            if (level < 0) level = 0;
            if (level > full - 1) level = full - 1;
            offset = TextureRepack.ChainBytes(w, h, level);
            w2 = Math.Max(1, w >> level);
            h2 = Math.Max(1, h >> level);
            mips2 = full - level;
            bytes = TextureRepack.ChainBytes(w2, h2, mips2);
        }

        /// <summary>The texture memory one bake holds: the albedo chain from <paramref name="level"/>, twice with the coloured emission.</summary>
        public static long BakeBytes(int w, int h, int level, bool illum)
        {
            long off, bytes;
            int w2, h2, m2;
            SubChain(w, h, level, out off, out w2, out h2, out m2, out bytes);
            return illum ? bytes * 2 : bytes;
        }

        /// <summary>A new bake fits when the bytes held plus its own stay within the budget.</summary>
        public static bool Admit(long usedBytes, long needBytes, long budgetBytes)
        {
            if (needBytes <= 0) return true;
            return usedBytes + needBytes <= budgetBytes;
        }

        /// <summary>
 /// The bake to free first: the least recently used one that no slot carries and that is settled (ready or failed);
 /// -1 when there is none. The three lists are parallel.
 /// </summary>
        public static int PickVictim(IList<int> refs, IList<bool> settled, IList<int> lastUsedFrame)
        {
            int best = -1;
            for (int i = 0; i < refs.Count; i++)
            {
                if (refs[i] > 0 || !settled[i]) continue;
                if (best < 0 || lastUsedFrame[i] < lastUsedFrame[best]) best = i;
            }
            return best;
        }

        /// <summary>
 /// The METALNESS_COLORABLE switch of a slot from the part flags seen on its renderers: on only when every user is
 /// colourable (a slot shared by both kinds keeps SE's metal reduction, counted as mixed).
 /// </summary>
        public static bool DesiredMc(bool mcSeen, bool nonMcSeen) { return mcSeen && !nonMcSeen; }

        public static bool Stale(int seenCycle, int cycle) { return cycle - seenCycle >= StaleCycles; }

        /// <summary>The bake's cache key: (texture set, colour, metalness-colourable), P9's key form with this feature's tag.</summary>
        public static string BakeKey(string setKey, long colourKey, bool metalnessColorable)
        {
            return "blk#" + S7pPaint.BakeKey(setKey, colourKey, metalnessColorable);
        }

        // ------------------------------------------------------------------------------------------ settings, env, command

        public static bool ValidSide(int v) { return v >= MinSide && v <= MaxSideLimit; }
        public static bool ValidMb(int v) { return v >= MinBudgetMb && v <= MaxBudgetMb; }

        /// <summary>The settings at start from <c>DEEPENGINEERS_SN_MASK</c> (bake|approx), <c>…_MASK_SIDE</c>, <c>…_MASK_MB</c>; bad values keep the default (noted).</summary>
        public static ColorMaskSettings FromEnvironment(string mode, string side, string mb, out string note)
        {
            var s = new ColorMaskSettings();
            var notes = new List<string>();
            if (!string.IsNullOrEmpty(mode))
            {
                bool bake;
                if (S7pPaint.TryParseMode(mode, out bake)) { s.Bake = bake; notes.Add(ModeVar + "=" + mode); }
                else notes.Add(ModeVar + "='" + mode + "' not understood (bake|approx): bake");
            }
            int n;
            if (!string.IsNullOrEmpty(side))
            {
                if (TryInt(side, out n) && ValidSide(n)) { s.MaxSide = n; notes.Add(SideVar + "=" + n); }
                else notes.Add(SideVar + "='" + side + "' outside " + MinSide + ".." + MaxSideLimit + ": " + DefaultSide);
            }
            if (!string.IsNullOrEmpty(mb))
            {
                if (TryInt(mb, out n) && ValidMb(n)) { s.BudgetMb = n; notes.Add(MbVar + "=" + n); }
                else notes.Add(MbVar + "='" + mb + "' outside " + MinBudgetMb + ".." + MaxBudgetMb + ": " + DefaultBudgetMb);
            }
            note = notes.Count == 0 ? "defaults (S7t)" : string.Join("; ", notes.ToArray());
            s.Note = note;
            return s;
        }

        private static bool TryInt(object o, out int v)
        {
            v = 0;
            if (o is double) { double d = (double)o; if (double.IsNaN(d) || d < int.MinValue || d > int.MaxValue || Math.Floor(d) != d) return false; v = (int)d; return true; }
            if (o is int) { v = (int)o; return true; }
            if (o is long) { long l = (long)o; if (l < int.MinValue || l > int.MaxValue) return false; v = (int)l; return true; }
            var s = o as string;
            if (s == null) return false;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        private static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        /// <summary>
 /// <c>de.s7t</c> is shared by the S7t features: this one answers only the keys <c>mask</c>, <c>maskSide</c>,
 /// <c>maskMb</c>, the action <c>mask</c> or a chat relay whose first word starts with <c>mask</c>.
 /// </summary>
        public static bool IsMine(ICollection<string> keys, string action, IList<string> words)
        {
            if (keys != null) foreach (string k in keys) if (Is(k, "mask") || Is(k, "maskSide") || Is(k, "maskMb")) return true;
            if (action != null && Is(action.Trim(), "mask")) return true;
            if (words != null && words.Count > 0 && words[0] != null && words[0].Trim().StartsWith("mask", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>One key/value onto the command (keys case-insensitive: mask, maskSide|side, maskMb|mb).</summary>
        private static bool Put(ColorMaskCommand c, string key, object value, out string error)
        {
            error = null;
            string k = (key ?? "").Trim().ToLowerInvariant();
            if (k == "mask")
            {
                string s = value as string;
                if (s == null && value is bool) s = (bool)value ? "bake" : "approx";
                if (s == null) { error = "mask: bake | approx | status"; return false; }
                s = s.Trim().ToLowerInvariant();
                if (s == "status" || s == "") return true;
                bool bake;
                if (!S7pPaint.TryParseMode(s, out bake)) { error = "mask: bake | approx | status"; return false; }
                c.Bake = bake;
                return true;
            }
            int n;
            if (k == "maskside" || k == "side")
            {
                if (!TryInt(value, out n) || !ValidSide(n)) { error = "maskSide: " + MinSide + ".." + MaxSideLimit; return false; }
                c.Side = n;
                return true;
            }
            if (k == "maskmb" || k == "mb")
            {
                if (!TryInt(value, out n) || !ValidMb(n)) { error = "maskMb: " + MinBudgetMb + ".." + MaxBudgetMb; return false; }
                c.Mb = n;
                return true;
            }
            error = "unknown key '" + key + "'";
            return false;
        }

        /// <summary>
 /// The request as a C11 dictionary (<c>{mask: "approx"}</c>, <c>{maskSide: 1024}</c>, <c>{maskMb: 512}</c>, <c>{action:
 /// "mask"}</c>) and/or a chat relay's words (<c>mask approx</c>, <c>mask side 1024</c>, <c>mask mb 512</c>,
 /// <c>mask=approx</c>, <c>maskSide=1024</c>, a lone <c>mask</c> = status). Keys of the other S7t features are ignored.
 /// </summary>
        public static bool TryParse(IDictionary<string, object> dict, IList<string> words, out ColorMaskCommand cmd, out string error)
        {
            cmd = new ColorMaskCommand();
            error = null;
            if (dict != null)
            {
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    if (!(Is(kv.Key, "mask") || Is(kv.Key, "maskSide") || Is(kv.Key, "maskMb"))) continue;
                    if (!Put(cmd, kv.Key, kv.Value, out error)) return false;
                }
            }
            if (words == null) return true;
            int i = 0;
            while (i < words.Count)
            {
                string t = (words[i] ?? "").Trim();
                if (t.Length == 0) { i++; continue; }
                int eq = t.IndexOf('=');
                if (eq > 0)
                {
                    string k = t.Substring(0, eq);
                    if (Is(k, "mask") || Is(k, "maskSide") || Is(k, "maskMb"))
                    {
                        if (!Put(cmd, k, t.Substring(eq + 1), out error)) return false;
                    }
                    i++;
                    continue;
                }
                if (Is(t, "mask"))
                {
                    if (i + 1 < words.Count && (words[i + 1] ?? "").IndexOf('=') < 0)
                    {
                        string u = (words[i + 1] ?? "").Trim();
                        if (Is(u, "side") || Is(u, "mb"))
                        {
                            if (i + 2 >= words.Count) { error = "mask " + u + ": a number (" + Usage + ")"; return false; }
                            if (!Put(cmd, u, words[i + 2], out error)) return false;
                            i += 3;
                            continue;
                        }
                        if (!Put(cmd, "mask", u, out error)) return false;
                        i += 2;
                        continue;
                    }
                    i++;
                    continue;
                }
                if (Is(t, "maskSide") || Is(t, "maskMb"))
                {
                    if (i + 1 >= words.Count) { error = t + ": a number"; return false; }
                    if (!Put(cmd, t, words[i + 1], out error)) return false;
                    i += 2;
                    continue;
                }
                error = "'" + t + "' not understood (" + Usage + ")";
                return false;
            }
            return true;
        }

        /// <summary>
 /// The command onto the settings: true when the existing bakes must be dropped and made again (the side changed);
 /// <paramref name="modeChanged"/> when bake/approx flipped.
 /// </summary>
        public static bool Apply(ColorMaskSettings s, ColorMaskCommand c, string why, out bool modeChanged)
        {
            modeChanged = false;
            bool rebake = false;
            if (c.Bake.HasValue && c.Bake.Value != s.Bake) { s.Bake = c.Bake.Value; modeChanged = true; }
            if (c.Side > 0 && c.Side != s.MaxSide) { s.MaxSide = c.Side; rebake = true; }
            if (c.Mb > 0) s.BudgetMb = c.Mb;
            if (c.Any) s.Note = why;
            return rebake;
        }
    }
}
