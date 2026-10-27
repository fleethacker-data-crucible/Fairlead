using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Config
{
    /// <summary>
    /// Turns a pasted curl command (the gateway's own example) into a one-endpoint, one-model gateway model file, so a
    /// gateway can be tried without writing YAML. The URL, the model and the request format come from the command; the
    /// key never does: the Authorization header only names the variable that holds it (Bearer $NAME), and a literal key
    /// in the text is ignored. Messages never quote the URL or the key.
    /// </summary>
    public sealed class CurlImport
    {
        private static readonly Regex TokenVar = new Regex("^\\$\\{?([A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\\}?$|^%([A-Za-z_][A-Za-z0-9_]*)%$");
        private static readonly Regex VariableName = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly string[] ValueOptions = { "-o", "--output", "-m", "--max-time", "--connect-timeout", "-A", "--user-agent", "-e", "--referer", "-b", "--cookie", "-w", "--write-out", "--retry", "--proxy", "-x" };

        public CurlImport()
        {
            Errors = new List<string>();
            Warnings = new List<string>();
        }

        public List<string> Errors { get; private set; }
        public List<string> Warnings { get; private set; }
        public bool Ok { get { return Errors.Count == 0; } }
        public string Url { get; private set; }
        /// <summary>"responses" (OpenAI Responses API) or "chat" (OpenAI Chat Completions).</summary>
        public string Format { get; private set; }
        public string Model { get; private set; }
        public string TokenVariable { get; private set; }
        public string EndpointKey { get; private set; }
        public string FileName { get; private set; }
        /// <summary>The generated model file; null when there are errors.</summary>
        public string Yaml { get; private set; }

        /// <param name="tokenVariable">The variable to read the key from when the command does not name one (for example AI_ENDPOINT_TOKEN).</param>
        public static CurlImport Parse(string command, string tokenVariable)
        {
            var c = new CurlImport();
            List<string> words;
            try { words = Split(command ?? string.Empty); }
            catch (FormatException ex) { c.Errors.Add(ex.Message); return c; }
            if (words.Count > 0 && (words[0] == "curl" || words[0].EndsWith("curl.exe", StringComparison.OrdinalIgnoreCase) || words[0].EndsWith("/curl", StringComparison.Ordinal))) words.RemoveAt(0);
            if (words.Count == 0) { c.Errors.Add("Paste the whole curl command, starting with curl."); return c; }

            string url = null, data = null, method = null, auth = null;
            for (var i = 0; i < words.Count; i++)
            {
                var w = words[i];
                Func<string> next = () =>
                {
                    if (i + 1 >= words.Count) throw new FormatException(w + " needs a value.");
                    return words[++i];
                };
                try
                {
                    if (w == "-X" || w == "--request") method = next();
                    else if (w.StartsWith("-X", StringComparison.Ordinal) && w.Length > 2) method = w.Substring(2);
                    else if (w == "-H" || w == "--header")
                    {
                        var h = next();
                        var colon = h.IndexOf(':');
                        if (colon > 0 && h.Substring(0, colon).Trim().Equals("Authorization", StringComparison.OrdinalIgnoreCase)) auth = h.Substring(colon + 1).Trim();
                    }
                    else if (w == "-d" || w == "--data" || w == "--data-raw" || w == "--data-binary" || w == "--json") data = next();
                    else if (w == "--url") url = next();
                    else if (w == "-u" || w == "--user") { next(); c.Errors.Add("The command sends a user name and password (-u); Fairlead only sends a bearer key."); }
                    else if (Array.IndexOf(ValueOptions, w) >= 0) next();
                    else if (w.StartsWith("-", StringComparison.Ordinal)) { /* -s, -L, -k, --compressed ... carry no value */ }
                    else if (url == null) url = w;
                }
                catch (FormatException ex) { c.Errors.Add(ex.Message); return c; }
            }
            if (method != null && !method.Equals("POST", StringComparison.OrdinalIgnoreCase)) c.Errors.Add("The command uses " + method.ToUpperInvariant() + "; a model gateway is called with POST.");

            // URL: an absolute https address, no placeholder, no query or credentials.
            if (url == null) c.Errors.Add("No URL found in the command.");
            else
            {
                url = url.Trim();
                Uri u;
                if (url.IndexOf('<') >= 0 || url.IndexOf('$') >= 0 || url.IndexOf('{') >= 0 || url.IndexOf('%') >= 0)
                    c.Errors.Add("The URL is still a placeholder or a variable. Put the endpoint's real https address in the command.");
                else if (!Uri.TryCreate(url, UriKind.Absolute, out u) || !(u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback))
                    || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.UserInfo) || u.AbsolutePath.Length <= 1)
                    c.Errors.Add("The URL must be the endpoint's full https address, with no query or credentials.");
                else c.Url = url;
            }

            // Body: the format and the model.
            JObject body = null;
            if (data == null) c.Errors.Add("No request body (-d) found; Fairlead needs it to tell the format and the model.");
            else
            {
                try { body = JObject.Parse(data); }
                catch (JsonException) { c.Errors.Add("The request body (-d) is not valid JSON."); }
                catch (InvalidCastException) { c.Errors.Add("The request body (-d) must be a JSON object."); }
            }
            if (body != null)
            {
                if (body["input"] != null) c.Format = "responses";
                else if (body["messages"] != null) c.Format = "chat";
                else c.Errors.Add("The body has neither input (Responses API) nor messages (Chat Completions).");
                var model = body["model"];
                if (model == null || model.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)model)) c.Errors.Add("The body has no model.");
                else
                {
                    c.Model = ((string)model).Trim();
                    if (c.Model.IndexOf('/') >= 0 || c.Model.IndexOf(' ') >= 0) c.Errors.Add("The model id cannot contain '/' or spaces.");
                }
            }

            // The key: only the name of the variable that holds it.
            var fallback = string.IsNullOrWhiteSpace(tokenVariable) ? "AI_ENDPOINT_TOKEN" : tokenVariable.Trim();
            if (!VariableName.IsMatch(fallback)) c.Errors.Add("The key variable name must be letters, digits and underscores.");
            c.TokenVariable = fallback;
            if (auth != null)
            {
                var value = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.Substring(7).Trim().Trim('"', '\'') : auth;
                var m = TokenVar.Match(value);
                if (m.Success) c.TokenVariable = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
                else c.Warnings.Add("The command contains a key; it was ignored and is not stored. Fairlead reads the key from " + fallback + " (share it from Lazarette, or set the environment variable).");
            }
            if (!c.Ok) return c;

            var slug = Slug(c.Model);
            c.EndpointKey = "curl_" + slug;
            c.FileName = "curl-" + slug.Replace('_', '-');
            c.Yaml = BuildYaml(c);
            return c;
        }

        private static string Slug(string model)
        {
            var sb = new StringBuilder();
            foreach (var ch in model.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            var s = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
            if (s.Length > 40) s = s.Substring(0, 40).TrimEnd('_');
            return s.Length == 0 ? "model" : s;
        }

        private static string Q(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string BuildYaml(CurlImport c)
        {
            var chat = c.Format == "chat";
            var sb = new StringBuilder();
            sb.Append("# Made by Fairlead from a pasted curl command. The key is read from ").Append(c.TokenVariable).Append(", never stored here.\n");
            sb.Append("environment:\n  required:\n    api_key:\n      env: ").Append(Q(c.TokenVariable)).Append("\n\n");
            sb.Append("endpoints:\n  ").Append(c.EndpointKey).Append(":\n");
            sb.Append("    method: \"POST\"\n");
            sb.Append("    url: ").Append(Q(c.Url)).Append("\n");
            sb.Append("    headers:\n      Content-Type: \"application/json\"\n      Authorization:\n        scheme: \"Bearer\"\n        token_env: ").Append(Q(c.TokenVariable)).Append("\n");
            sb.Append("    request_format: ").Append(Q(chat ? GatewayModelFile.ChatFormat : GatewayModelFile.SupportedFormat)).Append("\n");
            sb.Append("    request_body:\n      required_fields:\n        - \"model\"\n        - ").Append(chat ? "\"messages\"" : "\"input\"").Append("\n");
            if (!chat) sb.Append("      optional_fields:\n        - \"max_output_tokens\"\n");
            sb.Append("\nmodels:\n  ").Append(Slug(c.Model)).Append(":\n");
            sb.Append("    model_id: ").Append(Q(c.Model)).Append("\n");
            sb.Append("    endpoint: ").Append(Q(c.EndpointKey)).Append("\n");
            sb.Append("    enabled: true\n");
            return sb.ToString();
        }

        /// <summary>Splits a shell command line: single and double quotes, backslash escapes and line continuations (\ or ^ or ` at a line end).</summary>
        internal static List<string> Split(string text)
        {
            var s = Regex.Replace(text.Replace("\r\n", "\n"), "[\\\\^`][ \\t]*\\n", " ");
            var words = new List<string>();
            var sb = new StringBuilder();
            var inWord = false;
            for (var i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch == '\'')
                {
                    var end = s.IndexOf('\'', i + 1);
                    if (end < 0) throw new FormatException("A single quote in the command is not closed.");
                    sb.Append(s, i + 1, end - i - 1);
                    i = end;
                    inWord = true;
                }
                else if (ch == '"')
                {
                    var j = i + 1;
                    for (; j < s.Length && s[j] != '"'; j++)
                    {
                        if (s[j] == '\\' && j + 1 < s.Length && (s[j + 1] == '"' || s[j + 1] == '\\' || s[j + 1] == '$' || s[j + 1] == '`')) { sb.Append(s[++j]); continue; }
                        sb.Append(s[j]);
                    }
                    if (j >= s.Length) throw new FormatException("A double quote in the command is not closed.");
                    i = j;
                    inWord = true;
                }
                else if (ch == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); inWord = true; }
                else if (char.IsWhiteSpace(ch))
                {
                    if (inWord) { words.Add(sb.ToString()); sb.Length = 0; inWord = false; }
                }
                else { sb.Append(ch); inWord = true; }
            }
            if (inWord) words.Add(sb.ToString());
            return words;
        }
    }
}
