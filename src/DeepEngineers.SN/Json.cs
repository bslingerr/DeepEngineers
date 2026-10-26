using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Minimal JSON for the C11 control lines (CONTRACT §5.4 C11: one JSON object per message, UTF-8). Parse returns
 /// Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, bool or null and throws FormatException on
 /// malformed input. No dependency on a game JSON library. Our own code (not a copy of any library).
 /// </summary>
    internal static class Json
    {
        private const int MaxDepth = 32;

        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("null text");
            int i = 0;
            object v = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing characters at " + i);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nesting too deep");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth + 1);
            if (c == '[') return ParseArray(s, ref i, depth + 1);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
            throw new FormatException("unexpected '" + c + "' at " + i);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("expected " + word + " at " + i);
            i += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected a key at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                d[key] = ParseValue(s, ref i, depth);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end in object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i, depth));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end in array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
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

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') i++;
                else break;
            }
            double v;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new FormatException("bad number at " + start);
            return v;
        }

        // ---------------------------------------------------------------------------------------------- writing

        public static void AppendString(StringBuilder sb, string value)
        {
            if (value == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in value)
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
        }

        /// <summary>Re-emits a parsed scalar (the request id is echoed as it came: number, string or null).</summary>
        public static void AppendScalar(StringBuilder sb, object value)
        {
            if (value == null) sb.Append("null");
            else if (value is string) AppendString(sb, (string)value);
            else if (value is bool) sb.Append((bool)value ? "true" : "false");
            else if (value is double) AppendNumber(sb, (double)value);
            // a test run (no 20261024-033017, S8a.NO.immune "None hits"): ints, longs and floats went out as strings, floats in the
 // machine's culture ("89,08162"); every numeric type is a JSON number now (invariant)
            else if (value is float) AppendNumber(sb, (float)value);
            else if (value is int || value is long || value is short || value is byte || value is sbyte || value is ushort || value is uint)
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            else if (value is ulong) sb.Append(((ulong)value).ToString(CultureInfo.InvariantCulture));
            else if (value is decimal) AppendNumber(sb, (double)(decimal)value);
            else AppendString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        public static void AppendNumber(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { sb.Append("null"); return; }
            if (v == Math.Floor(v) && Math.Abs(v) < 9.007199254740992e15) sb.Append(((long)v).ToString(CultureInfo.InvariantCulture));
            else sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Builds one flat JSON object for a C11 reply.</summary>
    internal sealed class JsonObjectWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(256);
        private bool _first = true;

        public JsonObjectWriter() { _sb.Append('{'); }

        private StringBuilder Key(string name)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            Json.AppendString(_sb, name);
            _sb.Append(':');
            return _sb;
        }

        public JsonObjectWriter Str(string name, string value) { Json.AppendString(Key(name), value); return this; }
        public JsonObjectWriter Num(string name, double value) { Json.AppendNumber(Key(name), value); return this; }
        public JsonObjectWriter Num(string name, long value) { Key(name).Append(value.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonObjectWriter Num(string name, ulong value) { Key(name).Append(value.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonObjectWriter Bool(string name, bool value) { Key(name).Append(value ? "true" : "false"); return this; }
        public JsonObjectWriter Scalar(string name, object value) { Json.AppendScalar(Key(name), value); return this; }

        /// <summary>A value already in JSON (an array or an object built by the caller, e.g. with <see cref="JsonOut"/>), written as is.</summary>
        public JsonObjectWriter Raw(string name, string json) { Key(name).Append(string.IsNullOrEmpty(json) ? "null" : json); return this; }

        public override string ToString() { return _sb.ToString() + "}"; }
    }
}
