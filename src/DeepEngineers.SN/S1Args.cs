using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Argument helpers of the S1 C11 commands (`de.status`, `de.warpcam`, `de.capture`, `de.testSave`, `de.trace`,
 /// `de.underwater`; CONTRACT §5.4 C11). Arguments come as an object (`{"x": 1,...}`), a list (`[1, 2, 3]`) or, from
 /// the SE `/de` chat relay, `{"argv": [...]}` (S0 D28). Unity-free.
 /// </summary>
    internal static class S1Args
    {
        public static bool TryDouble(object v, out double d)
        {
            d = 0;
            if (v is double) { d = (double)v; return !double.IsNaN(d) && !double.IsInfinity(d); }
            var s = v as string;
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
        }

        /// <summary>The value named <paramref name="key"/> of an object, or the element <paramref name="index"/> of a list (−1: none).</summary>
        public static object Get(object args, string key, int index)
        {
            var dict = args as Dictionary<string, object>;
            if (dict != null)
            {
                object v;
                return dict.TryGetValue(key, out v) ? v : null;
            }
            var list = args as List<object>;
            if (list != null) return index >= 0 && index < list.Count ? list[index] : null;
            return null;
        }

        /// <summary>{"argv": [...]} (SE chat relay) becomes the list itself.</summary>
        public static object Unwrap(object args)
        {
            var dict = args as Dictionary<string, object>;
            object v;
            if (dict != null && dict.Count == 1 && dict.TryGetValue("argv", out v) && v is List<object>) return v;
            return args;
        }

        public static bool TryBool(object v, out bool b)
        {
            b = false;
            if (v is bool) { b = (bool)v; return true; }
            var s = v as string;
            if (s == null) return false;
            if (S1Switches.IsOn(s)) { b = true; return true; }
            if (S1Switches.IsOff(s)) { b = false; return true; }
            return false;
        }
    }

    /// <summary>
 /// Capture paths (CONTRACT §5.4 C10: relative to <c>%LOCALAPPDATA%\DeepEngineers\captures\</c>; ROADMAP §3 rule 8:
 /// derived data stays under %LOCALAPPDATA%\DeepEngineers). Unity-free.
 /// </summary>
    internal static class CapturePaths
    {
        public static string Dir()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(root, "DeepEngineers"), "captures");
        }

        /// <summary>
 /// A relative path under the captures folder: no root, no drive, no "." or ".." component, no empty component,
 /// no invalid file-name character, ".png" appended when missing, at most <paramref name="maxBytes"/> UTF-8 bytes.
 /// </summary>
        public static bool TryNormalise(string rel, int maxBytes, out string normalised, out string error)
        {
            normalised = null;
            error = null;
            if (string.IsNullOrEmpty(rel)) { error = "empty path"; return false; }
            rel = rel.Replace('/', '\\').Trim();
            if (rel.Length == 0) { error = "empty path"; return false; }
            if (rel.IndexOf(':') >= 0 || rel.StartsWith("\\", StringComparison.Ordinal)) { error = "path must be relative to the captures folder"; return false; }
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (string part in rel.Split('\\'))
            {
                if (part == ".." || part == ".") { error = "'.' and '..' are not allowed"; return false; }
                if (part.Length == 0) { error = "empty path component"; return false; }
                if (part.IndexOfAny(invalid) >= 0) { error = "invalid character in '" + part + "'"; return false; }
            }
            if (!rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) rel += ".png";
            if (Encoding.UTF8.GetByteCount(rel) > maxBytes) { error = "path longer than " + maxBytes + " bytes"; return false; }
            normalised = rel;
            return true;
        }
    }
}
