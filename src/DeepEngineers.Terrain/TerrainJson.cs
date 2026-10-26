using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Terrain
{
    /// <summary>
 /// Minimal JSON for the core's own records (A-T0 summaries). Objects parse to <see cref="Dictionary{TKey,TValue}"/>
 /// (string to object), arrays to <see cref="List{T}"/>, integers to long, other numbers to double.
 /// </summary>
    internal static class TerrainJson
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
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
            if (c == '{')
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs(s, ref i);
                    string key = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    Expect(s, ref i, ':');
                    d[key] = ParseValue(s, ref i);
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(ParseValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return l;
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (Match(s, ref i, "true")) return true;
            if (Match(s, ref i, "false")) return false;
            if (Match(s, ref i, "null")) return null;
            return ParseNumber(s, ref i);
        }

        private static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException("expected '" + c + "' at " + i);
            i++;
        }

        private static string ParseString(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
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
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
            throw new FormatException("unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            bool isFloat = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9' || c == '-' || c == '+') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E') { isFloat = true; i++; continue; }
                break;
            }
            string t = s.Substring(start, i - start);
            if (t.Length == 0) throw new FormatException("unexpected character at " + start);
            long l;
            if (!isFloat && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        public static long AsLong(object o)
        {
            if (o is long) return (long)o;
            if (o is double) return (long)(double)o;
            if (o is int) return (int)o;
            throw new FormatException("not a number: " + (o ?? "null"));
        }

        public static double AsDouble(object o)
        {
            if (o is double) return (double)o;
            if (o is long) return (long)o;
            if (o is int) return (int)o;
            throw new FormatException("not a number: " + (o ?? "null"));
        }
    }
}
