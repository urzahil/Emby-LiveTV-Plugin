using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Emby.Xtream.Xtream.Json
{
    /// <summary>Thrown when input is not valid JSON.</summary>
    public sealed class JsonLiteException : Exception
    {
        public JsonLiteException(string message) : base(message) { }
    }

    /// <summary>
    /// A small, dependency-free JSON parser used because netstandard2.0 has no System.Text.Json.
    /// Produces: Dictionary&lt;string, object&gt; (objects), List&lt;object&gt; (arrays), string, long, double, bool, null.
    /// Also provides tolerant accessors for the "numbers as strings / booleans as 0/1" shapes Xtream panels emit.
    /// </summary>
    public static class JsonLite
    {
        private const int MaxDepth = 64;

        public static object Parse(string json)
        {
            if (json == null) throw new JsonLiteException("Input is null.");
            var p = new Parser(json);
            return p.ParseDocument();
        }

        public static object Parse(Stream stream)
        {
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), true, 8192, true))
            {
                return Parse(reader.ReadToEnd());
            }
        }

        public static bool TryParse(string json, out object value)
        {
            try { value = Parse(json); return true; }
            catch (JsonLiteException) { value = null; return false; }
        }

        // ---- navigation helpers -------------------------------------------------

        public static Dictionary<string, object> AsObject(object o) => o as Dictionary<string, object>;

        public static List<object> AsArray(object o) => o as List<object>;

        /// <summary>
        /// Returns the elements of an array. A single object is treated as a one-element list and
        /// an object whose values are all objects (keyed collection) yields its values. Anything else yields an empty list.
        /// </summary>
        public static List<object> AsArrayLoose(object o)
        {
            if (o is List<object> list) return list;
            if (o is Dictionary<string, object> dict)
            {
                bool allObjects = dict.Count > 0;
                foreach (var v in dict.Values)
                {
                    if (!(v is Dictionary<string, object>)) { allObjects = false; break; }
                }
                if (allObjects) return new List<object>(dict.Values);
                if (dict.Count > 0) return new List<object> { dict };
            }
            return new List<object>();
        }

        public static object Get(object obj, string key)
        {
            var d = obj as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static string GetString(object obj, string key, string defaultValue = null)
        {
            return ToStringValue(Get(obj, key), defaultValue);
        }

        public static int GetInt(object obj, string key, int defaultValue = 0)
        {
            long l = GetLong(obj, key, defaultValue);
            if (l > int.MaxValue) return int.MaxValue;
            if (l < int.MinValue) return int.MinValue;
            return (int)l;
        }

        public static long GetLong(object obj, string key, long defaultValue = 0)
        {
            return ToLong(Get(obj, key), defaultValue);
        }

        public static bool GetBool(object obj, string key, bool defaultValue = false)
        {
            return ToBool(Get(obj, key), defaultValue);
        }

        public static string ToStringValue(object v, string defaultValue = null)
        {
            if (v == null) return defaultValue;
            if (v is string s) return s;
            if (v is bool b) return b ? "true" : "false";
            if (v is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            return defaultValue;
        }

        public static long ToLong(object v, long defaultValue = 0)
        {
            if (v == null) return defaultValue;
            if (v is long l) return l;
            if (v is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) return defaultValue;
                return (long)d;
            }
            if (v is bool b) return b ? 1 : 0;
            if (v is string s)
            {
                s = s.Trim();
                long parsed;
                if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out parsed)) return parsed;
                double pd;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out pd) && !double.IsNaN(pd) && !double.IsInfinity(pd)) return (long)pd;
            }
            return defaultValue;
        }

        public static bool ToBool(object v, bool defaultValue = false)
        {
            if (v == null) return defaultValue;
            if (v is bool b) return b;
            if (v is long l) return l != 0;
            if (v is double d) return d != 0;
            if (v is string s)
            {
                s = s.Trim();
                if (s.Length == 0) return defaultValue;
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "no", StringComparison.OrdinalIgnoreCase)) return false;
                double pd;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out pd)) return pd != 0;
            }
            return defaultValue;
        }

        // ---- serialisation (tiny; only used for DTOs returned by our own service/tests) ----

        public static string Escape(string s)
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

        // ---- parser -------------------------------------------------------------

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; }

            public object ParseDocument()
            {
                if (_s.Length > 0 && _s[0] == '﻿') _i = 1;
                SkipWs();
                if (_i >= _s.Length) throw Error("Empty document");
                var v = ParseValue(0);
                SkipWs();
                if (_i != _s.Length) throw Error("Unexpected trailing content");
                return v;
            }

            private JsonLiteException Error(string msg) => new JsonLiteException(msg + " at position " + _i.ToString(CultureInfo.InvariantCulture) + ".");

            private void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                    else break;
                }
            }

            private object ParseValue(int depth)
            {
                if (depth > MaxDepth) throw Error("Nesting too deep");
                SkipWs();
                if (_i >= _s.Length) throw Error("Unexpected end of input");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject(depth);
                    case '[': return ParseArray(depth);
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw Error("Unexpected character '" + c + "'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error("Invalid literal");
                _i += word.Length;
            }

            private object ParseObject(int depth)
            {
                _i++; // {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWs();
                if (_i < _s.Length && _s[_i] == '}') { _i++; return result; }
                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != '"') throw Error("Expected property name");
                    string key = ParseString();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ':') throw Error("Expected ':'");
                    _i++;
                    result[key] = ParseValue(depth + 1);
                    SkipWs();
                    if (_i >= _s.Length) throw Error("Unterminated object");
                    if (_s[_i] == ',')
                    {
                        _i++;
                        SkipWs();
                        // tolerate trailing comma
                        if (_i < _s.Length && _s[_i] == '}') { _i++; return result; }
                        continue;
                    }
                    if (_s[_i] == '}') { _i++; return result; }
                    throw Error("Expected ',' or '}'");
                }
            }

            private object ParseArray(int depth)
            {
                _i++; // [
                var result = new List<object>();
                SkipWs();
                if (_i < _s.Length && _s[_i] == ']') { _i++; return result; }
                while (true)
                {
                    result.Add(ParseValue(depth + 1));
                    SkipWs();
                    if (_i >= _s.Length) throw Error("Unterminated array");
                    if (_s[_i] == ',')
                    {
                        _i++;
                        SkipWs();
                        if (_i < _s.Length && _s[_i] == ']') { _i++; return result; }
                        continue;
                    }
                    if (_s[_i] == ']') { _i++; return result; }
                    throw Error("Expected ',' or ']'");
                }
            }

            private string ParseString()
            {
                _i++; // opening quote
                StringBuilder sb = null;
                int start = _i;
                while (true)
                {
                    if (_i >= _s.Length) throw Error("Unterminated string");
                    char c = _s[_i];
                    if (c == '"')
                    {
                        string result = sb == null ? _s.Substring(start, _i - start) : sb.Append(_s, start, _i - start).ToString();
                        _i++;
                        return result;
                    }
                    if (c == '\\')
                    {
                        if (sb == null) sb = new StringBuilder();
                        sb.Append(_s, start, _i - start);
                        _i++;
                        if (_i >= _s.Length) throw Error("Unterminated escape");
                        char e = _s[_i];
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
                                if (_i + 4 >= _s.Length) throw Error("Bad unicode escape");
                                int code;
                                if (!int.TryParse(_s.Substring(_i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                    throw Error("Bad unicode escape");
                                sb.Append((char)code);
                                _i += 4;
                                break;
                            default: throw Error("Bad escape '\\" + e + "'");
                        }
                        _i++;
                        start = _i;
                        continue;
                    }
                    // Be tolerant of raw control characters inside strings (some panels emit raw newlines).
                    _i++;
                }
            }

            private object ParseNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                bool isFloat = false;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    isFloat = true; _i++;
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    isFloat = true; _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                string token = _s.Substring(start, _i - start);
                if (token == "-" || token.Length == 0) throw Error("Invalid number");
                if (!isFloat)
                {
                    long l;
                    if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                }
                double d;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw Error("Invalid number");
            }
        }
    }
}
