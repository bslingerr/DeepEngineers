using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>
 /// Minimal strict JSON reader for the WM-1 files (one object per line). Objects become
 /// Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double, plus string, bool and null.
 /// Numbers are parsed with the invariant culture (round-trip, Python json output).
 /// </summary>
    internal static class MiniJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing characters at " + i);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (c == 'f' && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (c == 'n' && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string k = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (s[i] != ':') throw new FormatException("':' expected at " + i);
                i++;
                d[k] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("',' or '}' expected at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var a = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("',' or ']' expected at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("string expected at " + i);
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape at " + i);
                }
            }
        }

        private static double ParseNumber(string s, ref int i)
        {
            // Python's json writes NaN / Infinity / -Infinity for non-finite floats
            foreach (string word in new[] { "NaN", "Infinity", "-Infinity" })
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) == 0)
                {
                    i += word.Length;
                    return word == "NaN" ? double.NaN : word == "Infinity" ? double.PositiveInfinity : double.NegativeInfinity;
                }
            }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("value expected at " + i);
            // correctly rounded (the.NET Framework 4.8 double.Parse is not, for some 17-digit inputs)
            return PyFloat.Parse(s.Substring(start, i - start));
        }
    }

    /// <summary>Typed accessors over a parsed JSON object.</summary>
    internal static class J
    {
        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
        {
            return (Dictionary<string, object>)Get(o, key);
        }

        public static List<object> Arr(Dictionary<string, object> o, string key)
        {
            return (List<object>)Get(o, key);
        }

        public static double D(Dictionary<string, object> o, string key)
        {
            return Convert.ToDouble(Get(o, key), CultureInfo.InvariantCulture);
        }

        public static int I(Dictionary<string, object> o, string key)
        {
            double d = D(o, key);
            int n = (int)d;
            if (n != d) throw new FormatException(key + " is not an integer");
            return n;
        }

        public static string S(Dictionary<string, object> o, string key)
        {
            return (string)Get(o, key);
        }

        public static bool B(Dictionary<string, object> o, string key)
        {
            return (bool)Get(o, key);
        }

        public static bool Has(Dictionary<string, object> o, string key)
        {
            return o.ContainsKey(key);
        }

        public static Vec3 V3(object arr)
        {
            var a = (List<object>)arr;
            if (a.Count != 3) throw new FormatException("3 numbers expected");
            return new Vec3((double)a[0], (double)a[1], (double)a[2]);
        }

        public static Vec3 V3(Dictionary<string, object> o, string key) { return V3(Get(o, key)); }

        public static Quat Q4(Dictionary<string, object> o, string key)
        {
            var a = (List<object>)Get(o, key);
            if (a.Count != 4) throw new FormatException("4 numbers expected for " + key);
            return new Quat((double)a[0], (double)a[1], (double)a[2], (double)a[3]);
        }

        public static SeMatrix Matrix(Dictionary<string, object> o, string key)
        {
            var m = Obj(o, key);
            return new SeMatrix(V3(m, "right"), V3(m, "up"), V3(m, "backward"), V3(m, "translation"));
        }

        private static object Get(Dictionary<string, object> o, string key)
        {
            object v;
            if (!o.TryGetValue(key, out v)) throw new KeyNotFoundException("missing field '" + key + "'");
            return v;
        }
    }
}
