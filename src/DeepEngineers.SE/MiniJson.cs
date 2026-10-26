using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.SE
{
    /// <summary>JSON text helpers for our own log lines and C11 replies (no NuGet dependency, ROADMAP §5.2).</summary>
    internal static class JsonText
    {
        public static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        public static void AppendNumber(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { sb.Append("null"); return; }
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void AppendValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { AppendString(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double) { AppendNumber(sb, (double)v); return; }
            if (v is float) { AppendNumber(sb, (float)v); return; }
            if (v is int || v is long || v is uint || v is ulong || v is short || v is ushort || v is byte)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendString(sb, kv.Key);
                    sb.Append(':');
                    AppendValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var list = v as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            AppendString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        public static string Serialize(object v)
        {
            var sb = new StringBuilder(128);
            AppendValue(sb, v);
            return sb.ToString();
        }
    }

    /// <summary>
 /// A small recursive-descent JSON reader for C11 requests (CONTRACT §5.4: `{"id": n, "cmd": "&lt;name&gt;",
 /// "args": {…}}`). Objects become Dictionary&lt;string, object&gt; (ordinal keys), arrays List&lt;object&gt;, numbers double,
 /// plus string, bool and null. Throws FormatException on malformed input; the caller replies `ok: false`.
 /// </summary>
    internal sealed class MiniJson
    {
        private const int MaxDepth = 32;
        private readonly string _s;
        private int _i;

        private MiniJson(string s) { _s = s; }

        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("null text");
            var p = new MiniJson(text);
            p.SkipWs();
            object v = p.ReadValue(0);
            p.SkipWs();
            if (p._i != p._s.Length) throw new FormatException("trailing characters at " + p._i);
            return v;
        }

        private void SkipWs()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                else break;
            }
        }

        private char Peek()
        {
            if (_i >= _s.Length) throw new FormatException("unexpected end");
            return _s[_i];
        }

        private object ReadValue(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("too deep");
            char c = Peek();
            switch (c)
            {
                case '{': return ReadObject(depth);
                case '[': return ReadArray(depth);
                case '"': return ReadString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                    throw new FormatException("unexpected '" + c + "' at " + _i);
            }
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw new FormatException("expected " + word + " at " + _i);
            _i += word.Length;
        }

        private Dictionary<string, object> ReadObject(int depth)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            _i++; // {
            SkipWs();
            if (Peek() == '}') { _i++; return d; }
            while (true)
            {
                SkipWs();
                if (Peek() != '"') throw new FormatException("expected a key at " + _i);
                string key = ReadString();
                SkipWs();
                if (Peek() != ':') throw new FormatException("expected ':' at " + _i);
                _i++;
                SkipWs();
                d[key] = ReadValue(depth + 1);
                SkipWs();
                char c = Peek();
                _i++;
                if (c == ',') continue;
                if (c == '}') return d;
                throw new FormatException("expected ',' or '}' at " + (_i - 1));
            }
        }

        private List<object> ReadArray(int depth)
        {
            var list = new List<object>();
            _i++; // [
            SkipWs();
            if (Peek() == ']') { _i++; return list; }
            while (true)
            {
                SkipWs();
                list.Add(ReadValue(depth + 1));
                SkipWs();
                char c = Peek();
                _i++;
                if (c == ',') continue;
                if (c == ']') return list;
                throw new FormatException("expected ',' or ']' at " + (_i - 1));
            }
        }

        private string ReadString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length) throw new FormatException("unterminated string");
                char c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_i >= _s.Length) throw new FormatException("unterminated escape");
                char e = _s[_i++];
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
                        if (_i + 4 > _s.Length) throw new FormatException("short \\u escape");
                        sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _i += 4;
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
        }

        private double ReadNumber()
        {
            int start = _i;
            if (_s[_i] == '-') _i++;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                else break;
            }
            double v;
            if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new FormatException("bad number at " + start);
            return v;
        }

        // ------------------------------------------------------------------------------------------- accessors

        public static string GetString(IDictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double? GetNumber(IDictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
            if (v is double) return (double)v;
            double d;
            if (v is string && double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        public static bool? GetBool(IDictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return null;
            if (v is bool) return (bool)v;
            string s = v as string;
            if (s == "on" || s == "true" || s == "1") return true;
            if (s == "off" || s == "false" || s == "0") return false;
            if (v is double) return (double)v != 0;
            return null;
        }

        public static IDictionary<string, object> GetObject(IDictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v)) return null;
            return v as IDictionary<string, object>;
        }
    }
}
