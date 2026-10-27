using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Config
{
    /// <summary>
    /// The AI gateway model file (config\ai_gateway_models.yaml, git-ignored): which gateway endpoints exist, how to
    /// call them, and which provider models each one serves. Its format is set by the gateway, not by Fairlead:
    /// <c>environment.required</c>, <c>endpoints.&lt;key&gt;</c> and <c>models.&lt;key&gt;</c>. The file holds no host and no
    /// token: base URLs and tokens are read from the environment variables it names. Loaded once at start; any error
    /// rejects the whole file. Messages name keys and variable names, never values.
    /// </summary>
    public sealed class GatewayModelFile
    {
        public const string PathVariable = "FAIRLEAD_GATEWAY_CONFIG";
        public const string TimeoutVariable = "AI_GATEWAY_TIMEOUT_SECONDS";
        public const string SupportedFormat = "openai_responses_v1";
        /// <summary>An OpenAI-compatible Chat Completions endpoint (POST {base}/chat/completions), sent the request as is.</summary>
        public const string ChatFormat = "openai_chat_completions_v1";
        private static readonly string[] ChatFormatAliases = { ChatFormat, "openai_chat_completions", "openai_chat_v1" };
        public const int DefaultRetries = 2;

        private static readonly Regex VariableName = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly Regex Slug = new Regex("^[A-Za-z0-9._-]{1,64}$");
        private static readonly string[] Sections = { "environment", "endpoints", "models" };

        public GatewayModelFile()
        {
            Errors = new List<string>();
            Warnings = new List<string>();
            Providers = new List<ProviderConfig>();
            EndpointKeys = new List<string>();
            ModelKeys = new List<string>();
            RequiredVariables = new List<string>();
            BaseUrlVariables = new List<string>();
            TokenVariables = new List<string>();
        }

        public string Path { get; set; }
        /// <summary>The name an imported file is kept under (from the file the user picked); null for a combined view.</summary>
        public string Name { get; set; }
        public List<string> Errors { get; private set; }
        public List<string> Warnings { get; private set; }
        /// <summary>One Responses provider per endpoint that serves at least one enabled model; empty when there are errors.</summary>
        public List<ProviderConfig> Providers { get; private set; }
        public List<string> EndpointKeys { get; private set; }
        public List<string> ModelKeys { get; private set; }
        public int EnabledModels { get; set; }
        /// <summary>Every environment variable the file names (base URLs, tokens, environment.required).</summary>
        public List<string> RequiredVariables { get; private set; }
        /// <summary>The variables named by endpoints.*.base_url_env (gateway addresses).</summary>
        public List<string> BaseUrlVariables { get; private set; }
        /// <summary>The variables named by endpoints.*.headers.Authorization.token_env (bearer tokens).</summary>
        public List<string> TokenVariables { get; private set; }

        public bool Ok { get { return Errors.Count == 0; } }

        /// <summary>FAIRLEAD_GATEWAY_CONFIG (relative paths are under the app root), else config\ai_gateway_models.yaml when it exists, else null.</summary>
        public static string ResolvePath(string rootDirectory, Func<string, string> env)
        {
            if (env == null) env = Environment.GetEnvironmentVariable;
            var configured = env(PathVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                configured = configured.Trim().Trim('"');
                return System.IO.Path.IsPathRooted(configured) ? configured : System.IO.Path.Combine(rootDirectory ?? string.Empty, configured);
            }
            var fallback = System.IO.Path.Combine(rootDirectory ?? string.Empty, "config", "ai_gateway_models.yaml");
            return File.Exists(fallback) ? fallback : null;
        }

        public static GatewayModelFile Load(string path, Func<string, string> env)
        {
            if (!File.Exists(path))
            {
                var missing = new GatewayModelFile { Path = path };
                missing.Errors.Add("The gateway model file was not found at " + path + ".");
                return missing;
            }
            return Parse(File.ReadAllText(path), path, env);
        }

        /// <summary>
        /// Checks the file's own structure, as if every variable it names were set, so an import can be refused before
        /// anything is saved. Missing values are not errors here; the status page asks for them.
        /// </summary>
        public static GatewayModelFile Inspect(string text, string path)
        {
            return Parse(text, path, name => name == TimeoutVariable ? null : "https://placeholder.invalid");
        }

        public static GatewayModelFile Parse(string text, string path, Func<string, string> env)
        {
            if (env == null) env = Environment.GetEnvironmentVariable;
            var f = new GatewayModelFile { Path = path };
            JToken root;
            try { root = YamlReader.Parse(text); }
            catch (JsonException ex)
            {
                f.Errors.Add(ex.Message);
                return f;
            }
            var doc = root as JObject;
            if (doc == null)
            {
                f.Errors.Add("The file must be a mapping with the sections endpoints and models.");
                return f;
            }
            foreach (var p in doc.Properties())
                if (!Sections.Contains(p.Name)) f.Warnings.Add("Section '" + p.Name + "' is not used by Fairlead; it is ignored.");

            f.ReadEnvironment(doc["environment"], env);
            var endpoints = f.ReadEndpoints(doc["endpoints"], env);
            var models = f.ReadModels(doc["models"], endpoints);
            if (!f.Ok) return f;

            int timeout = 120;
            var timeoutText = env(TimeoutVariable);
            if (!string.IsNullOrWhiteSpace(timeoutText) && (!int.TryParse(timeoutText.Trim(), out timeout) || timeout < 5 || timeout > 3600))
            {
                f.Errors.Add("Environment variable " + TimeoutVariable + " must be a whole number of seconds from 5 to 3600.");
                return f;
            }
            foreach (var e in endpoints)
            {
                var served = models.Where(m => m.Enabled && m.Endpoint == e.Key).ToList();
                if (served.Count == 0)
                {
                    f.Warnings.Add("Endpoint '" + e.Key + "' serves no enabled model; it is not added.");
                    continue;
                }
                f.Providers.Add(new ProviderConfig
                {
                    Id = e.Key,
                    Name = "AI gateway: " + e.Key,
                    Kind = e.Chat ? ProviderKind.OpenAi : ProviderKind.Responses,
                    BaseUrl = e.BaseUrl,
                    Path = e.Chat ? null : e.PathPart,
                    ApiKeyEnv = e.TokenVariable,
                    Headers = e.Headers,
                    RequestFields = e.Chat ? new List<string>() : e.OptionalFields,
                    Local = false,
                    Enabled = true,
                    StreamUsage = false,
                    TimeoutSeconds = timeout,
                    Retries = DefaultRetries,
                    Managed = true,
                    // Capability labels in the file are preferences, not tested facts. A Responses endpoint gets text only;
                    // a chat endpoint takes the request as is, so its capabilities stay unknown (never excluded).
                    Models = served.Select(m => new ModelInfo { Id = m.ModelId, Tools = e.Chat ? (bool?)null : false, Vision = e.Chat ? (bool?)null : false }).ToList(),
                });
            }
            if (f.EnabledModels == 0) f.Warnings.Add("No model is enabled; Fairlead adds no gateway provider.");
            return f;
        }

        private sealed class Endpoint
        {
            public string Key;
            public bool Chat;
            public string BaseUrl;
            public string PathPart;
            public string TokenVariable;
            public Dictionary<string, string> Headers;
            public List<string> OptionalFields;
        }

        private sealed class Model
        {
            public string Key;
            public string ModelId;
            public string Endpoint;
            public bool Enabled;
        }

        private void NeedVariable(string name, string where, Func<string, string> env)
        {
            // One message per variable, however many places name it.
            if (RequiredVariables.Contains(name)) return;
            RequiredVariables.Add(name);
            if (string.IsNullOrWhiteSpace(env(name))) Errors.Add(name + " (" + where + ") is not set.");
        }

        private string VariableAt(JToken token, string where)
        {
            if (token == null || token.Type == JTokenType.Null) { Errors.Add(where + " is missing."); return null; }
            if (token.Type != JTokenType.String) { Errors.Add(where + " must be an environment variable name."); return null; }
            var name = ((string)token).Trim();
            if (!VariableName.IsMatch(name)) { Errors.Add(where + " must be an environment variable name (letters, digits, underscore)."); return null; }
            return name;
        }

        private void ReadEnvironment(JToken section, Func<string, string> env)
        {
            if (section == null || section.Type == JTokenType.Null) return;
            var o = section as JObject;
            if (o == null) { Errors.Add("environment must be a mapping."); return; }
            var required = o["required"];
            if (required == null || required.Type == JTokenType.Null) return;
            var r = required as JObject;
            if (r == null) { Errors.Add("environment.required must be a mapping."); return; }
            foreach (var p in r.Properties())
            {
                var where = "environment.required." + p.Name;
                var item = p.Value as JObject;
                if (item == null) { Errors.Add(where + " must be a mapping with env."); continue; }
                var name = VariableAt(item["env"], where + ".env");
                if (name != null) NeedVariable(name, where, env);
            }
        }

        private List<Endpoint> ReadEndpoints(JToken section, Func<string, string> env)
        {
            var list = new List<Endpoint>();
            var o = section as JObject;
            if (o == null || o.Count == 0) { Errors.Add("The endpoints section is missing or empty."); return list; }
            foreach (var p in o.Properties())
            {
                var key = p.Name;
                EndpointKeys.Add(key);
                var where = "endpoints." + key;
                var e = p.Value as JObject;
                if (e == null) { Errors.Add(where + " must be a mapping."); continue; }
                if (!Slug.IsMatch(key)) { Errors.Add(where + ": the key must be letters, digits, dot, dash or underscore (it becomes the provider id)."); continue; }
                var errorsBefore = Errors.Count;

                var method = e["method"];
                if (method != null && !(method.Type == JTokenType.String && string.Equals((string)method, "POST", StringComparison.OrdinalIgnoreCase)))
                    Errors.Add(where + ".method must be POST.");

                var format = e["request_format"];
                if (format == null || format.Type != JTokenType.String) Errors.Add(where + ".request_format is missing.");
                else if ((string)format != SupportedFormat && Array.IndexOf(ChatFormatAliases, (string)format) < 0)
                    Errors.Add(where + ".request_format '" + (string)format + "' is not supported; Fairlead speaks " + SupportedFormat + " and " + ChatFormat + ".");
                var chat = format != null && format.Type == JTokenType.String && Array.IndexOf(ChatFormatAliases, (string)format) >= 0;

                string path = null;
                string baseUrl = null;
                var urlToken = e["url"];
                if (urlToken != null && urlToken.Type != JTokenType.Null)
                {
                    // The whole address in the file (it is kept encrypted once imported): no address variable to set.
                    if (e["base_url_env"] != null || e["path"] != null) Errors.Add(where + ": give url, or base_url_env with path, not both.");
                    else if (urlToken.Type != JTokenType.String) Errors.Add(where + ".url must be text.");
                    else
                    {
                        var value = ((string)urlToken).Trim();
                        Uri u;
                        var ok = Uri.TryCreate(value, UriKind.Absolute, out u) && (u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback))
                            && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment) && string.IsNullOrEmpty(u.UserInfo) && u.AbsolutePath.Length > 1;
                        if (value.StartsWith("<", StringComparison.Ordinal)) Errors.Add(where + ".url still holds a placeholder; put the endpoint's full https address there.");
                        else if (!ok) Errors.Add(where + ".url must be the endpoint's full https address (for example https://gateway.example/ai-gateway/openai/v1/responses), with no query or credentials.");
                        else
                        {
                            baseUrl = u.GetLeftPart(UriPartial.Authority);
                            path = u.AbsolutePath;
                        }
                    }
                }
                else
                {
                    var pathToken = e["path"];
                    if (pathToken == null || pathToken.Type != JTokenType.String) { if (!chat) Errors.Add(where + ".path is missing."); }
                    else
                    {
                        path = ((string)pathToken).Trim();
                        if (!path.StartsWith("/", StringComparison.Ordinal) || path.Contains("://") || path.IndexOf('?') >= 0 || path.IndexOf('#') >= 0 || path.IndexOf(' ') >= 0)
                            Errors.Add(where + ".path must be a path starting with / (no host, query or spaces).");
                    }

                    var baseVar = VariableAt(e["base_url_env"], where + ".base_url_env");
                    if (baseVar != null)
                    {
                        NeedVariable(baseVar, where + ".base_url_env", env);
                        if (!BaseUrlVariables.Contains(baseVar)) BaseUrlVariables.Add(baseVar);
                        var value = env(baseVar);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            Uri u;
                            value = value.Trim();
                            var loopback = Uri.TryCreate(value, UriKind.Absolute, out u) && u.IsLoopback;
                            if (u == null || (u.Scheme != "https" && !(loopback && u.Scheme == "http")) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || !string.IsNullOrEmpty(u.UserInfo))
                                Errors.Add("The gateway address in " + baseVar + " must hold an absolute https URL with no query or credentials.");
                            else baseUrl = value.TrimEnd('/');
                        }
                    }
                }
                if (chat && baseUrl != null)
                {
                    // A chat endpoint is called at {base}/chat/completions: accept the full address or the base.
                    var full = baseUrl.TrimEnd('/') + (path ?? string.Empty);
                    full = full.TrimEnd('/');
                    if (full.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) full = full.Substring(0, full.Length - "/chat/completions".Length);
                    baseUrl = full;
                    path = null;
                }

                string tokenVar = null;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var h = e["headers"];
                if (h != null && h.Type != JTokenType.Null && !(h is JObject)) Errors.Add(where + ".headers must be a mapping.");
                var ho = h as JObject;
                if (ho != null)
                {
                    foreach (var hp in ho.Properties())
                    {
                        var hw = where + ".headers." + hp.Name;
                        if (string.Equals(hp.Name, "Authorization", StringComparison.OrdinalIgnoreCase))
                        {
                            var auth = hp.Value as JObject;
                            if (auth == null) { Errors.Add(hw + " must be a mapping with scheme and token_env; never put a token in this file."); continue; }
                            var scheme = auth["scheme"];
                            if (scheme == null || scheme.Type != JTokenType.String || !string.Equals((string)scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                                Errors.Add(hw + ".scheme must be Bearer.");
                            tokenVar = VariableAt(auth["token_env"], hw + ".token_env");
                            if (tokenVar != null)
                            {
                                NeedVariable(tokenVar, hw + ".token_env", env);
                                if (!TokenVariables.Contains(tokenVar)) TokenVariables.Add(tokenVar);
                            }
                            continue;
                        }
                        if (hp.Value.Type != JTokenType.String) { Errors.Add(hw + " must be a text value."); continue; }
                        var hv = (string)hp.Value;
                        if (string.Equals(hp.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!hv.Trim().StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) Errors.Add(hw + " must be application/json.");
                            continue;
                        }
                        var lower = hp.Name.ToLowerInvariant();
                        if (lower.Contains("key") || lower.Contains("token") || lower.Contains("secret") || lower.Contains("auth") || lower.Contains("cookie"))
                        {
                            Errors.Add(hw + " looks like a credential; secrets never belong in this file. Use Authorization with token_env.");
                            continue;
                        }
                        headers[hp.Name] = hv;
                    }
                }
                if (ho == null || !ho.Properties().Any(x => string.Equals(x.Name, "Authorization", StringComparison.OrdinalIgnoreCase)))
                    Errors.Add(where + ".headers.Authorization (scheme Bearer, token_env) is missing.");

                var optional = new List<string>();
                var rb = e["request_body"];
                if (rb != null && rb.Type != JTokenType.Null)
                {
                    var rbo = rb as JObject;
                    if (rbo == null) Errors.Add(where + ".request_body must be a mapping.");
                    else
                    {
                        var required = Strings(rbo["required_fields"], where + ".request_body.required_fields");
                        if (required != null)
                        {
                            var body = chat ? "messages" : "input";
                            foreach (var r in required)
                                if (r != "model" && r != body) Errors.Add(where + ".request_body.required_fields: Fairlead cannot fill '" + r + "'.");
                            if (!required.Contains("model") || !required.Contains(body)) Errors.Add(where + ".request_body.required_fields must list model and " + body + ".");
                        }
                        var opt = Strings(rbo["optional_fields"], where + ".request_body.optional_fields");
                        if (opt != null && !chat)
                        {
                            foreach (var o2 in opt)
                            {
                                if (ResponsesApi.KnownOptionalFields.Contains(o2)) optional.Add(o2);
                                else Warnings.Add(where + ".request_body.optional_fields: '" + o2 + "' is not filled by Fairlead and is never sent.");
                            }
                        }
                    }
                }
                if (Errors.Count == errorsBefore)
                    list.Add(new Endpoint { Key = key, Chat = chat, BaseUrl = baseUrl, PathPart = path, TokenVariable = tokenVar, Headers = headers, OptionalFields = optional });
            }
            return list;
        }

        private List<string> Strings(JToken token, string where)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var a = token as JArray;
            if (a == null || a.Any(x => x.Type != JTokenType.String)) { Errors.Add(where + " must be a list of names."); return null; }
            return a.Select(x => ((string)x).Trim()).ToList();
        }

        private List<Model> ReadModels(JToken section, List<Endpoint> endpoints)
        {
            var list = new List<Model>();
            var o = section as JObject;
            if (o == null || o.Count == 0) { Errors.Add("The models section is missing or empty."); return list; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in o.Properties())
            {
                ModelKeys.Add(p.Name);
                var where = "models." + p.Name;
                var m = p.Value as JObject;
                if (m == null) { Errors.Add(where + " must be a mapping."); continue; }
                var idToken = m["model_id"];
                var id = idToken != null && idToken.Type == JTokenType.String ? ((string)idToken).Trim() : null;
                if (string.IsNullOrEmpty(id)) Errors.Add(where + ".model_id is missing.");
                else if (id.IndexOf('/') >= 0 || id.Any(char.IsWhiteSpace)) Errors.Add(where + ".model_id cannot contain '/' or spaces.");
                var epToken = m["endpoint"];
                var ep = epToken != null && epToken.Type == JTokenType.String ? ((string)epToken).Trim() : null;
                if (string.IsNullOrEmpty(ep)) Errors.Add(where + ".endpoint is missing.");
                else if (!EndpointKeys.Contains(ep)) Errors.Add(where + ".endpoint refers to '" + ep + "', which is not under endpoints.");
                var enabledToken = m["enabled"];
                if (enabledToken == null || enabledToken.Type != JTokenType.Boolean) Errors.Add(where + ".enabled must be true or false.");
                var enabled = enabledToken != null && enabledToken.Type == JTokenType.Boolean && (bool)enabledToken;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(ep) && !seen.Add(ep + "/" + id)) Errors.Add(where + ": another model under endpoint '" + ep + "' has the same model_id.");
                if (enabled) EnabledModels++;
                list.Add(new Model { Key = p.Name, ModelId = id, Endpoint = ep, Enabled = enabled });
            }
            return list;
        }

        /// <summary>
        /// Several model files as one: their providers, keys and variables together. Each file stands alone: one with
        /// problems adds nothing, and the others still load. An endpoint key used in two files is an error in the later
        /// one (provider ids must be unique). With more than one file, problems are prefixed with the file's name.
        /// </summary>
        public static GatewayModelFile Combine(IList<GatewayModelFile> files)
        {
            if (files.Count == 1) return files[0];
            var c = new GatewayModelFile { Path = files.Count + " model files" };
            var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                foreach (var ep in f.EndpointKeys)
                    if (!endpoints.Add(ep)) f.Errors.Add("Endpoint '" + ep + "' is also in another model file; rename it in one of them.");
                if (!f.Ok) f.Providers.Clear();
                var prefix = string.IsNullOrEmpty(f.Name) ? string.Empty : f.Name + ": ";
                foreach (var e in f.Errors) if (!c.Errors.Contains(prefix + e)) c.Errors.Add(prefix + e);
                foreach (var w in f.Warnings) if (!c.Warnings.Contains(prefix + w)) c.Warnings.Add(prefix + w);
                c.Providers.AddRange(f.Providers);
                c.EndpointKeys.AddRange(f.EndpointKeys);
                c.ModelKeys.AddRange(f.ModelKeys);
                c.EnabledModels += f.EnabledModels;
                foreach (var v in f.RequiredVariables) if (!c.RequiredVariables.Contains(v)) c.RequiredVariables.Add(v);
                foreach (var v in f.BaseUrlVariables) if (!c.BaseUrlVariables.Contains(v)) c.BaseUrlVariables.Add(v);
                foreach (var v in f.TokenVariables) if (!c.TokenVariables.Contains(v)) c.TokenVariables.Add(v);
            }
            return c;
        }

        /// <summary>Lines for the console and the status page: counts, keys and problems; never URLs, tokens or model ids.</summary>
        public List<string> Summary()
        {
            var lines = new List<string>();
            lines.Add((Ok ? "OK" : "INVALID") + ": " + Path);
            lines.Add("  endpoints: " + (EndpointKeys.Count == 0 ? "none" : string.Join(", ", EndpointKeys)) + "; models: " + ModelKeys.Count + " (" + EnabledModels + " enabled)");
            if (RequiredVariables.Count > 0) lines.Add("  environment variables: " + string.Join(", ", RequiredVariables));
            foreach (var p in Providers) lines.Add("  provider '" + p.Id + "': " + p.Models.Count + " models, " + (p.Retries ?? 0) + " retries, timeout " + p.TimeoutSeconds + " s");
            foreach (var e in Errors) lines.Add("  error: " + e);
            foreach (var w in Warnings) lines.Add("  note: " + w);
            return lines;
        }
    }
}
