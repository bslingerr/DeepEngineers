using System;
using System.Collections;
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
    /// <summary>The paint state of one block material slot (S7t-colour-mask), hung on <see cref="MaterialSlot.Mask"/>.</summary>
    internal sealed class ColorMaskEntry
    {
        public MaterialSlot Slot;
        /// <summary>Part flags seen on its renderers: METALNESS_COLORABLE on some, off on others.</summary>
        public bool McSeen, NonMcSeen, McMixedCounted;
        /// <summary>The scan cycle that last found the slot on a block renderer.</summary>
        public int SeenCycle;
        public bool Queued, BudgetWaitCounted;
        /// <summary>The bake the slot carries or waits for (one reference in <see cref="ColorMaskBake.Users"/>).</summary>
        public ColorMaskBake Bake;
        /// <summary>The slot's material holds the bake now (UWE_3COLOR off, _MainTex = the bake).</summary>
        public bool Applied;
        /// <summary>P9's character bake was found on the slot: the character's paint owns it, never touched here.</summary>
        public bool CharacterOwned;
        // the desired key's inputs, so the per-visit check allocates nothing
        public TextureSet KeySet;
        public bool KeyMc;
        public string Key;
    }

    /// <summary>One (texture set, colour, metalness-colourable) bake: SE's colour per texel in an sRGB RGBA32 chain.</summary>
    internal sealed class ColorMaskBake
    {
        public const int Baking = 0, Baked = 1, Uploading = 2, Ready = 3, Failed = 4;
        public string Key;
        public TextureSet Set;
        public float H, S, V;
        public bool Mc, WantIllum, InFlight;
        public int Level;
        public volatile int State;
        public byte[] MainChain, IllumChain;
        public int TexW, TexH, Mips, SrcW, SrcH;
        /// <summary>Texture memory reserved for this bake (subtracted when it fails or is destroyed).</summary>
        public long Bytes;
        public Texture2D Main, Illum;
        public PaintBakeStats Stats;
        public string Error;
        public double CpuMs;
        public int LastUsedFrame;
        public bool FailLogged;
        public readonly List<ColorMaskEntry> Users = new List<ColorMaskEntry>();
        public bool Settled { get { return State == Ready || State == Failed; } }
    }

    /// <summary>
 /// S7t-colour-mask (ROADMAP S7t T6): SE's colour mask on the mirrored grids. The S3 path tinted each (SE material ×
 /// colour) slot with one <c>_Color</c> under UWE_3COLOR computed without SE's <c>ColorizeHSV</c> offset (white/yellow
 /// became beige, the pink suit white; <see cref="ColorMaskRules"/>). This class bakes SE's own <c>ColorizeGray</c> per
 /// texel for every block slot in use, P9's way (<see cref="S7pCharacterPaint"/>) with grid-sized caps:
 /// <list type="number">
 /// <item>a round-robin scan of the mirror's views (chunk renderers and S5 child renderers whose material is the slot's,
 /// at most <see cref="ScanPerFrame"/> renderers per frame; ghost and hologram renderers carry other materials and are
 /// skipped) finds the slots in use and the part flags they are drawn with (METALNESS_COLORABLE);</item>
 /// <item>per painted texture set one readback of <c>_cm</c> through an <c>R8G8B8A8_SRGB</c> target and of the level-0
 /// <c>_add</c> through <c>R8G8B8A8_UNorm</c> (at most one new per frame, <see cref="MaxSources"/> held);</item>
 /// <item>per (set, colour, metalness-colourable) one bake on a pool thread (<see cref="MaxInFlight"/> at a time): the
 /// full-size bake, its sRGB-aware mip chain, kept from the level whose side is ≤ <c>maskSide</c>; uploaded through the
 /// mirror's upload budget; the coloured <c>_Illum</c> when the set is emissive;</item>
 /// <item>the slot's material gets it as <c>_MainTex</c> with UWE_3COLOR off and <c>_Color</c>/<c>_SpecColor</c> white,
 /// again after every <see cref="MarmosetMaterials.ApplyMaps"/> (hook); a repaint in SE re-sends the parts with their
 /// new colour (S5's part diff hashes the HSV), so the new colour is a new slot and a new bake: nothing per frame.</item>
 /// </list>
 /// The bakes are capped by <c>maskMb</c> (least recently used free bakes go first; a slot that does not fit keeps the
 /// S3 tint, counted <c>budgetWaits</c>). No material is created here. <c>de.s7t mask approx</c> (env
 /// <c>DEEPENGINEERS_SN_MASK</c>) restores the tint for comparison. Main thread except the bake itself; an exception
 /// disables the feature (logged once) and restores every slot.
 /// </summary>
    internal sealed class ColorMaskPaint
    {
        public const int ScanPerFrame = 1024, QueuePerFrame = 16, ApplyPerFrame = 64;
        public const int MaxInFlight = 2, MaxSources = 4;
        public const int SourceKeepFrames = 300, IdleBakeFrames = 3600, HousekeepEveryFrames = 60;
        public const float ReadbackTimeoutS = 10f;
        public const ulong RowEveryMs = 2000;
        public const int DetailedLogLines = 12, LogEvery = 25;

        private sealed class Source
        {
            public TextureSet Set;
            public int W, H;
            public byte[] Cm, Add;
            public int Pending;
            public bool Ready, Failed;
            public float StartTime;
            public int LastUsedFrame;
        }

        private struct ChildItem
        {
            public ChildRenderer R;
            public uint Flags;
        }

        private readonly MirrorConsumer _m;
        public readonly ColorMaskSettings Settings;
        private readonly List<ColorMaskEntry> _entries = new List<ColorMaskEntry>();
        private readonly Dictionary<string, ColorMaskBake> _bakes = new Dictionary<string, ColorMaskBake>(StringComparer.Ordinal);
        private readonly List<ColorMaskBake> _pending = new List<ColorMaskBake>();
        private readonly Dictionary<TextureSet, Source> _sources = new Dictionary<TextureSet, Source>();
        private readonly HashSet<TextureSet> _failedSets = new HashSet<TextureSet>();
        private readonly Queue<ColorMaskEntry> _want = new Queue<ColorMaskEntry>();
        // scan state (round robin over the views; lists reused, nothing allocated per frame)
        private readonly List<MirrorGridView> _scanViews = new List<MirrorGridView>();
        private readonly List<ChunkView> _scanChunks = new List<ChunkView>();
        private readonly List<ChildItem> _scanChildren = new List<ChildItem>();
        private int _viewIdx, _chunkIdx, _childIdx, _cycle = 1;
        // eviction scratch
        private readonly List<ColorMaskBake> _victims = new List<ColorMaskBake>();
        private readonly List<int> _vRefs = new List<int>();
        private readonly List<bool> _vSettled = new List<bool>();
        private readonly List<int> _vLast = new List<int>();

        private long _usedBytes;
        private int _inFlight, _appliesThisFrame;
        private bool _readbackChecked, _readbackOk, _disabled;
        private string _disabledWhy;
        private ulong _nextRowMs;

        public long SmallerBakes;   //: slots baked below MaxSide because the budget was full
        public long Cycles, ScanCapFrames, Visits, Foreign, ApplyCapDrops, BudgetWaits, Evictions, IdleFreed, StaleReleased, Restores, Overrides;
        public long CharacterSkipped, McMixed, BakesStarted, BakesDone, BakesFailed, Readbacks, SourceWaits, Exceptions;
        public double CpuMsMax;
        public string LastLine = "";

        public ColorMaskPaint(MirrorConsumer m)
        {
            _m = m;
            string mode = null, side = null, mb = null, note;
            try
            {
                mode = Environment.GetEnvironmentVariable(ColorMaskRules.ModeVar);
                side = Environment.GetEnvironmentVariable(ColorMaskRules.SideVar);
                mb = Environment.GetEnvironmentVariable(ColorMaskRules.MbVar);
            }
            catch (Exception) { }
            Settings = ColorMaskRules.FromEnvironment(mode, side, mb, out note);
            DeLog.Info("DE.S7t mask " + Settings.Describe() + " (" + note + "; SE's ColorizeGray per texel with the key ColorMaskHSV + ColorizeHSV (" +
                       G(ColorMaskRules.ColorizeH) + "," + G(ColorMaskRules.ColorizeS) + "," + G(ColorMaskRules.ColorizeV) + "); S7t T6)");
        }

        private static string G(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        public int Baked { get { int n = 0; foreach (ColorMaskBake b in _bakes.Values) if (b.State == ColorMaskBake.Ready) n++; return n; } }

        public int AppliedSlots { get { int n = 0; foreach (ColorMaskEntry e in _entries) if (e.Applied) n++; return n; } }

        // ============================================================================================ per frame

 /// <summary>Main thread, every frame while the host world is up (from <see cref="TextureImport.Tick"/>).</summary>
        public void Tick(ulong nowMs)
        {
            if (_disabled || !_m.Marmoset.Ready) return;
            int frame = Time.frameCount;
            _appliesThisFrame = 0;
            try
            {
                ObservePending(frame);
                TimeoutSources();
                if (Settings.Bake)
                {
                    Scan(frame);
                    ProcessQueue(frame);
                }
                if (frame % HousekeepEveryFrames == 0) Housekeep(frame);
                if (nowMs >= _nextRowMs)
                {
                    _nextRowMs = nowMs + RowEveryMs;
                    if (_entries.Count > 0 || _bakes.Count > 0) Row();
                }
            }
            catch (Exception e) { Disable("tick " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>
 /// The hook at the end of <see cref="MarmosetMaterials.ApplyMaps"/>: the S3 maps were just put back on the slot
 /// (UWE_3COLOR and the tint), so a ready bake goes on again; a slot not seen by the scan has no entry (the
 /// character's, the ghost's): nothing happens.
 /// </summary>
        public void AfterApplyMaps(MaterialSlot slot)
        {
            ColorMaskEntry e = slot != null ? slot.Mask : null;
            if (e == null) return;
            e.Applied = false;
            if (_disabled || !Settings.Bake || e.CharacterOwned) return;
            ColorMaskBake b = e.Bake;
            if (b == null || b.State != ColorMaskBake.Ready) return;
            if (!string.Equals(DesiredKey(e), b.Key, StringComparison.Ordinal)) { Requeue(e); return; }
            ApplyTo(e, false);
        }

        // ============================================================================================ scan

        private void Scan(int frame)
        {
            int budget = ScanPerFrame;
            while (true)
            {
                if (_chunkIdx < _scanChunks.Count)
                {
                    if (budget <= 0) { ScanCapFrames++; return; }
                    VisitChunk(_scanChunks[_chunkIdx++]);
                    budget--;
                    continue;
                }
                if (_childIdx < _scanChildren.Count)
                {
                    if (budget <= 0) { ScanCapFrames++; return; }
                    VisitChild(_scanChildren[_childIdx]);
                    _childIdx++;
                    budget--;
                    continue;
                }
                if (_viewIdx < _scanViews.Count)
                {
                    if (budget <= 0) { ScanCapFrames++; return; }
                    MirrorGridView v = _scanViews[_viewIdx++];
                    _scanChunks.Clear();
                    _scanChildren.Clear();
                    _chunkIdx = _childIdx = 0;
                    budget--;
                    if (v == null || !v.Alive) continue;
                    _scanChunks.AddRange(v.Chunks.Values);
                    foreach (ChildPartView c in v.Children.Values)
                        foreach (ChildRenderer r in c.Renderers) _scanChildren.Add(new ChildItem { R = r, Flags = c.Flags });
                    continue;
                }
                // every view visited: the cycle ends, the next one starts from the views alive now (one cycle per frame at most)
                EndCycle();
                _scanChunks.Clear();
                _scanChildren.Clear();
                _chunkIdx = _childIdx = 0;
                _scanViews.Clear();
                foreach (MirrorGridView v in _m.ViewMap.Values) if (v != null && v.Alive) _scanViews.Add(v);
                _viewIdx = 0;
                // S13 hook (point 3, "the tool skin is still red"): the held tool's slots (character children) get SE's colour per texel like a block child
                if (_m.Character != null) foreach (ChildPartView ch in _m.Character.Children) foreach (ChildRenderer r in ch.Renderers) _scanChildren.Add(new ChildItem { R = r, Flags = ch.Flags });
                return;
            }
        }

        private void VisitChunk(ChunkView c)
        {
            if (c == null || c.Slot == null || c.Renderer == null) return;
            Visits++;
            if (!ReferenceEquals(c.Renderer.sharedMaterial, c.Slot.Mat)) { Foreign++; return; } // the S7a ghost, a P7 hologram
            Touch(c.Slot, (c.Key.Bits & S5Conv.MetalnessColorable) != 0);
        }

        private void VisitChild(ChildItem it)
        {
            ChildRenderer r = it.R;
            if (r == null || r.Slot == null || r.Renderer == null) return;
            Visits++;
            if (!ReferenceEquals(r.Renderer.sharedMaterial, r.Slot.Mat)) { Foreign++; return; }
            Touch(r.Slot, (it.Flags & S5Conv.MetalnessColorable) != 0);
        }

        private void Touch(MaterialSlot slot, bool mc)
        {
            if (slot.Mat == null) return;
            ColorMaskEntry e = slot.Mask;
            if (e == null)
            {
                e = new ColorMaskEntry { Slot = slot, SeenCycle = _cycle };
                slot.Mask = e;
                _entries.Add(e);
            }
            e.SeenCycle = _cycle;
            if (mc) e.McSeen = true; else e.NonMcSeen = true;
            if (!e.Queued && NeedsWork(e)) Requeue(e);
        }

        private void EndCycle()
        {
            _cycle++;
            Cycles++;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                ColorMaskEntry e = _entries[i];
                bool gone = e.Slot == null || e.Slot.Mat == null || !ReferenceEquals(e.Slot.Mask, e);
                bool stale = !gone && ColorMaskRules.Stale(e.SeenCycle, _cycle);
                if (!gone && e.McSeen && e.NonMcSeen && !e.McMixedCounted) { e.McMixedCounted = true; McMixed++; }
                if (!gone && !stale) continue;
                Detach(e, stale); // a destroyed material (MaterialDef changed) is not restored
                if (e.Slot != null && ReferenceEquals(e.Slot.Mask, e)) e.Slot.Mask = null;
                if (stale) StaleReleased++;
                _entries[i] = _entries[_entries.Count - 1];
                _entries.RemoveAt(_entries.Count - 1);
            }
        }

        // ============================================================================================ queue

        private void Requeue(ColorMaskEntry e)
        {
            if (e.Queued) return;
            e.Queued = true;
            _want.Enqueue(e);
        }

        /// <summary>The bake key the slot should carry now; null while its set is not converted, or unpainted (glass, plain metal).</summary>
        private static string DesiredKey(ColorMaskEntry e)
        {
            TextureSet set = e.Slot != null ? e.Slot.Set : null;
            if (set == null || set.ConvertState != TextureSet.Conv.Ready || set.Main == null || !(set.PaintMax > 0f)) return null;
            if (set.AddL0 == null || set.AddL0.State != RawTexture.St.Ready || set.AddL0.Tex == null) return null;
            bool mc = ColorMaskRules.DesiredMc(e.McSeen, e.NonMcSeen);
            if (e.Key != null && ReferenceEquals(e.KeySet, set) && e.KeyMc == mc) return e.Key;
            e.KeySet = set;
            e.KeyMc = mc;
            e.Key = ColorMaskRules.BakeKey(set.Key, e.Slot.ColourKey, mc);
            return e.Key;
        }

        private bool NeedsWork(ColorMaskEntry e)
        {
            if (!Settings.Bake || e.CharacterOwned) return false;
            string key = DesiredKey(e);
            if (key == null) return e.Bake != null;
            if (e.Bake == null || !string.Equals(e.Bake.Key, key, StringComparison.Ordinal)) return true;
            return e.Bake.State == ColorMaskBake.Ready && !e.Applied;
        }

        private void ProcessQueue(int frame)
        {
            int n = _want.Count, examined = 0, newSources = 0;
            bool started = false;
            while (_want.Count > 0 && examined < QueuePerFrame && examined < n)
            {
                ColorMaskEntry e = _want.Dequeue();
                e.Queued = false;
                examined++;
                if (e.Slot == null || e.Slot.Mat == null || !ReferenceEquals(e.Slot.Mask, e) || e.CharacterOwned || !Settings.Bake) continue;
                string key = DesiredKey(e);
                if (key == null) { if (e.Bake != null) Detach(e, true); continue; }
                ColorMaskBake b = e.Bake;
                if (b != null && string.Equals(b.Key, key, StringComparison.Ordinal))
                {
                    if (b.State == ColorMaskBake.Ready && !e.Applied) ApplyTo(e, true);
                    continue;
                }
                if (_bakes.TryGetValue(key, out b))
                {
                    Attach(e, b, frame);
                    if (b.State == ColorMaskBake.Ready) ApplyTo(e, true);
                    continue;
                }
                if (started || _inFlight >= MaxInFlight) { Requeue(e); continue; }
                TextureSet set = e.Slot.Set;
                Source src;
                int st = SourceFor(set, frame, ref newSources, out src);
                if (st < 0) continue; // the readback failed: that slot keeps the S3 tint (logged once)
                if (st == 0) { Requeue(e); continue; }
                bool illum = set.Illum != null && set.EmissiveMax > 2f / 255f;
                int level = ColorMaskRules.BakeLevel(src.W, src.H, Settings.MaxSide);
                long need = ColorMaskRules.BakeBytes(src.W, src.H, level, illum);
                bool admitted = ColorMaskRules.Admit(_usedBytes, need, Settings.BudgetBytes) || EvictFor(need);
                // the log: "mask budget full
 // (256 of 256 MB, 210 bakes)... keeps the S3 tint", 300 waiting): a slot that does not fit at MaxSide is baked at
 // half, then a quarter of it (a quarter, a sixteenth of the memory) before it falls back to the plain tint
                for (int shrink = 1; !admitted && shrink <= 2 && (Settings.MaxSide >> shrink) >= ColorMaskRules.MinSide; shrink++)
                {
                    int l2 = ColorMaskRules.BakeLevel(src.W, src.H, Settings.MaxSide >> shrink);
                    if (l2 == level) continue;
                    long n2 = ColorMaskRules.BakeBytes(src.W, src.H, l2, illum);
                    if (ColorMaskRules.Admit(_usedBytes, n2, Settings.BudgetBytes) || EvictFor(n2)) { level = l2; need = n2; admitted = true; SmallerBakes++; }
                }
                if (!admitted)
                {
                    if (!e.BudgetWaitCounted)
                    {
                        e.BudgetWaitCounted = true;
                        BudgetWaits++;
                        if (BudgetWaits == 1 || BudgetWaits % 50 == 0)
                            DeLog.Warn("DE.S7t mask budget full (" + Mb(_usedBytes) + " of " + Settings.BudgetMb + " MB, " + _bakes.Count + " bakes): slot " + e.Slot.Label +
                                       " keeps the S3 tint (" + BudgetWaits + " waiting so far; de.s7t mask mb <n> or mask side <n>)");
                    }
                    continue; // the next scan cycle asks again
                }
                e.BudgetWaitCounted = false;
                b = StartBake(key, src, e, level, illum, need, frame);
                Attach(e, b, frame);
                started = true;
            }
        }

        private void Attach(ColorMaskEntry e, ColorMaskBake b, int frame)
        {
            if (ReferenceEquals(e.Bake, b)) return;
            if (e.Bake != null) Detach(e, true);
            b.Users.Add(e);
            b.LastUsedFrame = frame;
            e.Bake = b;
        }

        /// <summary>The slot lets go of its bake; <paramref name="restore"/>: its material goes back on the S3 maps.</summary>
        private void Detach(ColorMaskEntry e, bool restore)
        {
            ColorMaskBake b = e.Bake;
            if (b != null)
            {
                b.Users.Remove(e);
                b.LastUsedFrame = Time.frameCount;
            }
            e.Bake = null;
            if (!e.Applied) return;
            e.Applied = false;
            if (!restore || e.Slot == null || e.Slot.Mat == null) return;
            try { _m.Marmoset.ApplyMaps(e.Slot); } catch (Exception) { }
            Restores++;
        }

        // ============================================================================================ material

        private void ApplyTo(ColorMaskEntry e, bool capped)
        {
            if (capped && _appliesThisFrame >= ApplyPerFrame) { ApplyCapDrops++; Requeue(e); return; }
            Material m = e.Slot != null ? e.Slot.Mat : null;
            ColorMaskBake b = e.Bake;
            if (m == null || b == null || b.State != ColorMaskBake.Ready || b.Main == null) return;
            if (IsCharacterSlot(m))
            {
                e.CharacterOwned = true;
                CharacterSkipped++;
                Detach(e, false);
                DeLog.Info("DE.S7t mask slot " + e.Slot.Label + " carries P9's character bake: left to the character's paint");
                return;
            }
            _appliesThisFrame++;
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", b.Main);
            m.DisableKeyword("UWE_3COLOR");
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", Color.white);
            if (b.Illum != null && m.HasProperty("_Illum")) { m.SetTexture("_Illum", b.Illum); m.EnableKeyword("MARMO_EMISSION"); }
            e.Applied = true;
            Overrides++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsCharacterSlot(Material m)
        {
            if (!m.HasProperty("_MainTex")) return false;
            Texture t = m.GetTexture("_MainTex");
            if (t == null) return false;
            string n = t.name;
            return n != null && n.StartsWith(ColorMaskRules.CharacterTexturePrefix, StringComparison.Ordinal);
        }

        // ============================================================================================ readback

 /// <summary>1 ready, 0 not yet (requeue), -1 failed.</summary>
        private int SourceFor(TextureSet set, int frame, ref int newThisFrame, out Source s)
        {
            if (_failedSets.Contains(set)) { s = null; return -1; }
            if (_sources.TryGetValue(set, out s))
            {
                s.LastUsedFrame = frame;
                if (s.Failed) return -1;
                return s.Ready ? 1 : 0;
            }
            if (!_readbackChecked)
            {
                _readbackChecked = true;
                _readbackOk = SystemInfo.supportsAsyncGPUReadback;
                if (!_readbackOk) DeLog.Warn("DE.S7t mask OFF: SystemInfo.supportsAsyncGPUReadback is false (the blocks keep the S3 tint)");
            }
            if (!_readbackOk) return -1;
            if (newThisFrame >= 1 || (_sources.Count >= MaxSources && !DropOldestSource(frame))) { SourceWaits++; return 0; }
            newThisFrame++;
            int w = set.W > 0 ? set.W : set.Main.width, h = set.H > 0 ? set.H : set.Main.height;
            s = new Source { Set = set, W = w, H = h, StartTime = Time.unscaledTime, LastUsedFrame = frame };
            _sources[set] = s;
            Source src = s;
            try
            {
                Read(src, set.Main, true, bytes => src.Cm = bytes);
                Read(src, set.AddL0.Tex, false, bytes => src.Add = bytes);
            }
            catch (Exception ex) { FailSource(src, "blit " + ex.GetType().Name + ": " + ex.Message); return -1; }
            return 0;
        }

        /// <summary>A blit of <paramref name="tex"/> into a temporary W×H 8-bit target (sRGB or UNorm) read back in its own format (TextureImport's rule).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Read(Source s, Texture tex, bool srgb, Action<byte[]> store)
        {
            GraphicsFormat fmt = TextureImport.ConversionTarget(srgb);
            RenderTexture rt = RenderTexture.GetTemporary(s.W, s.H, 0, fmt);
            RenderTexture prev = RenderTexture.active;
            try { Graphics.Blit(tex, rt); }
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
                catch (Exception ex) { FailSource(s, "readback callback " + ex.GetType().Name + ": " + ex.Message); }
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
            s.Cm = s.Add = null;
            if (s.Set != null)
            {
                _failedSets.Add(s.Set);
                Source cur;
                if (_sources.TryGetValue(s.Set, out cur) && ReferenceEquals(cur, s)) _sources.Remove(s.Set);
            }
            DeLog.Warn("DE.S7t mask readback FAILED cm='" + (s.Set != null && s.Set.Def != null ? s.Set.Def.ColorMetal : "?") + "': " + why + " (that material keeps the S3 tint)");
        }

        private void TimeoutSources()
        {
            if (_sources.Count == 0) return;
            Source late = null;
            foreach (Source s in _sources.Values)
                if (!s.Ready && !s.Failed && s.Pending > 0 && Time.unscaledTime - s.StartTime > ReadbackTimeoutS) { late = s; break; }
            if (late != null) FailSource(late, "AsyncGPUReadback did not complete within " + ReadbackTimeoutS + " s");
        }

        /// <summary>Room for one more source: the ready one used longest ago (not this frame) is dropped; false when all are still reading.</summary>
        private bool DropOldestSource(int frame)
        {
            TextureSet oldest = null;
            int when = int.MaxValue;
            foreach (KeyValuePair<TextureSet, Source> kv in _sources)
                if (kv.Value.Ready && kv.Value.LastUsedFrame < frame && kv.Value.LastUsedFrame < when) { when = kv.Value.LastUsedFrame; oldest = kv.Key; }
            if (oldest == null) return false;
            _sources.Remove(oldest);
            return true;
        }

        // ============================================================================================ bake

        private ColorMaskBake StartBake(string key, Source src, ColorMaskEntry e, int level, bool illum, long need, int frame)
        {
            var b = new ColorMaskBake
            {
                Key = key, Set = src.Set, H = e.Slot.H, S = e.Slot.S, V = e.Slot.V, Mc = e.KeyMc, WantIllum = illum, Level = level,
                Bytes = need, State = ColorMaskBake.Baking, LastUsedFrame = frame, SrcW = src.W, SrcH = src.H, InFlight = true,
            };
            _bakes[key] = b;
            _pending.Add(b);
            _usedBytes += need;
            _inFlight++;
            BakesStarted++;
            src.LastUsedFrame = frame;
            byte[] cm = src.Cm, add = src.Add;
            int w = src.W, h = src.H;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    byte[] illumL0 = b.WantIllum ? new byte[w * h * 4] : null;
                    PaintBakeStats st;
                    byte[] level0 = ColorMaskRules.Bake(cm, add, w * h, b.H, b.S, b.V, b.Mc, illumL0, out st);
                    int mips;
                    byte[] chain = TextureRepack.BuildMips(level0, w, h, true, out mips);
                    long off, bytes;
                    int w2, h2, m2;
                    ColorMaskRules.SubChain(w, h, b.Level, out off, out w2, out h2, out m2, out bytes);
                    b.MainChain = Slice(chain, off, bytes);
                    if (illumL0 != null) b.IllumChain = Slice(TextureRepack.BuildMips(illumL0, w, h, true, out mips), off, bytes);
                    b.TexW = w2;
                    b.TexH = h2;
                    b.Mips = m2;
                    b.Stats = st;
                    b.CpuMs = sw.Elapsed.TotalMilliseconds;
                    b.State = ColorMaskBake.Baked;
                }
                catch (Exception ex)
                {
                    b.Error = ex.GetType().Name + ": " + ex.Message;
                    b.State = ColorMaskBake.Failed;
                }
            });
            return b;
        }

        private static byte[] Slice(byte[] chain, long offset, long bytes)
        {
            if (offset == 0 && bytes == chain.Length) return chain;
            var r = new byte[bytes];
            Buffer.BlockCopy(chain, (int)offset, r, 0, (int)bytes);
            return r;
        }

        /// <summary>Finished pool bakes: queued for upload, or failed (bytes released, logged once, the users keep the tint).</summary>
        private void ObservePending(int frame)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                ColorMaskBake b = _pending[i];
                int st = b.State;
                if (st == ColorMaskBake.Baking) continue;
                _pending.RemoveAt(i);
                if (b.InFlight) { b.InFlight = false; _inFlight--; }
                if (b.CpuMs > CpuMsMax) CpuMsMax = b.CpuMs;
                if (st == ColorMaskBake.Baked)
                {
                    b.State = ColorMaskBake.Uploading;
                    ColorMaskBake captured = b;
                    _m.Uploads.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "S7t mask " + b.Key, Run = () => { Upload(captured); return true; } });
                }
                else if (st == ColorMaskBake.Failed) Fail(b, b.Error);
            }
        }

        private void Fail(ColorMaskBake b, string why)
        {
            b.State = ColorMaskBake.Failed;
            b.MainChain = b.IllumChain = null;
            _usedBytes -= b.Bytes;
            b.Bytes = 0;
            BakesFailed++;
            if (!b.FailLogged)
            {
                b.FailLogged = true;
                DeLog.Warn("DE.S7t mask bake FAILED " + b.Key + ": " + why + " (those blocks keep the S3 tint)");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Upload(ColorMaskBake b)
        {
            ColorMaskBake cur;
            if (!_bakes.TryGetValue(b.Key, out cur) || !ReferenceEquals(cur, b)) { b.MainChain = b.IllumChain = null; return; } // cleared or evicted meanwhile
            try
            {
                b.Main = MakeTexture("DE.S7t mask " + b.Key, b.MainChain, b.TexW, b.TexH, b.Mips);
                if (b.IllumChain != null) b.Illum = MakeTexture("DE.S7t mask illum " + b.Key, b.IllumChain, b.TexW, b.TexH, b.Mips);
                b.MainChain = b.IllumChain = null;
                b.State = ColorMaskBake.Ready;
                BakesDone++;
                if (BakesDone <= DetailedLogLines || BakesDone % LogEvery == 0)
                {
                    var ci = CultureInfo.InvariantCulture;
                    float kh, ks, kv;
                    ColorMaskRules.ShaderKey(b.H, b.S, b.V, out kh, out ks, out kv);
                    LastLine = "DE.S7t mask baked #" + BakesDone + " cm='" + (b.Set != null && b.Set.Def != null ? b.Set.Def.ColorMetal : "?") + "' colorMaskHSV=(" + G(b.H) + "," + G(b.S) + "," + G(b.V) +
                               ") key=(" + G(kh) + "," + G(ks) + "," + G(kv) + ") seGrey144=(" + ColorMaskRules.PreviewText(b.H, b.S, b.V) + ") metalnessColorable=" + b.Mc +
                               " " + b.SrcW + "x" + b.SrcH + "→" + b.TexW + "x" + b.TexH + " mips=" + b.Mips + " painted=" + b.Stats.Painted + "/" + b.Stats.Texels +
                               " meanColoring=" + b.Stats.MeanColoring.ToString("0.###", ci) + " illum=" + (b.Illum != null ? "yes" : "no") + " cpuMs=" + b.CpuMs.ToString("0.#", ci) +
                               " used=" + Mb(_usedBytes) + "/" + Settings.BudgetMb + " MB (S7t T6)";
                    DeLog.Info(LastLine);
                }
                for (int i = 0; i < b.Users.Count; i++)
                {
                    ColorMaskEntry e = b.Users[i];
                    if (ReferenceEquals(e.Bake, b) && !e.Applied) ApplyTo(e, true);
                }
            }
            catch (Exception ex)
            {
                b.MainChain = b.IllumChain = null;
                DestroyTextures(b);
                Fail(b, "upload " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private Texture2D MakeTexture(string name, byte[] chain, int w, int h, int mips)
        {
            var tex = _m.Owned.Track(new Texture2D(w, h, TextureFormat.RGBA32, mips, false)); // linear = false: sRGB bytes, SE's base colour
            tex.name = name;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.LoadRawTextureData(chain);
            tex.Apply(false, true);
            return tex;
        }

        private void DestroyTextures(ColorMaskBake b)
        {
            if (b.Main != null) _m.Owned.Destroy(b.Main);
            if (b.Illum != null) _m.Owned.Destroy(b.Illum);
            b.Main = b.Illum = null;
        }

        private void DestroyBake(ColorMaskBake b)
        {
            _bakes.Remove(b.Key);
            DestroyTextures(b);
            _usedBytes -= b.Bytes;
            b.Bytes = 0;
            b.MainChain = b.IllumChain = null;
        }

        /// <summary>
 /// Frees the least recently used free bakes until <paramref name="need"/> more bytes fit (0: until the bytes held are
 /// within the budget again, after a lower <c>maskMb</c>); false when it cannot.
 /// </summary>
        private bool EvictFor(long need)
        {
            while (_usedBytes + Math.Max(0L, need) > Settings.BudgetBytes)
            {
                _victims.Clear(); _vRefs.Clear(); _vSettled.Clear(); _vLast.Clear();
                foreach (ColorMaskBake b in _bakes.Values)
                {
                    _victims.Add(b);
                    _vRefs.Add(b.Users.Count);
                    _vSettled.Add(b.Settled);
                    _vLast.Add(b.LastUsedFrame);
                }
                int i = ColorMaskRules.PickVictim(_vRefs, _vSettled, _vLast);
                if (i < 0) return false;
                DestroyBake(_victims[i]);
                Evictions++;
            }
            _victims.Clear();
            return true;
        }

        /// <summary>Every 60 frames: free bakes idle for a minute are destroyed, sources unused for 5 s dropped.</summary>
        private void Housekeep(int frame)
        {
            _victims.Clear();
            foreach (ColorMaskBake b in _bakes.Values)
                if (b.Users.Count == 0 && b.Settled && frame - b.LastUsedFrame > IdleBakeFrames) _victims.Add(b);
            foreach (ColorMaskBake b in _victims) { DestroyBake(b); IdleFreed++; }
            _victims.Clear();
            List<TextureSet> drop = null;
            foreach (KeyValuePair<TextureSet, Source> kv in _sources)
                if (kv.Value.Ready && frame - kv.Value.LastUsedFrame > SourceKeepFrames) (drop = drop ?? new List<TextureSet>()).Add(kv.Key);
            if (drop != null) foreach (TextureSet t in drop) _sources.Remove(t);
        }

        // ============================================================================================ lifecycle

 /// <summary>Every slot material and texture set is being destroyed (world gone, options, mutants): forget everything.</summary>
        public void Clear(string why)
        {
            bool any = _entries.Count > 0 || _bakes.Count > 0;
            foreach (ColorMaskBake b in _bakes.Values) DestroyTextures(b);
            foreach (ColorMaskEntry e in _entries) if (e.Slot != null && ReferenceEquals(e.Slot.Mask, e)) e.Slot.Mask = null;
            _bakes.Clear();
            _pending.Clear();
            _sources.Clear();
            _failedSets.Clear();
            _entries.Clear();
            _want.Clear();
            _scanViews.Clear();
            _scanChunks.Clear();
            _scanChildren.Clear();
            _viewIdx = _chunkIdx = _childIdx = 0;
            _usedBytes = 0;
            _inFlight = 0;
            if (any) DeLog.Info("DE.S7t mask cleared (" + why + ")");
        }

        /// <summary>Every slot back on the S3 maps; <paramref name="dropBakes"/>: the bakes and readbacks destroyed too.</summary>
        private void RestoreAll(bool dropBakes)
        {
            foreach (ColorMaskEntry e in _entries) Detach(e, true);
            if (!dropBakes) return;
            foreach (ColorMaskBake b in _bakes.Values) { DestroyTextures(b); b.MainChain = b.IllumChain = null; }
            _bakes.Clear();
            _pending.Clear(); // pool bakes still running finish into nothing (Upload checks the dictionary)
            _inFlight = 0;
            _usedBytes = 0;
            _sources.Clear();
            _failedSets.Clear();
            foreach (ColorMaskEntry e in _entries) { e.Key = null; e.KeySet = null; e.BudgetWaitCounted = false; }
        }

        /// <summary>An exception in a hook (ApplyMaps): the feature goes off like one in its own tick.</summary>
        public void OnHookException(string where, Exception e)
        {
            Disable(where + " " + e.GetType().Name + ": " + e.Message);
        }

        private void Disable(string why)
        {
            Exceptions++;
            if (_disabled) return;
            _disabled = true;
            _disabledWhy = why;
            DeLog.Error("DE.S7t mask FEATURE DISABLED after an exception (" + why + "): every block slot back on the S3 tint");
            try { RestoreAll(true); } catch (Exception) { }
        }

        // ============================================================================================ command and row

 /// <summary><c>de.s7t {mask | maskSide | maskMb}</c> (and the chat relay's words); other S7t keys go on to their own handler.</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != ColorMaskRules.Cmd) return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            List<string> words = null;
            object v;
            var raw = req.Args as Dictionary<string, object>;
            IList list = null;
            if (raw != null && raw.TryGetValue("argv", out v)) list = v as IList;
            if (list == null) list = S1Args.Unwrap(req.Args) as IList;
            if (list != null)
            {
                words = new List<string>();
                foreach (object o in list) words.Add(o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "");
            }
            var keys = new List<string>();
            foreach (string k in a.Keys) if (k != "argv" && k != "action") keys.Add(k);
            string action = a.TryGetValue("action", out v) ? v as string : null;
            if (!ColorMaskRules.IsMine(keys, action, words)) return false;
            ColorMaskCommand c;
            string error;
            if (ColorMaskRules.TryParse(raw != null ? raw : a, words, out c, out error)) Change(c, "de.s7t");
            if (_disabled && error == null) error = "S7t colour mask is disabled after an exception: " + _disabledWhy;
            reply.Str("cmd", req.Cmd).Str("side", "sn").Bool("ok", error == null);
            if (error != null) reply.Str("error", error).Str("usage", ColorMaskRules.Usage);
            Status(reply);
            return true;
        }

        private void Change(ColorMaskCommand c, string why)
        {
            if (!c.Any) return;
            bool modeChanged;
            bool rebake = ColorMaskRules.Apply(Settings, c, why, out modeChanged);
            if (rebake || (modeChanged && !Settings.Bake)) RestoreAll(rebake);
            if (modeChanged && Settings.Bake) foreach (ColorMaskEntry e in _entries) Requeue(e);
            if (c.Mb > 0) EvictFor(0); // a lower budget frees what no slot carries
            DeLog.Warn("DE.S7t mask " + Settings.Describe() + " (" + why + (rebake ? "; bakes re-made at the new side" : "") + ")");
        }

        public void Status(JsonObjectWriter w)
        {
            w.Str("mask", Settings.Bake ? "bake" : "approx").Num("maskSide", (long)Settings.MaxSide).Num("maskMb", (long)Settings.BudgetMb)
             .Num("maskMbUsed", _usedBytes / 1048576.0).Num("maskSlots", (long)_entries.Count).Num("maskApplied", (long)AppliedSlots)
             .Num("maskBakes", (long)_bakes.Count).Num("maskBaked", (long)Baked).Num("maskBudgetWaits", BudgetWaits).Bool("maskDisabled", _disabled)
             .Str("maskKey", G(ColorMaskRules.ColorizeH) + "," + G(ColorMaskRules.ColorizeS) + "," + G(ColorMaskRules.ColorizeV)).Str("maskLast", LastLine);
        }

        /// <summary>The <c>s7t.mask</c> row (every 2 s while the feature holds slots or bakes; CONTRACT §5.4).</summary>
        private void Row()
        {
            JsonOut j = S5Rows.Begin("s7t.mask");
            j.Str("mode", Settings.Bake ? "bake" : "approx").Num("side", (long)Settings.MaxSide).Num("mb", (long)Settings.BudgetMb).Num("mbUsed", _usedBytes / 1048576.0)
             .Num("slots", (long)_entries.Count).Num("applied", (long)AppliedSlots).Num("bakes", (long)_bakes.Count).Num("baked", (long)Baked)
             .Num("pending", (long)_pending.Count).Num("queue", (long)_want.Count).Num("sources", (long)_sources.Count).Num("inFlight", (long)_inFlight)
             .Num("cycles", Cycles).Num("visits", Visits).Num("foreign", Foreign).Num("scanCapFrames", ScanCapFrames).Num("applyCapDrops", ApplyCapDrops)
             .Num("budgetWaits", BudgetWaits).Num("evictions", Evictions).Num("idleFreed", IdleFreed).Num("staleReleased", StaleReleased)
             .Num("overrides", Overrides).Num("restores", Restores).Num("characterSkipped", CharacterSkipped).Num("mcMixed", McMixed)
             .Num("bakesStarted", BakesStarted).Num("bakesDone", BakesDone).Num("bakesFailed", BakesFailed).Num("readbacks", Readbacks).Num("sourceWaits", SourceWaits)
             .Num("cpuMsMax", CpuMsMax).Bool("disabled", _disabled)
             .Str("key", G(ColorMaskRules.ColorizeH) + "," + G(ColorMaskRules.ColorizeS) + "," + G(ColorMaskRules.ColorizeV));
            S5Rows.End(j);
        }

        private static string Mb(long bytes) { return (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture); }

        public string Describe()
        {
            return Settings.Describe() + " used=" + Mb(_usedBytes) + "MB slots=" + _entries.Count + " applied=" + AppliedSlots + " bakes=" + _bakes.Count + " baked=" + Baked +
                   " done=" + BakesDone + " failed=" + BakesFailed + " budgetWaits=" + BudgetWaits + " evictions=" + Evictions + (_disabled ? " DISABLED" : "");
        }
    }
}
