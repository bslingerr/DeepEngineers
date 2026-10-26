using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace DeepEngineers.SE
{
    /// <summary>One coloured texture SE is asked to bake (S3.C13 reference).</summary>
    internal sealed class ColourExportItem
    {
        public MaterialInfo Material;
        public float H, S, V;
        public string PngPath;
        public string SidecarPath;
        public bool Seen;
        public long Bytes;
    }

    /// <summary>
 /// SE's own colouring of a material, baked by SE's renderer into a PNG (ROADMAP S3 "SE plugin (S3b)", S3.C13):
 /// <c>MyRenderProxy.RenderColoredTextures(List&lt;renderColoredTextureProperties&gt)</c>
 /// with the struct fields <c>ColorMaskHSV</c> (Vector3, the part's <c>InstanceData.ColorMaskHSV.xyz</c>),
 /// <c>TextureColorMetal</c>, <c>TextureAddMaps</c>, <c>TextureAplhaMask</c> (sic), <c>TextureNormalGloss</c> (null: no
 /// normal map export) and <c>PathToSave_ColorAlpha</c>, exactly as SE's OBJ exporter fills it
 /// (<c>MyCubeGrid.CreateMaterialFile</c>: the texture names as the model stores them). The render thread
 /// (<c>VRage.Render11.PostprocessStage.MySaveExportedTextures.RenderColoredTextures</c>) draws SE's
 /// <c>PostprocessColorizeExportedTexture.hlsl</c> into an <c>R8G8B8A8_UNorm_SRgb</c> target capped at 1024 px and
 /// writes a PNG at the path; it waits for the textures to stream in (a one-off render-thread hitch, so the export is a
 /// command, never automatic). Files go to <c>%LOCALAPPDATA%\DeepEngineers\s3\</c> with a JSON sidecar written at
 /// once; <see cref="Poll"/> logs when SE's PNG appears.
 /// </summary>
    internal static class MaterialExport
    {
        public const ulong PollTimeoutMs = 60000;

        private static readonly List<ColourExportItem> Pending = new List<ColourExportItem>();
        private static ulong _pendingSinceMs;
        private static ulong _nextPollMs;
        /// <summary>Sidecars are written on a pool thread; failures are reported by the next <see cref="Poll"/>.</summary>
        private static readonly List<string> SidecarErrors = new List<string>();
        public const ulong PollIntervalMs = 1000;

        public static string Directory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepEngineers", "s3"); }
        }

        /// <summary>Asks SE's renderer to bake the items (update thread), named &lt;stamp&gt;-&lt;material&gt;-&lt;n&gt;_ca.png.</summary>
        public static List<ColourExportItem> Request(List<ColourExportItem> items, ulong nowMs, string stamp)
        {
            return Request(items, nowMs, k => Path.Combine(Directory, stamp + "-" + Safe(items[k].Material != null ? items[k].Material.Name : null) + "-" +
                                                                   k.ToString(CultureInfo.InvariantCulture) + "_ca.png"));
        }

        /// <summary>Asks SE's renderer to bake the items at the PNG paths <paramref name="pngPath"/>(k) (update thread); throws on I/O errors.</summary>
        public static List<ColourExportItem> Request(List<ColourExportItem> items, ulong nowMs, Func<int, string> pngPath)
        {
            var props = new List<renderColoredTextureProperties>(items.Count);
            int n = 0;
            for (int k = 0; k < items.Count; k++)
            {
                ColourExportItem it = items[k];
                MaterialInfo m = it.Material;
                if (m == null || string.IsNullOrEmpty(m.ColorMetalRaw)) continue;
                it.PngPath = pngPath(k);
                string dir = Path.GetDirectoryName(it.PngPath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                string baseName = it.PngPath.EndsWith("_ca.png", StringComparison.OrdinalIgnoreCase) ? it.PngPath.Substring(0, it.PngPath.Length - "_ca.png".Length) : it.PngPath;
                it.SidecarPath = baseName + "_ca.json";
                props.Add(new renderColoredTextureProperties
                {
                    ColorMaskHSV = new Vector3(it.H, it.S, it.V),
                    TextureColorMetal = m.ColorMetalRaw,
                    TextureAddMaps = m.AddMapsRaw,
                    TextureAplhaMask = m.AlphaMaskRaw,
                    TextureNormalGloss = null,
                    PathToSave_ColorAlpha = it.PngPath,
                    PathToSave_NormalGloss = baseName + "_ng.png",
                });
                // the sidecar text is made here, the file written on a pool thread
                string path = it.SidecarPath, text = Sidecar(it);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { File.WriteAllText(path, text, new UTF8Encoding(false)); }
                    catch (Exception e) { lock (SidecarErrors) SidecarErrors.Add(path + ": " + e.GetType().Name + ": " + e.Message); }
                });
                n++;
            }
            if (props.Count > 0)
            {
                MyRenderProxy.RenderColoredTextures(props); // the render thread owns (and clears) the list from here
                lock (Pending)
                {
                    foreach (ColourExportItem it in items) if (it.PngPath != null) Pending.Add(it);
                    _pendingSinceMs = nowMs;
                }
            }
            return items;
        }

        /// <summary>
 /// Update thread, called every tick but working at most once per <see cref="PollIntervalMs"/>: logs PNGs that appeared and sidecar write failures; gives
 /// up after a minute.
 /// </summary>
        public static void Poll(ulong nowMs)
        {
            if (nowMs < _nextPollMs) return;
            _nextPollMs = nowMs + PollIntervalMs;
            lock (SidecarErrors)
            {
                foreach (string err in SidecarErrors) DeLog.Info("DE.S3 colour export sidecar NOT written: " + err);
                SidecarErrors.Clear();
            }
            lock (Pending)
            {
                if (Pending.Count == 0) return;
                for (int i = Pending.Count - 1; i >= 0; i--)
                {
                    ColourExportItem it = Pending[i];
                    try
                    {
                        var fi = new FileInfo(it.PngPath);
                        if (fi.Exists && fi.Length > 0)
                        {
                            it.Seen = true;
                            it.Bytes = fi.Length;
                            DeLog.Info("DE.S3 colour export written " + it.PngPath + " bytes=" + fi.Length + " material=" + it.Material.Name +
                                       " hsv=(" + G(it.H) + "," + G(it.S) + "," + G(it.V) + ") sidecar=" + it.SidecarPath + " (S3.C13 reference)");
                            Pending.RemoveAt(i);
                        }
                    }
                    catch (Exception) { }
                }
                if (Pending.Count > 0 && nowMs - _pendingSinceMs > PollTimeoutMs)
                {
                    foreach (ColourExportItem it in Pending)
                        DeLog.Info("DE.S3 colour export NOT written after " + PollTimeoutMs / 1000 + " s: " + it.PngPath + " (SE's renderer did not run the request)");
                    Pending.Clear();
                }
            }
        }

        private static string Sidecar(ColourExportItem it)
        {
            MaterialInfo m = it.Material;
            var sb = new StringBuilder(512);
            sb.Append("{\"kind\":\"s3.colourExport\",\"utc\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"png\":"); JsonText.AppendString(sb, it.PngPath);
            sb.Append(",\"material\":"); JsonText.AppendString(sb, m.Name);
            sb.Append(",\"materialKey\":"); JsonText.AppendString(sb, S3Rows.Hex(m.Key));
            sb.Append(",\"colorMaskHSV\":[").Append(G(it.H)).Append(',').Append(G(it.S)).Append(',').Append(G(it.V)).Append(']');
            sb.Append(",\"textureColorMetal\":"); JsonText.AppendString(sb, m.ColorMetalRaw ?? "");
            sb.Append(",\"textureAddMaps\":"); JsonText.AppendString(sb, m.AddMapsRaw ?? "");
            sb.Append(",\"textureAlphaMask\":"); JsonText.AppendString(sb, m.AlphaMaskRaw ?? "");
            sb.Append(",\"cmAbsolute\":"); JsonText.AppendString(sb, m.ColorMetal ?? "");
            sb.Append(",\"renderer\":\"MyRenderProxy.RenderColoredTextures -> PostprocessColorizeExportedTexture.hlsl, R8G8B8A8_UNorm_SRgb, <= 1024 px\"");
            sb.Append(",\"uv\":\"the PNG is the whole _cm texture coloured; sample at the part UV: uv / patternScale + uvOffset (s3 part rows)\"}");
            return sb.ToString();
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "material";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        private static string G(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    }
}
