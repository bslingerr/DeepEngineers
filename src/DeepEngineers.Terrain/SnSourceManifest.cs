using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DeepEngineers.Wire;

namespace DeepEngineers.Terrain
{
    /// <summary>
 /// The source manifest of the terrain gate's condition 3 (CONTRACT 3.1 "Source manifest"): Subnautica build, the
 /// batch files of CompiledOctreesCache (count, bytes, rows, listing hash) and index.txt. A directory listing only
 /// (no file is opened except index.txt and the Steam app manifest); never throws.
 /// </summary>
    public sealed class SnSourceManifest
    {
        public const int ExpectedBuildId = SiteV1.SnManifestBuildId;          // 20241558
        public const int ExpectedFiles = SiteV1.SnManifestFiles;              // 5416
        public const long ExpectedBytes = SiteV1.SnManifestBytes;             // 1203085204
 /// <summary>Pinned batch rows (the API table's constants "RowMin = 7; RowMax = 19"; renamed because the measured
 /// rows are the instance fields <see cref="RowMin"/>/<see cref="RowMax"/>).</summary>
        public const int ExpectedRowMin = SiteV1.SnBatchRowFirst;             // 7
        public const int ExpectedRowMax = SiteV1.SnBatchRowLast;              // 19
 /// <summary>FNV-1a 64 of the "&lt;name&gt;\t&lt;size&gt;\n" listing in ordinal name order (pinned here only).</summary>
        public const ulong ExpectedHash = 0x2cc277ce91763285UL;
        /// <summary>index.txt lines 2-5 (voxels, octrees, octree size, octrees per batch; LargeWorldStreamer.Initialize order).</summary>
        public static readonly string[] ExpectedIndexLines = { "4096 3200 4096", "128 100 128", "32", "5 5 5" };

        public const string SteamDefaultDir = @"C:\Program Files (x86)\Steam\steamapps\common\Subnautica";
        public const string EnvVar = "DEEPENG_SUBNAUTICA";
        public const string AppManifestName = "appmanifest_264710.acf";

        private static readonly Regex BatchName = new Regex(@"^compiled-batch-(\d+)-(\d+)-(\d+)\.optoctrees$", RegexOptions.CultureInvariant);
        private static readonly Regex BuildLine = new Regex("^\\s*\"buildid\"\\s*\"(\\d+)\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public string SubnauticaDir;
        public string OctreeDir;
        /// <summary>Steam build id, -1 when the app manifest is missing or unreadable.</summary>
        public int BuildId = -1;
        public int Files;
        public long Bytes;
        /// <summary>Lowest and highest batch row y with a file (-1 when there is none).</summary>
        public int RowMin = -1, RowMax = -1;
        public ulong Hash;
        public bool IndexOk;
        /// <summary>Files in the folder that are not batch files (informational; not part of <see cref="Matches(out string, out string, out string)"/>).</summary>
        public int OtherFiles;
        /// <summary>Why the folder could not be measured (null when it was).</summary>
        public string Error;

        /// <summary>The Subnautica folder: the plugin config key <c>subnauticaDir</c>, else DEEPENG_SUBNAUTICA, else the Steam default.</summary>
        public static string ResolveSubnauticaDir(string configValue)
        {
            if (!string.IsNullOrWhiteSpace(configValue)) return configValue.Trim();
            string env = null;
            try { env = Environment.GetEnvironmentVariable(EnvVar); }
            catch (System.Security.SecurityException) { }
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return SteamDefaultDir;
        }

        /// <summary>CompiledOctreesCache below a Subnautica folder.</summary>
        public static string OctreeDirOf(string subnauticaDir)
        {
            return Path.Combine(subnauticaDir, "Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "Build18", "CompiledOctreesCache");
        }

        /// <summary>Measures the folder (listing, index.txt, app manifest). Never throws; failures land in <see cref="Error"/>.</summary>
        public static SnSourceManifest Measure(string subnauticaDir)
        {
            var m = new SnSourceManifest { SubnauticaDir = subnauticaDir };
            try
            {
                if (string.IsNullOrWhiteSpace(subnauticaDir)) { m.Error = "no Subnautica folder"; return m; }
                m.OctreeDir = OctreeDirOf(subnauticaDir);
                m.BuildId = ReadBuildId(subnauticaDir);
                m.IndexOk = ReadIndexOk(Path.Combine(Path.GetDirectoryName(m.OctreeDir), "index.txt"));
                if (!Directory.Exists(m.OctreeDir)) { m.Error = "folder missing " + m.OctreeDir; return m; }
                MeasureListing(m);
            }
            catch (Exception e)
            {
                m.Error = e.GetType().Name + ": " + e.Message;
            }
            return m;
        }

        private static void MeasureListing(SnSourceManifest m)
        {
            var names = new List<string>(6000);
            var sizes = new Dictionary<string, long>(6000, StringComparer.Ordinal);
            int rowMin = int.MaxValue, rowMax = int.MinValue;
            foreach (FileInfo fi in new DirectoryInfo(m.OctreeDir).EnumerateFiles())
            {
                Match match = BatchName.Match(fi.Name);
                if (!match.Success) { m.OtherFiles++; continue; }
                int by = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (by < rowMin) rowMin = by;
                if (by > rowMax) rowMax = by;
                names.Add(fi.Name);
                sizes[fi.Name] = fi.Length;
                m.Bytes += fi.Length;
            }
            m.Files = names.Count;
            if (names.Count > 0) { m.RowMin = rowMin; m.RowMax = rowMax; }
            names.Sort(StringComparer.Ordinal);
            ulong h = SiteV1.FnvOffsetBasis;
            var sb = new StringBuilder(64);
            foreach (string n in names)
            {
                sb.Length = 0;
                sb.Append(n).Append('\t').Append(sizes[n].ToString(CultureInfo.InvariantCulture)).Append('\n');
                byte[] line = Encoding.UTF8.GetBytes(sb.ToString());
                for (int i = 0; i < line.Length; i++)
                {
                    h ^= line[i];
                    h = unchecked(h * SiteV1.FnvPrime);
                }
            }
            m.Hash = h;
        }

        /// <summary><c>"buildid"</c> of <c>&lt;Subnautica&gt;\..\..\appmanifest_264710.acf</c>; -1 when absent.</summary>
        internal static int ReadBuildId(string subnauticaDir)
        {
            try
            {
                string acf = Path.GetFullPath(Path.Combine(subnauticaDir, "..", "..", AppManifestName));
                if (!File.Exists(acf)) return -1;
                foreach (string line in File.ReadAllLines(acf))
                {
                    Match mm = BuildLine.Match(line);
                    int v;
                    if (mm.Success && int.TryParse(mm.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return v;
                }
            }
            catch (Exception) { }
            return -1;
        }

        internal static bool ReadIndexOk(string indexPath)
        {
            try
            {
                if (!File.Exists(indexPath)) return false;
                string[] lines = File.ReadAllLines(indexPath);
                if (lines.Length < 1 + ExpectedIndexLines.Length) return false;
                for (int i = 0; i < ExpectedIndexLines.Length; i++)
                    if (lines[i + 1].Trim() != ExpectedIndexLines[i]) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>True when every pinned field matches; otherwise the first differing field (CONTRACT 3.1 order).</summary>
        public bool Matches(out string field, out string found, out string expected)
        {
            return Matches(0, out field, out found, out expected);
        }

        /// <summary>
 /// As <see cref="Matches(out string, out string, out string)"/> with the expected file count shifted by
 /// <paramref name="expectedFilesDelta"/> (the SE plugin's mutant S4.M5 passes 1).
 /// </summary>
        public bool Matches(int expectedFilesDelta, out string field, out string found, out string expected)
        {
            field = found = expected = null;
            if (Error != null) return Fail("folder", Error, OctreeDir ?? "CompiledOctreesCache", out field, out found, out expected);
            if (!IndexOk) return Fail("index", "lines 2-5 differ or index.txt missing", string.Join(" | ", ExpectedIndexLines), out field, out found, out expected);
            if (BuildId != ExpectedBuildId) return Fail("build", Inv(BuildId), Inv(ExpectedBuildId), out field, out found, out expected);
            int files = ExpectedFiles + expectedFilesDelta;
            if (Files != files) return Fail("files", Inv(Files), Inv(files), out field, out found, out expected);
            if (Bytes != ExpectedBytes) return Fail("bytes", Inv(Bytes), Inv(ExpectedBytes), out field, out found, out expected);
            if (RowMin != ExpectedRowMin || RowMax != ExpectedRowMax)
                return Fail("rows", Inv(RowMin) + ".." + Inv(RowMax), Inv(ExpectedRowMin) + ".." + Inv(ExpectedRowMax), out field, out found, out expected);
            if (Hash != ExpectedHash) return Fail("manifestHash", Hex(Hash), Hex(ExpectedHash), out field, out found, out expected);
            return true;
        }

        private static bool Fail(string f, string fo, string ex, out string field, out string found, out string expected)
        {
            field = f;
            found = fo;
            expected = ex;
            return false;
        }

        /// <summary>"DE.Terrain SOURCE path=… build=… files=… bytes=… rows=7..19 manifestHash=0x… index=ok".</summary>
        public string LogLine()
        {
            return "DE.Terrain SOURCE path=" + (OctreeDir ?? SubnauticaDir ?? "?") + " build=" + Inv(BuildId) + " files=" + Inv(Files) +
                   " bytes=" + Inv(Bytes) + " rows=" + Inv(RowMin) + ".." + Inv(RowMax) + " manifestHash=" + Hex(Hash) +
                   " index=" + (IndexOk ? "ok" : "BAD") + (OtherFiles > 0 ? " otherFiles=" + Inv(OtherFiles) : "") +
                   (Error != null ? " error=" + Error : "");
        }

        public static string Hex(ulong v)
        {
            return "0x" + v.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static string Inv(long v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }
    }
}
