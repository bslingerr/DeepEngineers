using System;
using DeepEngineers.Wire;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>

 /// mirrored astronaut's paint as SE draws it. The S3 material path tints the albedo with one <c>_Color</c> under
 /// UWE_3COLOR computed on SE's 144 grey with the metal reduction always on (S3.C13: ΔE ≈ 17 against SE); a character is
 /// METALNESS_COLORABLE and its suit texels are not that grey, so the astronaut came out another colour. For every
 /// painted character material (<see cref="TextureSet.PaintMax"/> &gt; 0) this class:
 /// <list type="number">
 /// <item>reads the set's own textures back once (main thread): <c>_cm</c> blitted into an <c>R8G8B8A8_SRGB</c> target
 /// (the sRGB bytes SE's <c>rgb_to_srgb</c> sees, full 8-bit precision) and the level-0 <c>_add</c> into
 /// <c>R8G8B8A8_UNorm</c> (its alpha = the paint mask, G = the emissive mask), <c>AsyncGPUReadback</c> in the targets'
 /// own formats (TextureImport's rule);</item>
 /// <item>bakes SE's <c>ColorizeGray</c> per texel for the part's colour on a pool thread (<see cref="S7pPaint.Bake"/>,
 /// the coloured <c>_Illum</c> too when the set is emissive), builds the mip chain (<see cref="TextureRepack.BuildMips"/>,
 /// sRGB-aware) and uploads it through the mirror's upload budget as an sRGB <c>RGBA32</c> texture;</item>
 /// <item>puts it on the character's material slot as <c>_MainTex</c> with UWE_3COLOR off and <c>_Color</c> /
 /// <c>_SpecColor</c> white (the paint is in the texels now), again after every <c>ApplyMaps</c> of that slot.</item>
 /// </list>
 /// One bake per (texture set, colour key, metalness-colourable), at most <see cref="MaxBakes"/> kept (least recently used
 /// evicted first, never one on a live slot); a colour change bakes again from the kept readbacks. <c>de.s5 paint=approx</c>
 /// (env <c>DEEPENGINEERS_SN_CHAR_PAINT</c>) restores the S3 tint for comparison. Any failure leaves that slot on the S3
 /// tint (logged once). Main thread except the bake itself.
 /// </summary>
    internal sealed class S7pCharacterPaint
    {
        public static bool BakeOn = true;
        public static string ModeNote = "default bake (S7p)";
        public const int MaxBakes = 12, MaxSources = 8;
        public const float ReadbackTimeoutS = 10f;
        public const int SourceIdleFrames = 1800;

        private sealed class Source
        {
            public TextureSet Set;
            public int W, H;
            public byte[] Cm, Add;
            public int Pending;
            public bool Ready, Failed;
            public string Error;
            public float StartTime;
            public int LastUsedFrame;
        }

        private sealed class Bake
        {
            public string Key;
            public Source Src;
            public float H, S, V;
            public bool Mc, WantIllum;
            /// <summary>0 baking (pool thread), 1 baked, 2 uploading, 3 ready, 4 failed.</summary>
            public volatile int State;
            public byte[] MainChain, IllumChain;
            public int TexW, TexH, Mips;
            public Texture2D Main, Illum;
            public PaintBakeStats Stats;
            public string Error;
            public double CpuMs;
            public int LastUsedFrame;
            public bool FailLogged;
        }

        private sealed class Applied { public Bake Bake; public int Version; public bool Mc; }

        private readonly MirrorConsumer _m;
        private readonly Dictionary<TextureSet, Source> _sources = new Dictionary<TextureSet, Source>();
        private readonly Dictionary<string, Bake> _bakes = new Dictionary<string, Bake>(StringComparer.Ordinal);
        private readonly Dictionary<MaterialSlot, Applied> _applied = new Dictionary<MaterialSlot, Applied>();
        private readonly List<string> _evict = new List<string>();
        private bool _readbackChecked, _readbackOk;
        public long Readbacks, BakesDone, BakesFailed, Overrides, Restores, Evictions;
        public string LastLine = "";

        public S7pCharacterPaint(MirrorConsumer m) { _m = m; }

        public int Baked { get { int n = 0; foreach (Bake b in _bakes.Values) if (b.State == 3) n++; return n; } }
        public int AppliedSlots { get { return _applied.Count; } }

        /// <summary>The environment's mode at start (<c>DEEPENGINEERS_SN_CHAR_PAINT</c>: bake|approx).</summary>
        public static void ReadMode()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(S7pPaint.ModeVar); } catch (Exception) { }
            string note;
            BakeOn = S7pPaint.ModeFromEnvironmentValue(v, out note);
            ModeNote = note;
        }

        // ------------------------------------------------------------------------------------------ per frame (Update)

 /// <summary>Uploads finished bakes, times out stuck readbacks, logs failures once, drops idle sources.</summary>
        public void Tick()
        {
            int frame = Time.frameCount;
            foreach (Bake b in _bakes.Values)
            {
                if (b.State == 1)
                {
                    b.State = 2;
                    Bake captured = b;
                    _m.Uploads.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "S7p paint " + b.Key, Run = () => { Upload(captured); return true; } });
                }
                else if (b.State == 4 && !b.FailLogged)
                {
                    b.FailLogged = true;
                    BakesFailed++;
                    DeLog.Warn("DE.S7p paint bake FAILED " + b.Key + ": " + b.Error + " (that material keeps the S3 tint)");
                }
            }
            foreach (Source s in _sources.Values)
                if (!s.Ready && !s.Failed && s.Pending > 0 && Time.unscaledTime - s.StartTime > ReadbackTimeoutS)
                    FailSource(s, "AsyncGPUReadback did not complete within " + ReadbackTimeoutS + " s");
            if (frame % 300 == 0) DropIdleSources(frame);
        }

        /// <summary>One material slot of a character part (main thread, after its ApplyMaps): bake on demand, then override.</summary>
        public long ToolSkipped;

        public void Apply(MaterialSlot slot, uint partFlags)
        {
            if (slot == null || slot.Mat == null) return;

            // tool's textures came out red/yellow/pink (their mask channel is not the suit's); tools keep SE's base textures
            if ((partFlags & MirrorPartFlags.ClassMask) == MirrorPartClass.Tool)
            {
                ToolSkipped++;
                Applied prev;
                if (_applied.TryGetValue(slot, out prev) && prev != null) Restore(slot, "tool: unpainted");
                return;
            }
            Applied ap;
            _applied.TryGetValue(slot, out ap);
            if (!BakeOn)
            {
                if (ap != null) Restore(slot, "paint=approx");
                return;
            }
            TextureSet set = slot.Set;
            if (set == null || set.ConvertState != TextureSet.Conv.Ready || set.Main == null || !(set.PaintMax > 0f)) return; // glass, unpainted or not converted yet
            if (set.AddL0 == null || set.AddL0.State != RawTexture.St.Ready || set.AddL0.Tex == null) return;
            bool mc = (partFlags & S5Conv.MetalnessColorable) != 0;
            int frame = Time.frameCount;
            // the steady state: this slot already carries its bake (no key string, no allocation per frame)
            if (ap != null && ap.Mc == mc && ap.Version == slot.AppliedVersion && ap.Bake.State == 3 && ReferenceEquals(ap.Bake.Src.Set, set))
            {
                ap.Bake.LastUsedFrame = frame;
                ap.Bake.Src.LastUsedFrame = frame;
                return;
            }
            string key = S7pPaint.BakeKey(set.Key, slot.ColourKey, mc);
            Bake b;
            if (!_bakes.TryGetValue(key, out b))
            {
                Source src = SourceFor(set, frame);
                if (src == null || !src.Ready) return;
                if (_bakes.Count >= MaxBakes) Evict();
                b = StartBake(key, src, slot, mc, set);
            }
            b.LastUsedFrame = frame;
            b.Src.LastUsedFrame = frame;
            if (b.State != 3) return;
            if (ap != null && ReferenceEquals(ap.Bake, b) && ap.Version == slot.AppliedVersion) return;
            Override(slot, b);
            _applied[slot] = new Applied { Bake = b, Version = slot.AppliedVersion, Mc = mc };
        }

        // ------------------------------------------------------------------------------------------ readback

        private Source SourceFor(TextureSet set, int frame)
        {
            Source s;
            if (_sources.TryGetValue(set, out s)) return s.Failed ? null : s;
            if (!_readbackChecked)
            {
                _readbackChecked = true;
                _readbackOk = SystemInfo.supportsAsyncGPUReadback;
                if (!_readbackOk) DeLog.Warn("DE.S7p paint OFF: SystemInfo.supportsAsyncGPUReadback is false (the character keeps the S3 tint)");
            }
            if (!_readbackOk) return null;
            if (_sources.Count >= MaxSources) DropIdleSources(int.MaxValue);
            int w = set.W > 0 ? set.W : set.Main.width, h = set.H > 0 ? set.H : set.Main.height;
            s = new Source { Set = set, W = w, H = h, StartTime = Time.unscaledTime, LastUsedFrame = frame };
            _sources[set] = s;
            try
            {
                Read(s, set.Main, true, bytes => s.Cm = bytes);
                Read(s, set.AddL0.Tex, false, bytes => s.Add = bytes);
            }
            catch (Exception e) { FailSource(s, "blit " + e.GetType().Name + ": " + e.Message); }
            return s;
        }

        /// <summary>A blit of <paramref name="src"/> into a temporary W×H 8-bit target (sRGB or UNorm) read back in its own format.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Read(Source s, Texture src, bool srgb, Action<byte[]> store)
        {
            GraphicsFormat fmt = TextureImport.ConversionTarget(srgb);
            RenderTexture rt = RenderTexture.GetTemporary(s.W, s.H, 0, fmt);
            RenderTexture prev = RenderTexture.active;
            try { Graphics.Blit(src, rt); }
            finally { RenderTexture.active = prev; }
            s.Pending++;
            Readbacks++;
            AsyncGPUReadback.Request(rt, 0, rt.graphicsFormat, req =>
            {
                try
                {
                    if (s.Failed) return;
                    if (req.hasError) { FailSource(s, "AsyncGPUReadback error"); return; }
                    NativeArray<byte> data = req.GetData<byte>(0);
                    var bytes = new byte[data.Length];
                    data.CopyTo(bytes);
                    if (bytes.Length < s.W * s.H * 4) { FailSource(s, "readback of " + bytes.Length + " B for " + s.W + "x" + s.H); return; }
                    store(bytes);
                    s.Pending--;
                    if (s.Pending == 0 && s.Cm != null && s.Add != null) s.Ready = true;
                }
                catch (Exception e) { FailSource(s, "readback callback " + e.GetType().Name + ": " + e.Message); }
                finally
                {
                    try { RenderTexture.ReleaseTemporary(rt); } catch (Exception) { }
                }
            });
        }

        private void FailSource(Source s, string why)
        {
            if (s.Failed) return;
            s.Failed = true;
            s.Error = why;
            s.Cm = s.Add = null;
            DeLog.Warn("DE.S7p paint readback FAILED cm='" + (s.Set != null && s.Set.Def != null ? s.Set.Def.ColorMetal : "?") + "': " + why + " (that material keeps the S3 tint)");
        }

        private void DropIdleSources(int frame)
        {
            List<TextureSet> drop = null;
            foreach (KeyValuePair<TextureSet, Source> kv in _sources)
            {
                bool used = false;
                foreach (Bake b in _bakes.Values) if (ReferenceEquals(b.Src, kv.Value) && b.State < 3) { used = true; break; }
                if (used || kv.Value.Pending > 0) continue;
                if (frame == int.MaxValue || frame - kv.Value.LastUsedFrame > SourceIdleFrames) (drop = drop ?? new List<TextureSet>()).Add(kv.Key);
            }
            if (drop != null) foreach (TextureSet t in drop) _sources.Remove(t);
        }

        // ------------------------------------------------------------------------------------------ bake (pool thread)

        private Bake StartBake(string key, Source src, MaterialSlot slot, bool mc, TextureSet set)
        {
            var b = new Bake
            {
                Key = key, Src = src, H = slot.H, S = slot.S, V = slot.V, Mc = mc, TexW = src.W, TexH = src.H,
                WantIllum = set.Illum != null && set.EmissiveMax > 2f / 255f, State = 0,
            };
            _bakes[key] = b;
            byte[] cm = src.Cm, add = src.Add;
            int w = src.W, h = src.H;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    byte[] illum = b.WantIllum ? new byte[w * h * 4] : null;
                    PaintBakeStats st;
                    byte[] level0 = ColorMaskRules.Bake(cm, add, w * h, b.H, b.S, b.V, b.Mc, illum, out st); // S7t-colour-mask: SE's key = ColorMaskHSV + ColorizeHSV (0, 0.8, -0.1); P9 took the offset as 0 (the pink suit came out white)
                    int mips;
                    b.MainChain = TextureRepack.BuildMips(level0, w, h, true, out mips);
                    if (illum != null) b.IllumChain = TextureRepack.BuildMips(illum, w, h, true, out mips);
                    b.Mips = mips;
                    b.Stats = st;
                    b.CpuMs = sw.Elapsed.TotalMilliseconds;
                    b.State = 1;
                }
                catch (Exception e)
                {
                    b.Error = e.GetType().Name + ": " + e.Message;
                    b.State = 4;
                }
            });
            return b;
        }

        // ------------------------------------------------------------------------------------------ upload and material

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Upload(Bake b)
        {
            Bake cur;
            if (!_bakes.TryGetValue(b.Key, out cur) || !ReferenceEquals(cur, b)) { b.MainChain = b.IllumChain = null; return; } // evicted or cleared meanwhile
            try
            {
                b.Main = MakeTexture("DE.S7p paint " + b.Key, b.MainChain, b.TexW, b.TexH, b.Mips);
                if (b.IllumChain != null) b.Illum = MakeTexture("DE.S7p paint illum " + b.Key, b.IllumChain, b.TexW, b.TexH, b.Mips);
                b.MainChain = b.IllumChain = null;
                b.State = 3;
                BakesDone++;
                var ci = CultureInfo.InvariantCulture;
                LastLine = "DE.S7p paint baked " + b.Key + " hsv=(" + b.H.ToString("0.####", ci) + "," + b.S.ToString("0.####", ci) + "," + b.V.ToString("0.####", ci) + ")" +
                           " metalnessColorable=" + b.Mc + " " + b.TexW + "x" + b.TexH + " mips=" + b.Mips + " painted=" + b.Stats.Painted + "/" + b.Stats.Texels +
                           " meanColoring=" + b.Stats.MeanColoring.ToString("0.###", ci) + " illum=" + (b.Illum != null ? b.Stats.IllumTexels.ToString(ci) + " texels" : "none") +
                           " cpuMs=" + b.CpuMs.ToString("0.#", ci) + " (SE ColorizeGray per texel; UWE_3COLOR off on the character's material)";
                DeLog.Info(LastLine);
            }
            catch (Exception e)
            {
                b.Error = "upload " + e.GetType().Name + ": " + e.Message;
                b.MainChain = b.IllumChain = null;
                DestroyTextures(b);
                b.State = 4;
            }
        }

        private Texture2D MakeTexture(string name, byte[] chain, int w, int h, int mips)
        {
            var tex = _m.Owned.Track(new Texture2D(w, h, TextureFormat.RGBA32, mips, false)); // linear = false: the bytes are sRGB, sampled as SE's base colour
            tex.name = name;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.LoadRawTextureData(chain);
            tex.Apply(false, true);
            return tex;
        }

        private static void SetColour(Material m, string name, Color c) { if (m.HasProperty(name)) m.SetColor(name, c); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Override(MaterialSlot slot, Bake b)
        {
            Material m = slot.Mat;
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", b.Main);
            m.DisableKeyword("UWE_3COLOR");
            SetColour(m, "_Color", Color.white);
            SetColour(m, "_SpecColor", Color.white);
            if (b.Illum != null && m.HasProperty("_Illum")) { m.SetTexture("_Illum", b.Illum); m.EnableKeyword("MARMO_EMISSION"); }
            Overrides++;
        }

        /// <summary>The slot back on the S3 maps (ApplyMaps sets _MainTex, UWE_3COLOR and the tint from its texture set again).</summary>
        private void Restore(MaterialSlot slot, string why)
        {
            _applied.Remove(slot);
            try { _m.Marmoset.ApplyMaps(slot); } catch (Exception) { }
            Restores++;
            if (Restores <= 4) DeLog.Info("DE.S7p paint slot " + slot.Label + " back on the S3 tint (" + why + ")");
        }

        private void Evict()
        {
            Bake oldest = null;
            foreach (Bake b in _bakes.Values)
            {
                if (b.State == 0 || b.State == 2) continue;
                bool live = false;
                foreach (Applied a in _applied.Values) if (ReferenceEquals(a.Bake, b)) { live = true; break; }
                if (live) continue;
                if (oldest == null || b.LastUsedFrame < oldest.LastUsedFrame) oldest = b;
            }
            if (oldest == null) return;
            _bakes.Remove(oldest.Key);
            DestroyTextures(oldest);
            Evictions++;
        }

        private void DestroyTextures(Bake b)
        {
            if (b.Main != null) _m.Owned.Destroy(b.Main);
            if (b.Illum != null) _m.Owned.Destroy(b.Illum);
            b.Main = b.Illum = null;
        }

        /// <summary>The material slots were re-made, the character torn down or the mode switched: everything forgotten, textures destroyed.</summary>
        public void Clear(string why)
        {
            // the shared slot materials go back on their S3 maps first: none keeps a reference to a destroyed bake
            foreach (MaterialSlot s in _applied.Keys) { try { _m.Marmoset.ApplyMaps(s); } catch (Exception) { } }
            foreach (Bake b in _bakes.Values) DestroyTextures(b);
            bool any = _bakes.Count > 0 || _sources.Count > 0;
            _bakes.Clear();
            _sources.Clear();
            _applied.Clear();
            if (any) DeLog.Info("DE.S7p paint cleared (" + why + ")");
        }

        /// <summary>A part's slots are being replaced (a colour change, the part rebuilt): they go back on the S3 maps and their bakes become evictable.</summary>
        public void Release(MaterialSlot[] slots, string why)
        {
            if (slots == null) return;
            foreach (MaterialSlot s in slots) if (s != null && _applied.ContainsKey(s)) Restore(s, why);
        }

        /// <summary>The live switch (<c>de.s5 {paint: bake|approx}</c>): approx restores every overridden slot at once.</summary>
        public void SetMode(bool bake, string why)
        {
            BakeOn = bake;
            ModeNote = why;
            if (!bake)
            {
                var slots = new List<MaterialSlot>(_applied.Keys);
                foreach (MaterialSlot s in slots) Restore(s, why);
            }
            DeLog.Warn("DE.S7p paint=" + (bake ? "bake" : "approx") + " (" + why + ")");
        }

        public string Describe()
        {
            return "paint=" + (BakeOn ? "bake" : "approx") + " (" + ModeNote + ") sources=" + _sources.Count + " bakes=" + _bakes.Count + " baked=" + Baked +
                   " slots=" + _applied.Count + " readbacks=" + Readbacks + " done=" + BakesDone + " failed=" + BakesFailed + " overrides=" + Overrides +
                   " restores=" + Restores + " evictions=" + Evictions;
        }
    }
}
