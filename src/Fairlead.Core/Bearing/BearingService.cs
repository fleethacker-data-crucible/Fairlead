using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Bearing
{
    /// <summary>One source an answer relied on, as the response reports it.</summary>
    public sealed class BearingCitation
    {
        public int Number { get; set; }
        public string Source { get; set; }
        public string Location { get; set; }
        public string Heading { get; set; }
        /// <summary>Words of the document: the quote the model gave when it is verbatim in the passage, else the passage sentence closest to the claim.</summary>
        public string Quote { get; set; }
        /// <summary>True when the model's own quotation was found word for word in the passage.</summary>
        public bool Verified { get; set; }
        public string Link { get; set; }

        public BearingCitation()
        {
            Source = string.Empty;
            Location = string.Empty;
            Heading = string.Empty;
            Quote = string.Empty;
            Link = string.Empty;
        }
    }

    /// <summary>
    /// Cited answers (Bearing). Off unless <c>FAIRLEAD_BEARING_DIR</c> names a folder, and then only for a request that
    /// opts in with <c>X-Fairlead-Bearing: cite</c>. A request that opts in has its last question searched against the
    /// folder, the best passages added as numbered sources, and the answer checked and annotated: each [n] it wrote is
    /// returned with the file, the lines, the quote and a link back. Retrieval is local (no embedding model, no service).
    /// </summary>
    public sealed class BearingService
    {
        public const string OptInHeader = "X-Fairlead-Bearing";
        public const string TopHeader = "X-Fairlead-Bearing-Top";
        public const int DefaultTop = 4;
        public const int MaxTop = 8;
        private readonly BearingIndex _index;

        public BearingService(string folder)
        {
            _index = string.IsNullOrWhiteSpace(folder) ? null : new BearingIndex(folder);
        }

        public bool Enabled { get { return _index != null && Directory.Exists(_index.Folder); } }
        public BearingIndex Index { get { return _index; } }

        /// <summary>True for the values that opt a request in.</summary>
        public static bool Requested(string headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return false;
            var v = headerValue.Trim().ToLowerInvariant();
            return v == "cite" || v == "on" || v == "true" || v == "1" || v == "yes";
        }

        public static int TopFrom(string headerValue)
        {
            int n;
            if (!string.IsNullOrWhiteSpace(headerValue) && int.TryParse(headerValue.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return Math.Max(1, Math.Min(MaxTop, n));
            return DefaultTop;
        }

        public object Status()
        {
            if (_index != null) _index.Refresh(false);
            return new
            {
                enabled = Enabled,
                folder = _index == null ? null : _index.Folder,
                files = _index == null ? 0 : _index.Files,
                passages = _index == null ? 0 : _index.PassageCount,
                skipped = _index == null ? 0 : _index.Skipped,
                indexedAt = _index == null ? null : _index.IndexedAt,
                optIn = OptInHeader + ": cite",
            };
        }

        /// <summary>Retrieval only, for checking what a question would find.</summary>
        public List<object> Search(string query, int top)
        {
            var list = new List<object>();
            if (!Enabled) return list;
            foreach (var h in _index.Search(query, Math.Min(MaxTop * 2, top)))
                list.Add(new { source = h.Passage.File, location = h.Passage.Location, heading = h.Passage.Heading, score = Math.Round(h.Score, 3), text = h.Passage.Text, link = h.Passage.Link });
            return list;
        }

        /// <summary>
        /// Searches for the last user question and adds the passages found to the request as numbered sources. The body is
        /// changed in place. Returns null with an error when the request has no question to search with.
        /// </summary>
        public BearingRun Prepare(JObject body, Dialect dialect, int top, out string error)
        {
            error = null;
            var question = LastUserText(body);
            if (question.Length == 0) { error = "Bearing needs a user message to search the documents with."; return null; }
            var run = new BearingRun { Question = question };
            run.Hits = Enabled ? _index.Search(question, top) : new List<BearingHit>();
            if (run.Hits.Count == 0) return run;

            var sb = new StringBuilder();
            sb.Append("Answer the user's question using only the numbered sources below, which come from the user's own documents. ");
            sb.Append("Quote the exact words you rely on in double quotes and put the source number in square brackets right after each claim, like [1]. ");
            sb.Append("If the sources do not answer the question, say that plainly and do not guess.\n\nSources:\n");
            for (var i = 0; i < run.Hits.Count; i++)
            {
                var p = run.Hits[i].Passage;
                sb.Append('[').Append(i + 1).Append("] ").Append(p.File).Append(" (").Append(p.Location).Append(")\n").Append(p.Text).Append("\n\n");
            }
            var prompt = sb.ToString().TrimEnd();
            if (dialect == Dialect.Anthropic)
            {
                var existing = body["system"];
                var existingText = existing == null ? string.Empty : (existing.Type == JTokenType.String ? (string)existing : Translate.BlocksText(existing));
                body["system"] = existingText.Length == 0 ? prompt : prompt + "\n\n" + existingText;
            }
            else
            {
                ((JArray)body["messages"]).Insert(0, new JObject { { "role", "system" }, { "content", prompt } });
            }
            return run;
        }

        public static string LastUserText(JObject body)
        {
            var messages = body["messages"] as JArray;
            if (messages == null) return string.Empty;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var m = messages[i];
                if ((string)m["role"] != "user") continue;
                var content = m["content"];
                if (content == null) return string.Empty;
                if (content.Type == JTokenType.String) return ((string)content ?? string.Empty).Trim();
                var sb = new StringBuilder();
                foreach (var part in content)
                {
                    var t = part.At("text");
                    if (t != null && t.Type == JTokenType.String) sb.Append((string)t).Append('\n');
                }
                return sb.ToString().Trim();
            }
            return string.Empty;
        }
    }

    /// <summary>One cited-answer request: what was retrieved, and the check of the answer against it.</summary>
    public sealed class BearingRun
    {
        private static readonly Regex Marker = new Regex(@"\[(\d+(?:\s*,\s*\d+)*)\]", RegexOptions.CultureInvariant);
        private static readonly Regex Quoted = new Regex("[\"" + (char)0x201c + "]([^\"" + (char)0x201d + "]{12,400})[\"" + (char)0x201d + "]", RegexOptions.CultureInvariant);

        public string Question { get; set; }
        public List<BearingHit> Hits { get; set; }
        public List<BearingCitation> Citations { get; private set; }

        public BearingRun()
        {
            Question = string.Empty;
            Hits = new List<BearingHit>();
            Citations = new List<BearingCitation>();
        }

        /// <summary>Nothing in the folder matched: the request is answered here, without calling a model, so it cannot invent an answer.</summary>
        public bool NoSources { get { return Hits.Count == 0; } }

        public const string NoSourcesText = "I found nothing in the documents that answers this question.";

        /// <summary>The answer as a chat completion (OpenAI dialect); the caller translates it for an Anthropic client.</summary>
        public JObject NoSourcesAnswer()
        {
            var message = new JObject { { "role", "assistant" }, { "content", NoSourcesText } };
            var choice = new JObject { { "index", 0 }, { "message", message }, { "finish_reason", "stop" } };
            var output = new JObject { { "id", "chatcmpl-bearing" }, { "object", "chat.completion" }, { "created", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, { "model", "bearing" }, { "choices", new JArray { choice } } };
            output["usage"] = new JObject { { "prompt_tokens", 0 }, { "completion_tokens", 0 }, { "total_tokens", 0 } };
            output["fairlead_citations"] = new JArray();
            output["fairlead_bearing"] = Summary();
            return output;
        }

        /// <summary>
        /// Checks the model's answer against the sources and annotates it: <c>fairlead_citations</c> (one entry per source
        /// the answer cited, with file, lines, quote and link), <c>fairlead_bearing</c> and a Sources list at the end of the text.
        /// </summary>
        public void Apply(JObject output, Dialect dialect)
        {
            Check(AnswerText(output, dialect));
            output["fairlead_citations"] = CitationsJson;
            output["fairlead_bearing"] = BearingJson;
            if (Citations.Count > 0) AppendSources(output, dialect);
        }

        /// <summary>The citations as the response carries them, set by <see cref="Check"/>.</summary>
        public JArray CitationsJson { get; private set; }
        public JObject BearingJson { get; private set; }

        /// <summary>
        /// Checks an answer (all of its text) against the sources: fills <see cref="Citations"/>, <see cref="CitationsJson"/> and
        /// <see cref="BearingJson"/>. Used directly when the answer was streamed and only its text is at hand.
        /// </summary>
        public void Check(string answer)
        {
            var cited = new SortedDictionary<int, BearingCitation>();
            var unknown = new List<int>();
            foreach (Match m in Marker.Matches(answer))
            {
                var sentence = SentenceAround(answer, m.Index);
                foreach (var part in m.Groups[1].Value.Split(','))
                {
                    int n;
                    if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) continue;
                    if (n < 1 || n > Hits.Count) { if (!unknown.Contains(n)) unknown.Add(n); continue; }
                    var p = Hits[n - 1].Passage;
                    BearingCitation c;
                    if (!cited.TryGetValue(n, out c))
                    {
                        c = new BearingCitation { Number = n, Source = p.File, Location = p.Location, Heading = p.Heading, Link = p.Link };
                        cited[n] = c;
                    }
                    if (c.Verified) continue;
                    string verbatim = VerbatimQuote(sentence, p.Text);
                    if (verbatim != null) { c.Quote = verbatim; c.Verified = true; }
                    else if (c.Quote.Length == 0) c.Quote = ClosestSentence(sentence, p.Text);
                }
            }
            Citations = cited.Values.ToList();
            var citations = new JArray();
            foreach (var c in Citations)
                citations.Add(new JObject { { "n", c.Number }, { "source", c.Source }, { "location", c.Location }, { "heading", c.Heading }, { "quote", c.Quote }, { "verified", c.Verified }, { "link", c.Link } });
            CitationsJson = citations;
            var summary = Summary();
            summary["cited"] = Citations.Count;
            summary["verified"] = Citations.Count(c => c.Verified);
            if (unknown.Count > 0)
            {
                var bad = new JArray();
                foreach (var u in unknown) bad.Add(new JValue(u));
                summary["unknownSources"] = bad;
            }
            BearingJson = summary;
        }

        private JObject Summary()
        {
            var list = new JArray();
            for (var i = 0; i < Hits.Count; i++)
            {
                var p = Hits[i].Passage;
                list.Add(new JObject { { "n", i + 1 }, { "source", p.File }, { "location", p.Location }, { "link", p.Link } });
            }
            return new JObject { { "retrieved", Hits.Count }, { "sources", list } };
        }

        private static string AnswerText(JObject output, Dialect dialect)
        {
            if (dialect == Dialect.Anthropic) return Translate.BlocksText(output["content"]);
            var content = output["choices"].At(0).At("message").At("content");
            return content == null || content.Type != JTokenType.String ? string.Empty : (string)content;
        }

        private void AppendSources(JObject output, Dialect dialect)
        {
            var sb = new StringBuilder("\n\nSources:");
            foreach (var c in Citations)
                sb.Append("\n[").Append(c.Number).Append("] ").Append(c.Source).Append(" (").Append(c.Location).Append("): \"").Append(c.Quote).Append("\" ").Append(c.Link);
            if (dialect == Dialect.Anthropic)
            {
                var blocks = output["content"] as JArray;
                if (blocks == null) return;
                for (var i = blocks.Count - 1; i >= 0; i--)
                {
                    if ((string)blocks[i]["type"] != "text") continue;
                    blocks[i]["text"] = ((string)blocks[i]["text"] ?? string.Empty) + sb;
                    return;
                }
            }
            else
            {
                var message = output["choices"].At(0).At("message") as JObject;
                if (message != null) message["content"] = ((string)message["content"] ?? string.Empty) + sb;
            }
        }

        /// <summary>The model's quoted words if they occur word for word (case and spacing aside) in the passage.</summary>
        private static string VerbatimQuote(string sentence, string passage)
        {
            var flat = Flatten(passage);
            foreach (Match q in Quoted.Matches(sentence))
            {
                var wanted = Flatten(q.Groups[1].Value);
                if (wanted.Length >= 12 && flat.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return wanted;
            }
            return null;
        }

        /// <summary>The sentence of the passage that shares most words with the claim, shortened to 240 characters.</summary>
        private static string ClosestSentence(string claim, string passage)
        {
            var want = new HashSet<string>(BearingIndex.Tokens(claim), StringComparer.Ordinal);
            string best = null;
            var bestScore = -1;
            foreach (var s in Regex.Split(Flatten(passage), @"(?<=[.!?])\s+"))
            {
                if (s.Trim().Length == 0) continue;
                var score = BearingIndex.Tokens(s).Count(t => want.Contains(t));
                if (score > bestScore) { best = s.Trim(); bestScore = score; }
            }
            best = best ?? Flatten(passage);
            return best.Length > 240 ? best.Substring(0, 237).TrimEnd() + "..." : best;
        }

        private static string SentenceAround(string text, int index)
        {
            var start = index;
            while (start > 0 && ".!?\n".IndexOf(text[start - 1]) < 0) start--;
            var end = index;
            while (end < text.Length && ".!?\n".IndexOf(text[end]) < 0) end++;
            return text.Substring(start, Math.Min(text.Length, end + 1) - start).Trim();
        }

        private static string Flatten(string text)
        {
            var t = (text ?? string.Empty).Replace((char)0x2019, '\'').Replace((char)0x201c, '"').Replace((char)0x201d, '"');
            return Regex.Replace(t, @"\s+", " ").Trim();
        }
    }
}
