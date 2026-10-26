using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// Planet v2 (docs/S8A-PLANET-V2.md R8, the ore density; decision a: the planet's files are generated on the user's machine from
 /// the install and the layout): the runtime rules a layout carries beside its 12 PNGs, written by tools/planet/sculpt_de4546b.py
 /// into the planet data folder as <see cref="FileName"/> (only when the layout has them: design 3 writes none and keeps today's
 /// rules), read by the SE plugin at world up. One <c>key=value</c> per line after the magic line; unknown keys are kept and
 /// ignored. The window's and the charts' keys fold the file's hash in (<see cref="FoldInto"/>), so a store made under other rules
 /// is never served.
 /// <list type="bullet">
 /// <item><c>crater.cubeRule</c> (bool, default true): the crater tiles' 8 m cube rule (S8aCraterOres.RowAt: 22-26 pockets per
 /// hectare at 0-200 m today). Off: ore only under Subnautica's large deposits.</item>
 /// <item><c>crater.depositsPerKm2</c> (number, default 0 = no cap): at most this many of Subnautica's large deposits get an SE
 /// pocket per square kilometre cell of the crater (1 km x 1 km, SN x/z floor-divided; the cells approximate the doc's "per biome").</item>
 /// </list>
 /// </summary>
    public sealed class S8aPlanetRules
    {
        public const string FileName = "de-planet-rules.txt";
        public const string Magic = "de-planet-rules 1";
        public const double CapCellM = 1000.0;

        public bool CraterCubeRule = true;
        public double CraterDepositsPerKm2;
        /// <summary>Every key=value read (unknown keys included), in file order.</summary>
        public readonly List<KeyValuePair<string, string>> Entries = new List<KeyValuePair<string, string>>();
        /// <summary>fnv1a64 of the file's bytes (0 for the defaults: no file).</summary>
        public ulong Hash;

        /// <summary>The defaults (no file): today's rules.</summary>
        public static S8aPlanetRules Defaults() { return new S8aPlanetRules(); }

        public bool IsDefault { get { return CraterCubeRule && CraterDepositsPerKm2 <= 0; } }

        /// <summary>The text the tool writes (LF line ends, invariant numbers).</summary>
        public static string Format(bool craterCubeRule, double craterDepositsPerKm2)
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append('\n');
            sb.Append("crater.cubeRule=").Append(craterCubeRule ? "true" : "false").Append('\n');
            sb.Append("crater.depositsPerKm2=").Append(craterDepositsPerKm2.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        /// <summary>Parses the file's bytes; false (and the defaults) on a wrong magic or a bad value.</summary>
        public static bool TryParse(byte[] bytes, out S8aPlanetRules rules, out string error)
        {
            rules = Defaults();
            error = null;
            if (bytes == null) { error = "no bytes"; return false; }
            string text = Encoding.UTF8.GetString(bytes);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != Magic) { error = "magic"; return false; }
            var r = new S8aPlanetRules { Hash = LayoutHash.Fnv1a64(bytes) };
            for (int k = 1; k < lines.Length; k++)
            {
                string line = lines[k].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) { error = "line " + (k + 1) + ": no key=value"; return false; }
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                r.Entries.Add(new KeyValuePair<string, string>(key, val));
                if (key == "crater.cubeRule")
                {
                    if (val == "true") r.CraterCubeRule = true;
                    else if (val == "false") r.CraterCubeRule = false;
                    else { error = "crater.cubeRule: " + val; return false; }
                }
                else if (key == "crater.depositsPerKm2")
                {
                    double v;
                    if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || v < 0) { error = "crater.depositsPerKm2: " + val; return false; }
                    r.CraterDepositsPerKm2 = v;
                }
            }
            rules = r;
            return true;
        }

        /// <summary>The rules of a planet data folder: the file's, or the defaults when there is none (read only).</summary>
        public static S8aPlanetRules OfFolder(string planetDataDir, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(planetDataDir)) return Defaults();
            string path = Path.Combine(planetDataDir, FileName);
            if (!File.Exists(path)) return Defaults();
            S8aPlanetRules r;
            if (!TryParse(File.ReadAllBytes(path), out r, out error)) return Defaults();
            return r;
        }

        /// <summary>The store key's sculpt part with the rules folded in: unchanged without a file (design 3), else fnv1a64(utf8 "sculptHash x16|rules|hash x16").</summary>
        public ulong FoldInto(ulong sculptHash)
        {
            if (Hash == 0 || sculptHash == 0) return sculptHash;
            return S8aStore.Fnv1a64Utf8(S8aStore.Hex16(sculptHash) + "|rules|" + S8aStore.Hex16(Hash));
        }

        public override string ToString()
        {
            return "cubeRule " + (CraterCubeRule ? "on" : "off") + ", deposits per km2 " + (CraterDepositsPerKm2 > 0 ? CraterDepositsPerKm2.ToString("0.##", CultureInfo.InvariantCulture) : "uncapped") +
                   (Hash != 0 ? " (" + FileName + " " + S8aStore.Hex16(Hash) + ")" : " (defaults)");
        }
    }
}
