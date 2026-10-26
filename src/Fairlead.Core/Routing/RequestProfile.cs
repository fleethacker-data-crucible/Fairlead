using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Routing
{
    /// <summary>What the router knows about a request before choosing a target. Built once, read by every rule.</summary>
    public sealed class RequestProfile
    {
        public Dialect Dialect { get; set; }
        public string RequestedModel { get; set; }
        public bool Stream { get; set; }
        /// <summary>Rough prompt size (characters / 4, plus tool schemas). Good enough to pick a context window.</summary>
        public int EstimatedTokens { get; set; }
        public int? MaxOutputTokens { get; set; }
        public bool HasTools { get; set; }
        public bool HasImages { get; set; }
        /// <summary>The latest user message, for keyword rules. Never logged unless content logging is on.</summary>
        public string LastUserText { get; set; }
        public bool Sensitive { get; set; }
        /// <summary>Why the request is sensitive ("header", "aws-key", "private-key"...).</summary>
        public List<string> SensitiveReasons { get; set; }
        public string ClientName { get; set; }
        /// <summary>X-Fairlead-Route: the caller asks for a route by header (overrides the model name).</summary>
        public string RouteOverride { get; set; }

        public RequestProfile()
        {
            RequestedModel = string.Empty;
            LastUserText = string.Empty;
            SensitiveReasons = new List<string>();
        }

        public static RequestProfile From(JObject body, Dialect dialect, bool detectSecrets)
        {
            var p = new RequestProfile
            {
                Dialect = dialect,
                RequestedModel = (string)body["model"] ?? string.Empty,
                Stream = body.Value<bool?>("stream") == true,
            };
            var chars = 0;
            var all = new StringBuilder();
            if (dialect == Dialect.Anthropic)
            {
                var sys = Translate.BlocksText(body["system"]);
                chars += sys.Length;
                all.Append(sys).Append('\n');
                p.MaxOutputTokens = (int?)body["max_tokens"];
            }
            else
            {
                p.MaxOutputTokens = (int?)body["max_completion_tokens"] ?? (int?)body["max_tokens"];
            }
            foreach (var m in Translate.Arr(body["messages"]))
            {
                var role = (string)m["role"];
                var content = m["content"];
                var text = new StringBuilder();
                if (content != null && content.Type == JTokenType.String) text.Append((string)content);
                foreach (var part in Translate.Arr(content))
                {
                    var type = (string)part["type"];
                    if (type == "image" || type == "image_url") { p.HasImages = true; chars += 1500 * 4; continue; }
                    if (type == "text") text.Append((string)part["text"]).Append('\n');
                    else if (type == "tool_result") text.Append(Translate.BlocksText(part["content"])).Append('\n');
                    else if (type == "tool_use") text.Append(part["input"] == null ? null : part["input"].ToString()).Append('\n');
                }
                foreach (var call in Translate.Arr(m["tool_calls"])) text.Append((string)call["function"].At("arguments")).Append('\n');
                chars += text.Length;
                all.Append(text).Append('\n');
                if (role == "user")
                {
                    var t = text.ToString().Trim();
                    // A user turn that only carries tool results is not what the person asked; keep the last real one.
                    var onlyResults = Translate.Arr(content).Any() && Translate.Arr(content).All(b => (string)b["type"] == "tool_result");
                    if (t.Length > 0 && !onlyResults) p.LastUserText = t;
                }
            }
            var tools = body["tools"] as JArray;
            if (tools != null && tools.Count > 0)
            {
                p.HasTools = true;
                chars += tools.ToString(Formatting.None).Length;
            }
            p.EstimatedTokens = (int)Math.Ceiling(chars / 4.0);
            if (detectSecrets)
            {
                foreach (var reason in SecretScanner.Scan(all.ToString())) p.MarkSensitive(reason);
            }
            return p;
        }

        public void MarkSensitive(string reason)
        {
            Sensitive = true;
            if (!SensitiveReasons.Contains(reason)) SensitiveReasons.Add(reason);
        }
    }

    /// <summary>
    /// Finds credentials and private material in a prompt so it can be kept on this machine. Patterns are deliberately
    /// specific (a word like "password" alone is not enough) to avoid sending every prompt local.
    /// </summary>
    public static class SecretScanner
    {
        private static readonly KeyValuePair<string, Regex>[] Patterns =
        {
            P("private-key", @"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----"),
            P("aws-key", @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),
            P("api-key", @"\b(?:sk-ant-[A-Za-z0-9_\-]{20,}|sk-(?:proj-)?[A-Za-z0-9_\-]{32,}|AIza[0-9A-Za-z_\-]{35}|gsk_[A-Za-z0-9]{40,}|xox[baprs]-[A-Za-z0-9-]{10,})\b"),
            P("github-token", @"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{60,})\b"),
            P("jwt", @"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b"),
            P("connection-string", @"(?i)\b(?:password|pwd)\s*=\s*[^;\s]{4,};"),
            P("password-assignment", @"(?i)(?<![a-z])(?:password|passwd|secret|api[_-]?key)\s*[:=]\s*[""'][^""'\s]{6,}[""']"),
            P("us-ssn", @"\b(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b"),
        };

        private static KeyValuePair<string, Regex> P(string name, string pattern)
        {
            return new KeyValuePair<string, Regex>(name, new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant));
        }

        public static IEnumerable<string> Scan(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            foreach (var p in Patterns)
            {
                if (p.Value.IsMatch(text)) yield return p.Key;
            }
            if (HasCardNumber(text)) yield return "card-number";
        }

        // Only the grouped way people write card numbers (4-4-4-4, Amex 4-6-5) and pass Luhn: long ids and
        // timestamps in logs would otherwise send one prompt in ten local.
        private static readonly Regex CardCandidate = new Regex(@"\b(?:[3-6]\d{3}[ -]\d{4}[ -]\d{4}[ -]\d{4}|3[47]\d{2}[ -]\d{6}[ -]\d{5})\b", RegexOptions.Compiled);

        private static bool HasCardNumber(string text)
        {
            foreach (Match m in CardCandidate.Matches(text))
            {
                var digits = new string(m.Value.Where(char.IsDigit).ToArray());
                if (Luhn(digits)) return true;
            }
            return false;
        }

        private static bool Luhn(string digits)
        {
            var sum = 0;
            var alt = false;
            for (var i = digits.Length - 1; i >= 0; i--)
            {
                var n = digits[i] - '0';
                if (alt) { n *= 2; if (n > 9) n -= 9; }
                sum += n;
                alt = !alt;
            }
            return sum % 10 == 0;
        }
    }
}
