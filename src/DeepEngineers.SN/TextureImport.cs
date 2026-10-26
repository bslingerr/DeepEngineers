using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire.Native;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>One DDS file uploaded as a Unity texture (the whole skipped chain, or only its first level for conversion).</summary>
    internal sealed class RawTexture
    {
        public enum St { Reading, Uploading, Ready, Failed }

        public string Path;
        public TexRole Role;
        public bool FirstLevelOnly;
        public St State;
        public DdsInfo Info;
        public SkipPlan Plan;
        public byte[] Data;
        public Texture2D Tex;
        public bool Srgb;
        public string Error;
        public int Generation;
    }

    /// <summary>The maps of one SE material: _MainTex from _cm, and the four derived maps from the GPU conversion.</summary>
    internal sealed class TextureSet
    {
        public enum Conv { Waiting, Reading, Repacking, Uploading, Ready, Failed, Off }

        public string Key;
        public MaterialDefData Def;
        public RawTexture Cm, NgL0, AddL0;
        public Conv ConvertState = Conv.Waiting;
        public string ConvertError;
        public int W, H;
        public byte[] CmRb, NgRb, AddRb;
        public int ReadbacksPending;
        public ulong ConvertStartMs;
        public Texture2D Spec, Bump, Mask, Illum;
        public float EmissiveMax, PaintMax;
        public int Version;
        public int Generation;
        public bool M8;
        /// <summary>_MainTex became available (bumps <see cref="Version"/> once, so materials pick it up before the conversion ends).</summary>
        public bool MainSeen;

        public Texture2D Main { get { return Cm != null && Cm.State == RawTexture.St.Ready ? Cm.Tex : null; } }

        public string Describe()
        {
            return "main=" + (Main != null ? "ok" : Cm == null ? "none" : Cm.State.ToString()) + " convert=" + ConvertState +
                   " spec=" + (Spec != null ? "ok" : "-") + " bump=" + (Bump != null ? "ok" : "-") + " mask=" + (Mask != null ? "ok" : "-") +
                   " illum=" + (Illum != null && EmissiveMax > 2f / 255f ? "ok" : "off") + (ConvertError != null ? " error=" + ConvertError : "");
        }
    }

    /// <summary>Worker → main: a DDS read.</summary>
    internal sealed class DdsLoadResult
    {
        public RawTexture Target;
        public int Generation;
        public DdsInfo Info;
        public SkipPlan Plan;
        public byte[] Data;
        public string Error;
    }

    /// <summary>Worker → main: the repacked maps of a texture set.</summary>
    internal sealed class RepackResult
    {
        public TextureSet Target;
        public int Generation;
        public DerivedMaps Maps;
        public string Error;
    }

    /// <summary>One S3.N1d fidelity probe: the same small blit into the conversion's 8-bit target and into a float target.</summary>
    internal sealed class FidelityProbe
    {
        public string Role, Path, Target;
        public byte[] Bytes;
        public float[] Floats;
        public int Pending = 2;
        public bool Failed;
    }

    /// <summary>
 /// SE textures into Subnautica (ARCHITECTURE §5.8 "Texture slots"; CONTRACT §2.5 "Texture colour space"; ROADMAP S3):
 /// <list type="number">
 /// <item>The worker reads the DDS file in place (header, then only the levels whose largest side is ≤ 1024 px).</item>
 /// <item>The main thread uploads it raw (<c>new Texture2D(w, h, fmt, mips, linear)</c>, <c>LoadRawTextureData</c>,
 /// <c>Apply(false, true)</c>) within the upload budget. `_cm` becomes _MainTex as is (BC7 sRGB). The colour space of
 /// every source follows the file (DdsReader.SampleAsSrgb): what SE's shader samples.</item>
 /// <item>_cm, and the first kept level of _ng and _add, are blitted into linear 8-bit RGBA targets (R8G8B8A8_UNorm, the
 /// RenderTextureReadWrite.Linear ARGB32 format; S3.M8 blits _ng into R8G8B8A8_SRGB), read back in the target's own
 /// format with <c>AsyncGPUReadback</c> (main-thread callback),
 /// repacked on the worker (TextureRepack: _SpecTex, _BumpMap X→A Y→G, _MultiColorMask, _Illum, with mips) and
 /// uploaded as RGBA32 (v1; <c>Texture2D.Compress</c> one texture per N frames when configured).</item>
 /// </list>
 /// Textures are shared by path; sets by (cm, ng, add, options). A failure leaves _MainTex alone and is logged once.
 /// </summary>
    internal sealed class TextureImport
    {
        public const ulong ReadbackTimeoutMs = 10000;
        /// <summary>S10 round 3 E: the conversion's readback arrays, reused (at most three 1024² readbacks kept).</summary>
        public static readonly Wire.S10BufferPool S10Readbacks = new Wire.S10BufferPool(Wire.S10AllocCuts.PoolBytes);

        private readonly MirrorWorker _worker;
        private readonly UploadScheduler _uploads;
        private readonly MirrorObjects _owned;
        private readonly Dictionary<string, RawTexture> _raw = new Dictionary<string, RawTexture>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextureSet> _sets = new Dictionary<string, TextureSet>(StringComparer.OrdinalIgnoreCase);
        private int _generation;
        private bool _readbackChecked, _readbackSupported;
        private bool _floatChecked, _floatSupported;

        public S3Options Options;
        public bool M8;
        /// <summary>S7t-colour-mask (T6): SE's colour per texel on the block slots; ticked with the importer, cleared with its textures.</summary>
        public ColorMaskPaint ColorMask;
        public long Uploads, UploadFailures, Readbacks, Repacks, Compressions, FidelityChecks, FidelityFailures;
        public double UploadMsTotal;

        /// <summary>Side of the S3.N1d fidelity probe targets.</summary>
        public const int FidelitySide = 64;

        /// <summary>
 /// The 8-bit conversion target (one rule for the conversion and its fidelity probe): R8G8B8A8_UNorm, the values
 /// stored as sampled; R8G8B8A8_SRGB only for S3.M8's _ng.
 /// </summary>
        public static GraphicsFormat ConversionTarget(bool srgbTarget)
        {
            return srgbTarget ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
        }

        public TextureImport(MirrorWorker worker, UploadScheduler uploads, MirrorObjects owned, S3Options options)
        {
            _worker = worker;
            _uploads = uploads;
            _owned = owned;
            Options = options;
        }

        public int RawCount { get { return _raw.Count; } }
        public int SetCount { get { return _sets.Count; } }

        /// <summary>The set for a MaterialDef (created and started on first use).</summary>
        public TextureSet Request(MaterialDefData def)
        {
            string key = def.ColorMetal + "|" + def.NormalGloss + "|" + def.AddMaps + "|" + Options.MaxTextureSide + "|" + Options.NormalFlipY + "|" + Options.GpuConvert + "|" + M8;
            TextureSet s;
            if (_sets.TryGetValue(key, out s)) return s;
            s = new TextureSet { Key = key, Def = def, Generation = _generation, M8 = M8 };
            _sets[key] = s;
            s.Cm = Raw(def.ColorMetal, TexRole.ColorMetal, false);
            if (Options.GpuConvert)
            {
                s.NgL0 = Raw(def.NormalGloss, TexRole.NormalGloss, true);
                s.AddL0 = Raw(def.AddMaps, TexRole.AddMaps, true);
            }
            else s.ConvertState = TextureSet.Conv.Off;
            return s;
        }

        private RawTexture Raw(string path, TexRole role, bool firstLevelOnly)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "|" + role + "|" + firstLevelOnly + "|" + Options.MaxTextureSide;
            RawTexture r;
            if (_raw.TryGetValue(key, out r)) return r;
            r = new RawTexture { Path = path, Role = role, FirstLevelOnly = firstLevelOnly, State = RawTexture.St.Reading, Generation = _generation };
            _raw[key] = r;
            int maxSide = Options.MaxTextureSide;
            int gen = _generation;
            _worker.Post("dds " + path, () =>
            {
                var res = new DdsLoadResult { Target = r, Generation = gen };
                DdsInfo info;
                SkipPlan plan;
                byte[] data;
                string error;
                if (DdsReader.TryRead(path, maxSide, firstLevelOnly, out info, out plan, out data, out error)) { res.Info = info; res.Plan = plan; res.Data = data; }
                else res.Error = error;
                return res;
            });
            return r;
        }

        /// <summary>Main thread: a worker result of this importer; false when the result is not ours.</summary>
        public bool OnWorkerResult(object o)
        {
            var d = o as DdsLoadResult;
            if (d != null)
            {
                if (d.Generation != _generation) return true;
                RawTexture r = d.Target;
                if (d.Error != null)
                {
                    r.State = RawTexture.St.Failed;
                    r.Error = d.Error;
                    DeLog.Warn("DE.Mirror texture FAILED role=" + r.Role + " path='" + r.Path + "': " + d.Error);
                    return true;
                }
                r.Info = d.Info;
                r.Plan = d.Plan;
                r.Data = d.Data;
                r.Srgb = DdsReader.SampleAsSrgb(d.Info, r.Role);
                r.State = RawTexture.St.Uploading;
                _uploads.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "upload " + r.Path, Run = () => UploadRaw(r) });
                return true;
            }
            var p = o as RepackResult;
            if (p != null)
            {
                if (p.Generation != _generation) return true;
                TextureSet s = p.Target;
                if (p.Error != null) { FailConvert(s, p.Error); return true; }
                Repacks++;
                s.EmissiveMax = p.Maps.EmissiveMax;
                s.PaintMax = p.Maps.PaintMax;
                s.ConvertState = TextureSet.Conv.Uploading;
                DerivedMaps m = p.Maps;
                _uploads.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "spec", Run = () => { s.Spec = UploadDerived("spec", m.Spec, m, false); return true; } });
                _uploads.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "bump", Run = () => { s.Bump = UploadDerived("bump", m.Bump, m, true); return true; } });
                _uploads.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "mask", Run = () => { s.Mask = UploadDerived("mask", m.Mask, m, true); return true; } });
                _uploads.Enqueue(new UploadJob
                {
                    Kind = UploadKind.Texture, Name = "illum",
                    Run = () =>
                    {
                        s.Illum = m.EmissiveMax > 2f / 255f ? UploadDerived("illum", m.Illum, m, false) : null;
                        s.ConvertState = TextureSet.Conv.Ready;
                        s.Version++;
                        DeLog.Info("DE.Mirror textures ready cm='" + s.Def.ColorMetal + "' " + s.Describe() + " size=" + m.Width + "x" + m.Height + " mips=" + m.MipCount +
                                   " emissiveMax=" + m.EmissiveMax.ToString("0.###") + " paintMax=" + m.PaintMax.ToString("0.###") + " metalMean=" + m.MetalMean.ToString("0.###") +
                                   (s.M8 ? " (MUTANT S3.M8: _ng through an sRGB target)" : "") + " normalFlipY=" + Options.NormalFlipY);
                        if (Options.CompressEveryFrames > 0) QueueCompress(s);
                        return true;
                    },
                });
                return true;
            }
            return false;
        }

        private static TextureFormat UnityFormat(DdsFormat f, out bool ok)
        {
            ok = true;
            switch (f)
            {
                case DdsFormat.BC1: return TextureFormat.DXT1;
                case DdsFormat.BC3: return TextureFormat.DXT5;
                case DdsFormat.BC4: return TextureFormat.BC4;
                case DdsFormat.BC5: return TextureFormat.BC5;
                case DdsFormat.BC6H: return TextureFormat.BC6H;
                case DdsFormat.BC7: return TextureFormat.BC7;
                case DdsFormat.RGBA8: return TextureFormat.RGBA32;
                case DdsFormat.BGRA8: case DdsFormat.BGRX8: return TextureFormat.BGRA32;
                case DdsFormat.R8: return TextureFormat.R8;
                default: ok = false; return TextureFormat.RGBA32;
            }
        }

        /// <summary>Upload job: the raw DDS levels as a Unity texture (no conversion).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool UploadRaw(RawTexture r)
        {
            long t0 = Win32.QpcNow();
            try
            {
                bool ok;
                TextureFormat fmt = UnityFormat(r.Info.Format, out ok);
                if (!ok) throw new NotSupportedException("no Unity format for " + r.Info.FormatName);
                if (!SystemInfo.SupportsTextureFormat(fmt)) throw new NotSupportedException("the GPU does not support " + fmt);
                var tex = _owned.Track(new Texture2D(r.Plan.Width, r.Plan.Height, fmt, r.Plan.MipCount, !r.Srgb));
                tex.name = "DE.Mirror " + System.IO.Path.GetFileName(r.Path) + (r.FirstLevelOnly ? " (level 0)" : "");
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Trilinear;
                tex.anisoLevel = 4;
                tex.LoadRawTextureData(r.Data);
                tex.Apply(false, true);
                r.Tex = tex;
                r.Data = null;
                r.State = RawTexture.St.Ready;
                Uploads++;
                double ms = (Win32.QpcNow() - t0) * 1000.0 / Win32.QpcFrequency();
                UploadMsTotal += ms;
                DeLog.Info("DE.Mirror texture role=" + r.Role + " path='" + r.Path + "' format=" + r.Info.FormatName + " sampledAs=" + (r.Srgb ? "sRGB" : "linear") +
                           " file=" + r.Info.Width + "x" + r.Info.Height + "/" + r.Info.MipCount + " uploaded=" + r.Plan + " unity=" + fmt + " uploadMs=" + ms.ToString("0.###"));
            }
            catch (Exception e)
            {
                r.State = RawTexture.St.Failed;
                r.Error = e.GetType().Name + ": " + e.Message;
                r.Data = null;
                UploadFailures++;
                DeLog.Warn("DE.Mirror texture upload FAILED role=" + r.Role + " path='" + r.Path + "': " + r.Error);
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Texture2D UploadDerived(string what, byte[] chain, DerivedMaps m, bool linear)
        {
            var tex = _owned.Track(new Texture2D(m.Width, m.Height, TextureFormat.RGBA32, m.MipCount, linear));
            tex.name = "DE.Mirror " + what;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.LoadRawTextureData(chain);
            tex.Apply(false, Options.CompressEveryFrames <= 0);
            Uploads++;
            return tex;
        }

        private void QueueCompress(TextureSet s)
        {
            foreach (Texture2D t in new[] { s.Spec, s.Bump, s.Mask, s.Illum })
            {
                Texture2D tex = t;
                if (tex == null) continue;
                _uploads.Enqueue(new UploadJob
                {
                    Kind = UploadKind.Compress, Name = "compress " + tex.name,
                    Run = () => { if (tex != null) { tex.Compress(false); tex.Apply(false, true); Compressions++; } return true; },
                });
            }
        }

        private void FailConvert(TextureSet s, string why)
        {
            s.ConvertState = TextureSet.Conv.Failed;
            s.ConvertError = why;
            s.Version++;
            DeLog.Warn("DE.Mirror texture conversion FAILED cm='" + (s.Def != null ? s.Def.ColorMetal : "?") + "': " + why + " (_MainTex only)");
        }

        private static bool Settled(RawTexture r)
        {
            return r == null || r.State == RawTexture.St.Ready || r.State == RawTexture.St.Failed;
        }

        /// <summary>Main thread, every frame: starts the GPU conversion of sets whose sources are settled; times out readbacks.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Tick(ulong nowMs)
        {
            foreach (TextureSet s in _sets.Values)
            {
                if (!s.MainSeen && s.Main != null) { s.MainSeen = true; s.Version++; }
                if (s.ConvertState == TextureSet.Conv.Waiting)
                {
                    if (!Settled(s.Cm) || !Settled(s.NgL0) || !Settled(s.AddL0)) continue;
                    if (s.Cm == null || s.Cm.State != RawTexture.St.Ready) { FailConvert(s, "no _cm texture"); continue; }
                    StartConversion(s, nowMs);
                }
                else if (s.ConvertState == TextureSet.Conv.Reading && nowMs - s.ConvertStartMs > ReadbackTimeoutMs)
                {
                    FailConvert(s, "AsyncGPUReadback did not complete within " + ReadbackTimeoutMs + " ms");
                }
            }
            if (ColorMask != null) ColorMask.Tick(nowMs); // S7t-colour-mask (T6): scan, bakes, uploads; catches its own exceptions
        }

        private void StartConversion(TextureSet s, ulong nowMs)
        {
            if (!_readbackChecked)
            {
                _readbackChecked = true;
                _readbackSupported = SystemInfo.supportsAsyncGPUReadback;
                if (!_readbackSupported) DeLog.Error("DE.Mirror FEATURE gpuConvert DISABLED: SystemInfo.supportsAsyncGPUReadback is false (_MainTex only)");
            }
            if (!_readbackSupported) { FailConvert(s, "AsyncGPUReadback unsupported"); return; }
            s.W = s.Cm.Plan.Width;
            s.H = s.Cm.Plan.Height;
            s.ConvertState = TextureSet.Conv.Reading;
            s.ConvertStartMs = nowMs;
            s.ReadbacksPending = 0;
            int gen = _generation;
            Blit(s, s.Cm.Tex, false, b => s.CmRb = b, gen);
            Fidelity(s.Cm, false, gen);
            if (s.NgL0 != null && s.NgL0.State == RawTexture.St.Ready)
            {
                Blit(s, s.NgL0.Tex, s.M8, b => s.NgRb = b, gen);
                Fidelity(s.NgL0, s.M8, gen);
            }
            if (s.AddL0 != null && s.AddL0.State == RawTexture.St.Ready)
            {
                Blit(s, s.AddL0.Tex, false, b => s.AddRb = b, gen);
                Fidelity(s.AddL0, false, gen);
            }
        }

        /// <summary>
 /// S3.N1d: the same small blit of a converted source into the conversion's own 8-bit
 /// target (<see cref="ConversionTarget"/>) and into an R32G32B32A32_SFloat target (no encoding possible), both read
 /// back in their own formats; the 8-bit values must equal the float ones within 1/255. An sRGB conversion target
 /// (S3.M8) re-encodes the values and fails here whatever the sun does, where the image check N1b cannot see it (the
 /// sRGB curve is monotonic, so the bump ordering survives). Logged as <c>DE.Mirror texture fidelity</c>; never
 /// changes the conversion. Main thread (blits, readback requests; callbacks on the main thread).
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Fidelity(RawTexture src, bool srgbTarget, int gen)
        {
            if (src == null || src.Tex == null) return;
            if (!_floatChecked)
            {
                _floatChecked = true;
                _floatSupported = SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Render);
                if (!_floatSupported) DeLog.Warn("DE.Mirror texture fidelity probe off: R32G32B32A32_SFloat is not a supported render target (S3.N1d MISSING)");
            }
            if (!_floatSupported) return;
            GraphicsFormat f8 = ConversionTarget(srgbTarget);
            var probe = new FidelityProbe { Role = src.Role.ToString(), Path = src.Path, Target = f8.ToString() };
            RenderTexture rt8 = RenderTexture.GetTemporary(FidelitySide, FidelitySide, 0, f8);
            RenderTexture rtf = RenderTexture.GetTemporary(FidelitySide, FidelitySide, 0, GraphicsFormat.R32G32B32A32_SFloat);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src.Tex, rt8);
                Graphics.Blit(src.Tex, rtf);
            }
            finally
            {
                RenderTexture.active = prev;
            }
            FidelityChecks++;
            AsyncGPUReadback.Request(rt8, 0, f8, req =>
            {
                try
                {
                    if (gen != _generation) return;
                    if (req.hasError) { probe.Failed = true; return; }
                    NativeArray<byte> d = req.GetData<byte>(0);
                    probe.Bytes = new byte[d.Length];
                    d.CopyTo(probe.Bytes);
                }
                catch (Exception) { probe.Failed = true; }
                finally
                {
                    try { RenderTexture.ReleaseTemporary(rt8); } catch (Exception) { }
                    probe.Pending--;
                    if (gen == _generation) FinishFidelity(probe);
                }
            });
            AsyncGPUReadback.Request(rtf, 0, GraphicsFormat.R32G32B32A32_SFloat, req =>
            {
                try
                {
                    if (gen != _generation) return;
                    if (req.hasError) { probe.Failed = true; return; }
                    NativeArray<float> d = req.GetData<float>(0);
                    probe.Floats = new float[d.Length];
                    d.CopyTo(probe.Floats);
                }
                catch (Exception) { probe.Failed = true; }
                finally
                {
                    try { RenderTexture.ReleaseTemporary(rtf); } catch (Exception) { }
                    probe.Pending--;
                    if (gen == _generation) FinishFidelity(probe);
                }
            });
        }

        private void FinishFidelity(FidelityProbe p)
        {
            if (p.Pending > 0) return;
            double max, mean;
            int n;
            if (p.Failed || !TextureRepack.FidelityError(p.Bytes, p.Floats, out max, out mean, out n))
            {
                FidelityFailures++;
                DeLog.Warn("DE.Mirror texture fidelity role=" + p.Role + " path='" + p.Path + "' target=" + p.Target + " verdict=MISSING (readback failed) (S3.N1d)");
                return;
            }
            bool pass = max <= TextureRepack.FidelityMaxError;
            if (!pass) FidelityFailures++;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string line = "DE.Mirror texture fidelity role=" + p.Role + " path='" + p.Path + "' target=" + p.Target + " float=R32G32B32A32_SFloat texels=" + n +
                          " maxErr=" + max.ToString("0.######", ci) + " meanErr=" + mean.ToString("0.######", ci) + " verdict=" + (pass ? "PASS" : "FAIL") +
                          (M8 && p.Role == TexRole.NormalGloss.ToString() ? " (MUTANT S3.M8)" : "") +
                          " (S3.N1d: the 8-bit conversion target keeps the sampled values within 1/255)";
            if (pass) DeLog.Info(line); else DeLog.Warn(line);
        }

        /// <summary>
 /// Blit into a temporary 8-bit RGBA target of the set's size and read it back (main-thread callback). The target is
 /// R8G8B8A8_UNorm (what RenderTextureFormat.ARGB32 + RenderTextureReadWrite.Linear gives: no encoding on write),
 /// or R8G8B8A8_SRGB under S3.M8; the readback asks for the target's own GraphicsFormat, so the bytes are copied as
 /// stored (the TextureFormat overload would map RGBA32 to an sRGB format in a Linear-colour-space project).
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Blit(TextureSet s, Texture src, bool srgbTarget, Action<byte[]> store, int gen)
        {
            RenderTexture rt = RenderTexture.GetTemporary(s.W, s.H, 0, ConversionTarget(srgbTarget));
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
            }
            finally
            {
                RenderTexture.active = prev;
            }
            s.ReadbacksPending++;
            Readbacks++;
            AsyncGPUReadback.Request(rt, 0, rt.graphicsFormat, req =>
            {
                try
                {
                    if (gen != _generation || s.ConvertState != TextureSet.Conv.Reading) return;
                    if (req.hasError) { FailConvert(s, "AsyncGPUReadback error"); return; }
                    NativeArray<byte> data = req.GetData<byte>(0);
                    var bytes = Wire.S10AllocCuts.On ? S10Readbacks.Rent(data.Length) : new byte[data.Length]; // S10 hook (round 3 E): pooled readbacks, every byte overwritten
                    data.CopyTo(bytes);
                    store(bytes);
                    s.ReadbacksPending--;
                    if (s.ReadbacksPending == 0) StartRepack(s, gen);
                }
                catch (Exception e)
                {
                    FailConvert(s, "readback callback " + e.GetType().Name + ": " + e.Message);
                }
                finally
                {
                    try { RenderTexture.ReleaseTemporary(rt); } catch (Exception) { }
                }
            });
        }

        private void StartRepack(TextureSet s, int gen)
        {
            s.ConvertState = TextureSet.Conv.Repacking;
            byte[] cm = s.CmRb, ng = s.NgRb, add = s.AddRb;
            int w = s.W, h = s.H;
            bool flipY = Options.NormalFlipY;
            s.CmRb = s.NgRb = s.AddRb = null;
            _worker.Post("repack " + s.Def.ColorMetal, () =>
            {
                var r = new RepackResult { Target = s, Generation = gen };
                try { r.Maps = TextureRepack.Build(cm, ng, add, w, h, flipY); }
                catch (Exception e) { r.Error = "repack " + e.GetType().Name + ": " + e.Message; }
                if (Wire.S10AllocCuts.On) { S10Readbacks.Return(cm); S10Readbacks.Return(ng); S10Readbacks.Return(add); } // S10 hook (round 3 E): the readbacks are read only by Build (the maps are new arrays)
                return r;
            });
        }

        /// <summary>Main thread: destroys every texture of this importer and forgets every set (world gone, options changed, teardown).</summary>
        public void DestroyAll()
        {
            if (ColorMask != null) ColorMask.Clear("textures and materials re-made or torn down"); // S7t-colour-mask (T6): its bakes go with the sets
            _generation++;
            S10Readbacks.Clear(); // S10 hook (round 3 E): the kept readbacks go with the textures
            foreach (RawTexture r in _raw.Values) if (r.Tex != null) _owned.Destroy(r.Tex);
            foreach (TextureSet s in _sets.Values)
            {
                if (s.Spec != null) _owned.Destroy(s.Spec);
                if (s.Bump != null) _owned.Destroy(s.Bump);
                if (s.Mask != null) _owned.Destroy(s.Mask);
                if (s.Illum != null) _owned.Destroy(s.Illum);
            }
            _raw.Clear();
            _sets.Clear();
        }

        public string Describe()
        {
            int ready = 0, failed = 0;
            foreach (RawTexture r in _raw.Values) { if (r.State == RawTexture.St.Ready) ready++; else if (r.State == RawTexture.St.Failed) failed++; }
            return "raw=" + _raw.Count + " (ready " + ready + ", failed " + failed + ") sets=" + _sets.Count + " uploads=" + Uploads + " uploadFailures=" + UploadFailures +
                   " readbacks=" + Readbacks + " repacks=" + Repacks + " compressions=" + Compressions + " fidelity=" + FidelityChecks + "/" + FidelityFailures + " bad";
        }
    }
}
