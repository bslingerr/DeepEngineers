using System;
using System.Collections.Generic;
using System.IO;

namespace DeepEngineers.SE
{
    // =================================================================================================================
 // S13 "Character and animation v2", the SE side's pure rules (ROADMAP §14 "### S13"; docs/S13-BRIEF.md; CONTRACT §5.4
 // "S13 conventions"). Game-free and compiled into DeepEngineers.SE.Tests; the game calls are in S13Se.cs.
 // =================================================================================================================

 /// <summary>The SE side's S13 seeded failures (dev builds; <c>de.mutant S13.Mx on|off</c>).</summary>
    internal static class S13SeMutants
    {
        /// <summary>S13.M3: the held tool's PartAdd colour is (0, 0, 0) again (the red tool: hue 0 + SE's ColorizeHSV saturation 0.8).</summary>
        public static volatile bool ToolColourZero;
        /// <summary>S13.M5: third person keeps the camera as the swim basis (the backwards animation moving forward).</summary>
        public static volatile bool CameraBasis;
        public static readonly string[] All = { "S13.M3", "S13.M5" };

        public static string Active
        {
            get
            {
                var parts = new List<string>(2);
                if (ToolColourZero) parts.Add("S13.M3");
                if (CameraBasis) parts.Add("S13.M5");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll() { ToolColourZero = CameraBasis = false; }

        /// <summary>Switches an S13 SE mutant; the log text, or null when the id is not an S13 id this side owns.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S13.M3": ToolColourZero = on; return "S13.M3 " + state + " (the held tool's PartAdd colour is (0, 0, 0): Subnautica tints it red)";
                case "S13.M5": CameraBasis = on; return "S13.M5 " + state + " (third person swims along the camera, not the body)";
                case "S13.M1": case "S13.M2": case "S13.M4": case "S13.M6": case "S13.M7": case "S13.M8":
                    return "REFUSE " + id.Trim() + ": a Subnautica-side S13 mutant (send de.mutant to Subnautica: /de sn mutant ...)";
                default: return null;
            }
        }
    }

    /// <summary>The swim basis of a tick (S13 point 5).</summary>
    internal enum S13Basis { None = 0, Camera = 1, Body = 2, Head = 3 }

    internal static class S13SeRules
    {
        /// <summary>
 /// S13 point 5: which basis the swim model moves the character in.
 /// fix: what the player sees). Third person: the character's own look (body yaw, head pitch), so a backward key moves

 /// character goes forward"). <paramref name="bodyOn"/> = <c>de.s13 basis body</c> (default); the mutant S13.M5 keeps
 /// the camera.
 /// </summary>
        public static S13Basis Choose(bool cameraIsCharacter, bool firstPerson, bool bodyOn, bool mutantCamera)
        {
            if (!cameraIsCharacter) return S13Basis.Head;
            if (firstPerson || !bodyOn || mutantCamera) return S13Basis.Camera;
            return S13Basis.Body;
        }

        public static string Name(S13Basis b)
        {
            switch (b)
            {
                case S13Basis.Camera: return "camera";
                case S13Basis.Body: return "body";
                case S13Basis.Head: return "head";
                default: return "none";
            }
        }

        /// <summary>
 /// SE's content root of a model outside the game's Content (a mod's or a DLC's.mwm by full path): the path before the
 /// last "models"; null when the asset is
 /// relative (vanilla, Content-relative) or has no "models" part.
 /// </summary>
        public static string MwmContentPath(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return null;
            bool rooted;
            try { rooted = Path.IsPathRooted(asset); } catch (Exception) { return null; }
            if (!rooted) return null;
            int i = asset.ToLowerInvariant().LastIndexOf("models", StringComparison.Ordinal);
            return i > 0 ? asset.Substring(0, i) : null;
        }

        /// <summary>
 /// A texture name as SE's renderer resolves it
 /// a rooted name as it is; else, when the model has a content root of its own (a mod)
 /// that differs from the game's Content and the file exists there, that one (<paramref name="viaRoot"/> true); else
 /// Content + name. Returns null when the chosen file does not exist (the caller logs it missing).
 /// </summary>
        public static string TextureFullPath(string raw, string contentRoot, string contentPath, Func<string, bool> exists, out bool viaRoot)
        {
            viaRoot = false;
            if (string.IsNullOrEmpty(raw)) return "";
            string full;
            try
            {
                if (Path.IsPathRooted(raw)) full = raw;
                else
                {
                    full = null;
                    if (!string.IsNullOrEmpty(contentRoot) && !SamePath(contentRoot, contentPath))
                    {
                        string c = Path.Combine(contentRoot, raw);
                        if (exists(c)) { full = c; viaRoot = true; }
                    }
                    if (full == null) full = Path.Combine(contentPath ?? "", raw);
                }
            }
            catch (Exception) { return null; }
            return exists(full) ? full : null;
        }

        private static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
 /// S13 point 3: the held tool's PartAdd colour = the tool entity's own <c>Render.ColorMaskHsv</c>
 /// so the shader key is (0, -0.2, -0.1): grey, never
 /// the red of (0, 0, 0) + ColorizeHSV (0, 0.8, -0.1). A non-finite colour falls back to OldGray.
 /// </summary>
        public static void ToolColour(float h, float s, float v, bool mutantZero, out float oh, out float os, out float ov)
        {
            if (mutantZero) { oh = os = ov = 0f; return; }
            if (float.IsNaN(h) || float.IsNaN(s) || float.IsNaN(v) || float.IsInfinity(h) || float.IsInfinity(s) || float.IsInfinity(v))
            {
                oh = OldGrayH; os = OldGrayS; ov = OldGrayV;
                return;
            }
            oh = h; os = s; ov = v;
        }

        /// <summary>SE's <c>MyRenderComponentBase.OldGrayToHSV</c>.</summary>
        public const float OldGrayH = 0f, OldGrayS = -1f, OldGrayV = 0f;

        /// <summary>The angle in degrees between two directions (0 for a zero vector).</summary>
        public static double AngleDeg(double ax, double ay, double az, double bx, double by, double bz)
        {
            double la = Math.Sqrt(ax * ax + ay * ay + az * az), lb = Math.Sqrt(bx * bx + by * by + bz * bz);
            if (!(la > 1e-9) || !(lb > 1e-9)) return 0;
            double c = (ax * bx + ay * by + az * bz) / (la * lb);
            if (c > 1) c = 1;
            if (c < -1) c = -1;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>The s13.swim row cadence: one per second in the water, plus every basis change.</summary>
        public const int RowEveryTicks = 60;
    }
}
