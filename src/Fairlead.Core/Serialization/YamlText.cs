using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Fairlead.Core.Serialization
{
    /// <summary>
    /// The block-style YAML subset configuration files use, read into the JSON document model: mappings, sequences
    /// (including sequences of mappings), plain, single- and double-quoted scalars, literal (|) and folded (&gt;) block
    /// scalars with their chomping and indentation indicators, comments, and one-line flow sequences and mappings of
    /// scalars. Anything else (anchors, aliases, tags, several documents, tab indentation, duplicate keys) is refused
    /// with a line number. Errors never quote the file's values.
    /// </summary>
    public sealed class YamlReader
    {
        private sealed class Line
        {
            public int Number;
            public int Indent;
            public string Text;
        }

        private readonly List<Line> _lines = new List<Line>();
        private readonly string[] _raw;
        // Problems on lines that turn out to be inside a block scalar are not problems; the rest are thrown after parsing.
        private readonly SortedDictionary<int, string> _deferred = new SortedDictionary<int, string>();
        private readonly HashSet<int> _blockLines = new HashSet<int>();
        private int _pos;

        private YamlReader(string text)
        {
            var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (raw.Length > 0 && raw[0].Length > 0 && raw[0][0] == '\uFEFF') raw[0] = raw[0].Substring(1);
            _raw = raw;
            var seenContent = false;
            for (var i = 0; i < raw.Length; i++)
            {
                var s = raw[i];
                var indent = 0;
                while (indent < s.Length && s[indent] == ' ') indent++;
                if (indent < s.Length && s[indent] == '\t')
                {
                    _deferred[i + 1] = "tabs are not allowed for indentation";
                    continue;
                }
                var content = StripComment(s.Substring(indent), i + 1).TrimEnd();
                if (content.Length == 0) continue;
                if (indent == 0 && (content == "---" || content == "..."))
                {
                    if (seenContent) throw Error(i + 1, "only one document per file is supported");
                    continue;
                }
                seenContent = true;
                _lines.Add(new Line { Number = i + 1, Indent = indent, Text = content });
            }
        }

        /// <summary>The document as tokens; an empty document is an empty object.</summary>
        public static JToken Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            var r = new YamlReader(text);
            if (r._lines.Count == 0)
            {
                foreach (var d in r._deferred) throw Error(d.Key, d.Value);
                return new JObject();
            }
            var root = r.ParseBlock(r._lines[0].Indent);
            if (r._pos < r._lines.Count) throw Error(r._lines[r._pos].Number, "unexpected indentation");
            foreach (var d in r._deferred)
                if (!r._blockLines.Contains(d.Key)) throw Error(d.Key, d.Value);
            return root;
        }

        private static JsonException Error(int line, string what)
        {
            return new JsonException("YAML line " + line + ": " + what + ".");
        }

        private static string StripComment(string s, int line)
        {
            char quote = '\0';
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (quote != '\0')
                {
                    if (quote == '"' && c == '\\') { i++; continue; }
                    if (c == quote)
                    {
                        if (quote == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { i++; continue; }
                        quote = '\0';
                    }
                    continue;
                }
                if ((c == '"' || c == '\'') && (i == 0 || " :-[{,".IndexOf(s[i - 1]) >= 0)) { quote = c; continue; }
                if (c == '#' && (i == 0 || s[i - 1] == ' ')) return s.Substring(0, i);
            }
            return s;
        }

        private static bool IsSequenceItem(string text)
        {
            return text == "-" || text.StartsWith("- ", StringComparison.Ordinal);
        }

        private JToken ParseBlock(int indent)
        {
            var line = _lines[_pos];
            if (IsSequenceItem(line.Text)) return ParseSequence(line.Indent);
            int colon;
            if (FindKeyColon(line.Text, out colon)) return ParseMapping(line.Indent);
            _pos++;
            return Scalar(line.Text, line.Number);
        }

        private JObject ParseMapping(int indent)
        {
            var o = new JObject();
            while (_pos < _lines.Count && _lines[_pos].Indent == indent && !IsSequenceItem(_lines[_pos].Text))
            {
                var line = _lines[_pos];
                int colon;
                if (!FindKeyColon(line.Text, out colon)) throw Error(line.Number, "expected 'key: value'");
                var key = Key(line.Text.Substring(0, colon).Trim(), line.Number);
                if (o.ContainsKey(key)) throw Error(line.Number, "key '" + key + "' appears twice");
                var rest = line.Text.Substring(colon + 1).Trim();
                _pos++;
                JToken value;
                if (IsBlockScalarHeader(rest)) value = BlockScalar(rest, indent, line.Number);
                else if (rest.Length > 0) value = Scalar(rest, line.Number);
                else if (_pos < _lines.Count && _lines[_pos].Indent > indent) value = ParseBlock(_lines[_pos].Indent);
                else if (_pos < _lines.Count && _lines[_pos].Indent == indent && IsSequenceItem(_lines[_pos].Text)) value = ParseSequence(indent);
                else value = JValue.CreateNull();
                o.Add(key, value);
            }
            if (_pos < _lines.Count && _lines[_pos].Indent > indent) throw Error(_lines[_pos].Number, "unexpected indentation");
            return o;
        }

        private JArray ParseSequence(int indent)
        {
            var a = new JArray();
            while (_pos < _lines.Count && _lines[_pos].Indent == indent && IsSequenceItem(_lines[_pos].Text))
            {
                var line = _lines[_pos];
                var item = line.Text.Length > 1 ? line.Text.Substring(2) : string.Empty;
                var lead = item.Length - item.TrimStart().Length;
                item = item.Trim();
                if (item.Length == 0)
                {
                    _pos++;
                    if (_pos < _lines.Count && _lines[_pos].Indent > indent) a.Add(ParseBlock(_lines[_pos].Indent));
                    else a.Add(JValue.CreateNull());
                    continue;
                }
                int colon;
                if (IsSequenceItem(item) || (FindKeyColon(item, out colon) && !item.StartsWith("[", StringComparison.Ordinal) && !item.StartsWith("{", StringComparison.Ordinal)))
                {
                    // "- key: value" starts a mapping (or "- - x" a sequence) whose first line is this one, one column in.
                    line.Indent = indent + 2 + lead;
                    line.Text = item;
                    a.Add(ParseBlock(line.Indent));
                    continue;
                }
                if (IsBlockScalarHeader(item)) { _pos++; a.Add(BlockScalar(item, indent, line.Number)); continue; }
                _pos++;
                a.Add(Scalar(item, line.Number));
            }
            if (_pos < _lines.Count && _lines[_pos].Indent > indent) throw Error(_lines[_pos].Number, "unexpected indentation");
            return a;
        }

        private static bool IsBlockScalarHeader(string text)
        {
            return text.Length > 0 && (text[0] == '|' || text[0] == '>');
        }

        /// <summary>
        /// A literal (|) or folded (&gt;) block scalar whose header is on line <paramref name="headerLine"/> under a node
        /// indented <paramref name="parentIndent"/>. Reads the raw lines that follow (comments and blank lines in them are
        /// content), applies the chomping (- strip, + keep, default clip) and indentation indicators, and moves past them.
        /// The caller has already moved past the header line.
        /// </summary>
        private JValue BlockScalar(string header, int parentIndent, int headerLine)
        {
            var folded = header[0] == '>';
            char chomp = '\0';
            var explicitIndent = 0;
            for (var i = 1; i < header.Length; i++)
            {
                var c = header[i];
                if ((c == '-' || c == '+') && chomp == '\0') chomp = c;
                else if (c >= '1' && c <= '9' && explicitIndent == 0) explicitIndent = c - '0';
                else if (c == ' ') { if (header.Substring(i).Trim().Length > 0) throw Error(headerLine, "text after a block scalar indicator"); break; }
                else throw Error(headerLine, "invalid block scalar indicator");
            }

            // Raw line indexes are 0-based; line numbers are index + 1, so the content starts at index headerLine.
            var start = headerLine;
            var blockIndent = explicitIndent > 0 ? parentIndent + explicitIndent : -1;
            var end = start;
            for (; end < _raw.Length; end++)
            {
                var s = _raw[end];
                if (s.Trim(' ').Length == 0) continue;   // blank lines belong to the scalar until a less-indented line
                var ind = 0;
                while (ind < s.Length && s[ind] == ' ') ind++;
                if (blockIndent < 0)
                {
                    if (ind <= parentIndent) break;
                    blockIndent = ind;
                }
                if (ind < blockIndent) break;
            }
            // Trailing blank lines up to the next node stay with this scalar only through chomping.
            var lastContent = end - 1;
            while (lastContent >= start && _raw[lastContent].Trim(' ').Length == 0) lastContent--;
            if (blockIndent < 0) blockIndent = parentIndent + 1;

            var lines = new List<string>();
            for (var i = start; i <= lastContent; i++)
            {
                var s = _raw[i];
                lines.Add(s.Length > blockIndent ? s.Substring(blockIndent) : (s.Trim(' ').Length == 0 ? string.Empty : s.TrimStart(' ')));
            }
            var trailingBlank = end - 1 - lastContent;
            for (var n = start + 1; n <= end; n++) _blockLines.Add(n);
            while (_pos < _lines.Count && _lines[_pos].Number <= end) _pos++;

            var sb = new StringBuilder();
            if (!folded)
            {
                for (var i = 0; i < lines.Count; i++) { if (i > 0) sb.Append('\n'); sb.Append(lines[i]); }
            }
            else
            {
                // Folding: a line break between two ordinary lines becomes a space; an empty line is a line break;
                // more-indented lines keep their breaks.
                var prev = 0;   // 0 start, 1 ordinary, 2 more-indented, 3 empty
                foreach (var l in lines)
                {
                    if (l.Length == 0) { sb.Append('\n'); prev = 3; continue; }
                    var more = l[0] == ' ' || l[0] == '\t';
                    if (prev == 1 && !more) sb.Append(' ');
                    else if (prev == 1 || prev == 2) sb.Append('\n');
                    else if (prev == 3 && more) sb.Append('\n');
                    sb.Append(l);
                    prev = more ? 2 : 1;
                }
            }
            if (lines.Count > 0 && chomp != '-') sb.Append('\n');
            if (chomp == '+') sb.Append('\n', trailingBlank);
            return new JValue(sb.ToString());
        }

        /// <summary>The ':' that ends a mapping key: followed by a space or the end of the line, outside quotes and brackets.</summary>
        private static bool FindKeyColon(string text, out int index)
        {
            index = -1;
            if (text.Length == 0 || text[0] == '[' || text[0] == '{') return false;
            char quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (quote == '"' && c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (i == 0 && (c == '"' || c == '\'')) { quote = c; continue; }
                if (c == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
                {
                    index = i;
                    return i > 0;
                }
            }
            return false;
        }

        private static string Key(string raw, int line)
        {
            if (raw.Length == 0) throw Error(line, "empty key");
            if (raw[0] == '"' || raw[0] == '\'') return Quoted(raw, line);
            if ("&*!?|>%@`".IndexOf(raw[0]) >= 0) throw Error(line, "unsupported YAML syntax in a key");
            return raw;
        }

        private static JToken Scalar(string text, int line)
        {
            if (text.Length == 0) return JValue.CreateNull();
            var c = text[0];
            if (c == '"' || c == '\'') return new JValue(Quoted(text, line));
            if (c == '[') return FlowSequence(text, line);
            if (c == '{') return FlowMapping(text, line);
            if (c == '&' || c == '*') throw Error(line, "anchors and aliases are not supported");
            if (c == '!') throw Error(line, "tags are not supported");
            if (c == '|' || c == '>') throw Error(line, "block scalars (| and >) are not supported; use a quoted string");
            if (c == '@' || c == '`') throw Error(line, "a plain value cannot start with " + c);
            switch (text)
            {
                case "true": case "True": case "TRUE": return new JValue(true);
                case "false": case "False": case "FALSE": return new JValue(false);
                case "null": case "Null": case "NULL": case "~": return JValue.CreateNull();
            }
            long l;
            if (IsNumberLike(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return new JValue(l);
            double d;
            if (IsNumberLike(text) && text.IndexOf('.') >= 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return new JValue(d);
            return new JValue(text);
        }

        private static bool IsNumberLike(string text)
        {
            foreach (var ch in text)
                if (!(char.IsDigit(ch) || ch == '-' || ch == '+' || ch == '.' || ch == 'e' || ch == 'E')) return false;
            return true;
        }

        private static string Quoted(string text, int line)
        {
            var quote = text[0];
            var sb = new StringBuilder();
            var i = 1;
            for (; i < text.Length; i++)
            {
                var c = text[i];
                if (quote == '\'')
                {
                    if (c == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                        break;
                    }
                    sb.Append(c);
                    continue;
                }
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= text.Length) throw Error(line, "unterminated escape");
                switch (text[i])
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        int code;
                        if (i + 4 >= text.Length || !int.TryParse(text.Substring(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) throw Error(line, "invalid \\u escape");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw Error(line, "unsupported escape \\" + text[i]);
                }
            }
            if (i >= text.Length) throw Error(line, "unterminated quoted string");
            if (text.Substring(i + 1).Trim().Length > 0) throw Error(line, "text after a quoted string");
            return sb.ToString();
        }

        private static List<string> FlowItems(string text, char open, char close, int line)
        {
            if (text[text.Length - 1] != close) throw Error(line, "a flow collection must close on the same line");
            var inner = text.Substring(1, text.Length - 2);
            var items = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            for (var i = 0; i < inner.Length; i++)
            {
                var c = inner[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (quote == '"' && c == '\\' && i + 1 < inner.Length) { sb.Append(inner[++i]); continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; sb.Append(c); continue; }
                if (c == '[' || c == '{' || c == ']' || c == '}') throw Error(line, "nested flow collections are not supported");
                if (c == ',') { items.Add(sb.ToString().Trim()); sb.Length = 0; continue; }
                sb.Append(c);
            }
            if (quote != '\0') throw Error(line, "unterminated quoted string");
            var last = sb.ToString().Trim();
            if (last.Length > 0 || items.Count > 0) items.Add(last);
            if (items.Count > 0 && items[items.Count - 1].Length == 0) items.RemoveAt(items.Count - 1);
            return items;
        }

        private static JArray FlowSequence(string text, int line)
        {
            var a = new JArray();
            foreach (var item in FlowItems(text, '[', ']', line))
            {
                if (item.Length == 0) throw Error(line, "empty item in a flow sequence");
                a.Add(Scalar(item, line));
            }
            return a;
        }

        private static JObject FlowMapping(string text, int line)
        {
            var o = new JObject();
            foreach (var item in FlowItems(text, '{', '}', line))
            {
                int colon;
                if (!FindKeyColon(item, out colon)) throw Error(line, "expected 'key: value' in a flow mapping");
                var key = Key(item.Substring(0, colon).Trim(), line);
                if (o.ContainsKey(key)) throw Error(line, "key '" + key + "' appears twice");
                o.Add(key, Scalar(item.Substring(colon + 1).Trim(), line));
            }
            return o;
        }
    }
}
