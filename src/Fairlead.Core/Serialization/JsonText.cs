using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Fairlead.Core.Serialization
{
    /// <summary>
    /// JSON text to tokens. As lenient as Newtonsoft's reader was for model output: // and /* */ comments,
    /// single-quoted strings, unquoted property names, trailing commas, NaN and Infinity. A duplicate name keeps
    /// the last value. Integers become long (decimal or double beyond that); other numbers become double.
    /// </summary>
    public sealed class JsonReader
    {
        private readonly string _text;
        private int _pos;

        private JsonReader(string text)
        {
            _text = text;
        }

        public static JToken Parse(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            var r = new JsonReader(json);
            r.SkipTrivia();
            if (r._pos >= r._text.Length) throw r.Error("Unexpected end of content while reading JSON");
            var token = r.ReadValue(0);
            r.SkipTrivia();
            if (r._pos < r._text.Length) throw r.Error("Additional text encountered after finished reading JSON content: " + r._text[r._pos]);
            return token;
        }

        private JsonException Error(string what)
        {
            var line = 1;
            var col = 0;
            for (var i = 0; i < _pos && i < _text.Length; i++)
            {
                if (_text[i] == '\n') { line++; col = 0; }
                else col++;
            }
            return new JsonException(what + ". Line " + line + ", position " + (col + 1) + ".");
        }

        private void SkipTrivia()
        {
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF' || char.IsWhiteSpace(c)) { _pos++; continue; }
                if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                {
                    while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
                    continue;
                }
                if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
                {
                    var end = _text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("Unexpected end while parsing comment");
                    _pos = end + 2;
                    continue;
                }
                break;
            }
        }

        private JToken ReadValue(int depth)
        {
            if (depth > 512) throw Error("The JSON is nested too deeply");
            SkipTrivia();
            if (_pos >= _text.Length) throw Error("Unexpected end of content while reading JSON");
            var c = _text[_pos];
            switch (c)
            {
                case '{': return ReadObject(depth);
                case '[': return ReadArray(depth);
                case '"':
                case '\'':
                    return new JValue(ReadString());
            }
            if (c == '-' || (c >= '0' && c <= '9') || c == '.') return ReadNumber();
            if (Word("true")) return new JValue(true);
            if (Word("false")) return new JValue(false);
            if (Word("null")) return JValue.CreateNull();
            if (Word("NaN")) return new JValue(double.NaN);
            if (Word("Infinity")) return new JValue(double.PositiveInfinity);
            throw Error("Unexpected character encountered while parsing value: " + c);
        }

        private bool Word(string word)
        {
            if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) return false;
            var after = _pos + word.Length;
            if (after < _text.Length && (char.IsLetterOrDigit(_text[after]) || _text[after] == '_')) return false;
            _pos = after;
            return true;
        }

        private JObject ReadObject(int depth)
        {
            _pos++; // {
            var o = new JObject();
            while (true)
            {
                SkipTrivia();
                if (_pos >= _text.Length) throw Error("Unexpected end when reading an object");
                if (_text[_pos] == '}') { _pos++; return o; }
                string name;
                var c = _text[_pos];
                if (c == '"' || c == '\'') name = ReadString();
                else name = ReadBareName();
                SkipTrivia();
                if (_pos >= _text.Length || _text[_pos] != ':')
                    throw Error("Invalid character after parsing property name. Expected ':' but got: " + (_pos < _text.Length ? _text[_pos].ToString() : "end of text"));
                _pos++;
                o[name] = ReadValue(depth + 1);
                SkipTrivia();
                if (_pos >= _text.Length) throw Error("Unexpected end when reading an object");
                if (_text[_pos] == ',') { _pos++; continue; }
                if (_text[_pos] == '}') { _pos++; return o; }
                throw Error("After parsing a value an unexpected character was encountered: " + _text[_pos]);
            }
        }

        private string ReadBareName()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_' || _text[_pos] == '$')) _pos++;
            if (_pos == start) throw Error("Invalid property identifier character: " + _text[_pos]);
            return _text.Substring(start, _pos - start);
        }

        private JArray ReadArray(int depth)
        {
            _pos++; // [
            var a = new JArray();
            while (true)
            {
                SkipTrivia();
                if (_pos >= _text.Length) throw Error("Unexpected end when reading an array");
                if (_text[_pos] == ']') { _pos++; return a; }
                a.Add(ReadValue(depth + 1));
                SkipTrivia();
                if (_pos >= _text.Length) throw Error("Unexpected end when reading an array");
                if (_text[_pos] == ',') { _pos++; continue; }
                if (_text[_pos] == ']') { _pos++; return a; }
                throw Error("After parsing a value an unexpected character was encountered: " + _text[_pos]);
            }
        }

        private string ReadString()
        {
            var quote = _text[_pos];
            _pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length) throw Error("Unterminated string. Expected delimiter: " + quote);
                var c = _text[_pos++];
                if (c == quote) return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_pos >= _text.Length) throw Error("Unterminated string. Expected delimiter: " + quote);
                var e = _text[_pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        int code;
                        if (_pos + 4 > _text.Length || !int.TryParse(_text.Substring(_pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                            throw Error("Invalid Unicode escape sequence");
                        sb.Append((char)code);
                        _pos += 4;
                        break;
                    default:
                        throw Error("Bad JSON escape sequence: \\" + e);
                }
            }
        }

        private JToken ReadNumber()
        {
            var start = _pos;
            if (_text[_pos] == '-')
            {
                _pos++;
                if (Word("Infinity")) return new JValue(double.NegativeInfinity);
            }
            var isFloat = false;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c >= '0' && c <= '9') { _pos++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; _pos++; continue; }
                break;
            }
            var text = _text.Substring(start, _pos - start);
            if (_pos < _text.Length && (char.IsLetter(_text[_pos]) || _text[_pos] == '_')) throw Error("Unexpected character encountered while parsing number: " + _text[_pos]);
            if (!isFloat)
            {
                long l;
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return new JValue(l);
                decimal m;
                if (decimal.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out m)) return JValue.Integer(m);
                double big;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out big)) return JValue.Integer(big);
                throw Error("Input string '" + text + "' is not a valid integer");
            }
            double d;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Error("Input string '" + text + "' is not a valid number");
            return new JValue(d);
        }
    }

    /// <summary>Tokens to JSON text, formatted exactly as Newtonsoft.Json wrote them.</summary>
    public static class JsonWriter
    {
        public static string Write(JToken token, Formatting formatting)
        {
            var sb = new StringBuilder();
            WriteToken(sb, token, formatting == Formatting.Indented, 0);
            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append(Environment.NewLine);
            sb.Append(' ', depth * 2);
        }

        private static void WriteToken(StringBuilder sb, JToken token, bool indented, int depth)
        {
            var o = token as JObject;
            if (o != null)
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                var first = true;
                foreach (var p in o.Properties())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (indented) NewLine(sb, depth + 1);
                    WriteString(sb, p.Name);
                    sb.Append(indented ? ": " : ":");
                    WriteToken(sb, p.Value, indented, depth + 1);
                }
                if (indented) NewLine(sb, depth);
                sb.Append('}');
                return;
            }
            var a = token as JArray;
            if (a != null)
            {
                if (a.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (indented) NewLine(sb, depth + 1);
                    WriteToken(sb, a[i], indented, depth + 1);
                }
                if (indented) NewLine(sb, depth);
                sb.Append(']');
                return;
            }
            var p2 = token as JProperty;
            if (p2 != null)
            {
                WriteString(sb, p2.Name);
                sb.Append(indented ? ": " : ":");
                WriteToken(sb, p2.Value, indented, depth);
                return;
            }
            var v = token as JValue;
            if (v != null && v.Type == JTokenType.Integer && (v.Value is decimal || v.Value is double))
            {
                // An integer wider than 64 bits (kept as decimal, or double beyond that): digits only, as Newtonsoft wrote its BigInteger.
                sb.Append(v.Value is decimal ? ((decimal)v.Value).ToString("0", CultureInfo.InvariantCulture) : ((double)v.Value).ToString("0", CultureInfo.InvariantCulture));
                return;
            }
            WriteValue(sb, v == null ? null : v.Value);
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            var s = value as string;
            if (s != null) { WriteString(sb, s); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is double) { WriteDouble(sb, (double)value); return; }
            if (value is float) { WriteFloat(sb, (float)value); return; }
            if (value is decimal) { sb.Append(EnsureDecimalPlace(((decimal)value).ToString(CultureInfo.InvariantCulture))); return; }
            if (value is long || value is ulong) { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { WriteString(sb, d.ToString("R", CultureInfo.InvariantCulture)); return; }
            sb.Append(EnsureDecimalPlace(d.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static void WriteFloat(StringBuilder sb, float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) { WriteString(sb, f.ToString("R", CultureInfo.InvariantCulture)); return; }
            sb.Append(EnsureDecimalPlace(f.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static string EnsureDecimalPlace(string text)
        {
            return text.IndexOf('.') >= 0 || text.IndexOf('E') >= 0 || text.IndexOf('e') >= 0 ? text : text + ".0";
        }

        private const string Hex = "0123456789abcdef";

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); continue;
                    case '\\': sb.Append("\\\\"); continue;
                    case '\n': sb.Append("\\n"); continue;
                    case '\r': sb.Append("\\r"); continue;
                    case '\t': sb.Append("\\t"); continue;
                    case '\f': sb.Append("\\f"); continue;
                    case '\b': sb.Append("\\b"); continue;
                }
                if (c < ' ' || c == '\u0085' || c == '\u2028' || c == '\u2029')
                {
                    sb.Append("\\u").Append(Hex[(c >> 12) & 0xF]).Append(Hex[(c >> 8) & 0xF]).Append(Hex[(c >> 4) & 0xF]).Append(Hex[c & 0xF]);
                    continue;
                }
                sb.Append(c);
            }
            sb.Append('"');
        }

        /// <summary>ISO 8601 as Newtonsoft wrote dates: seconds, then up to 7 fraction digits with trailing zeros removed, then the zone.</summary>
        public static string FormatDate(DateTime value)
        {
            var sb = new StringBuilder(value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            AppendFraction(sb, value.Ticks);
            if (value.Kind == DateTimeKind.Utc) sb.Append('Z');
            else if (value.Kind == DateTimeKind.Local) AppendOffset(sb, TimeZoneInfo.Local.GetUtcOffset(value));
            return sb.ToString();
        }

        public static string FormatDate(DateTimeOffset value)
        {
            var sb = new StringBuilder(value.DateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            AppendFraction(sb, value.Ticks);
            AppendOffset(sb, value.Offset);
            return sb.ToString();
        }

        private static void AppendFraction(StringBuilder sb, long ticks)
        {
            var fraction = (int)(ticks % 10000000L);
            if (fraction == 0) return;
            sb.Append('.').Append(fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0'));
        }

        private static void AppendOffset(StringBuilder sb, TimeSpan offset)
        {
            sb.Append(offset < TimeSpan.Zero ? '-' : '+');
            var abs = offset.Duration();
            sb.Append(abs.Hours.ToString("00", CultureInfo.InvariantCulture)).Append(':').Append(abs.Minutes.ToString("00", CultureInfo.InvariantCulture));
        }
    }
}
