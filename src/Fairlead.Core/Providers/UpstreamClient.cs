using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Protocol;
using Fairlead.Core.JsonLite;

namespace Fairlead.Core.Providers
{
    /// <summary>Why an upstream call failed; decides failover and how the circuit breaker reacts.</summary>
    public enum FailureKind
    {
        /// <summary>DNS, refused or unreachable: the machine is probably offline or the server is not running.</summary>
        Network,
        /// <summary>No answer in time.</summary>
        Timeout,
        /// <summary>5xx or an overloaded answer.</summary>
        Server,
        RateLimited,
        /// <summary>401/403: the key is wrong. Retrying will not help until it is changed.</summary>
        Auth,
        /// <summary>The model is not served by this provider (404 or "model not found").</summary>
        ModelNotFound,
        /// <summary>400/422: the request itself was rejected. Not retried elsewhere.</summary>
        BadRequest,
    }

    public sealed class UpstreamException : Exception
    {
        public UpstreamException(FailureKind kind, string message, int? status = null, TimeSpan? retryAfter = null, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
            Status = status;
            RetryAfter = retryAfter;
        }

        public FailureKind Kind { get; private set; }
        public int? Status { get; private set; }
        public TimeSpan? RetryAfter { get; private set; }
        /// <summary>The upstream error body, when there was one (already redacted).</summary>
        public string Body { get; set; }
    }

    /// <summary>A successful (2xx) upstream answer whose body has not been read yet.</summary>
    public sealed class UpstreamResponse : IDisposable
    {
        private readonly HttpResponseMessage _message;
        private readonly CancellationTokenSource _cts;
        private readonly int _idleMs;

        internal UpstreamResponse(HttpResponseMessage message, CancellationTokenSource cts, int idleMs, TimeSpan headersAfter)
        {
            _message = message;
            _cts = cts;
            _idleMs = idleMs;
            HeadersAfter = headersAfter;
        }

        public int Status { get { return (int)_message.StatusCode; } }
        public TimeSpan HeadersAfter { get; private set; }
        public CancellationToken Token { get { return _cts.Token; } }

        public async Task<string> ReadTextAsync()
        {
            return await _message.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public async Task<Stream> OpenStreamAsync()
        {
            return await _message.Content.ReadAsStreamAsync().ConfigureAwait(false);
        }

        /// <summary>Restarts the idle timer: a stream that goes quiet for the provider timeout is cut.</summary>
        public void Touch()
        {
            try { _cts.CancelAfter(_idleMs); } catch (ObjectDisposedException) { }
        }

        public void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _message.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// The only place Fairlead makes HTTP calls to model providers. One shared HttpClient (connection pooling, the
    /// system proxy); per-call timeouts through cancellation; no redirects, so a key is never forwarded somewhere
    /// nobody configured.
    /// </summary>
    public sealed class UpstreamClient
    {
        public const string AnthropicVersion = "2023-06-01";
        private readonly HttpClient _http;

        static UpstreamClient()
        {
            // .NET Framework defaults: TLS 1.0 and two connections per host. Both break a gateway.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (NotSupportedException) { }
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 64);
            ServicePointManager.Expect100Continue = false;
        }

        public UpstreamClient(HttpMessageHandler handler = null)
        {
            if (handler == null)
            {
                handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    UseProxy = true,
                };
            }
            _http = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fairlead/" + Host.FairleadHost.Version);
        }

        public static Dialect DialectOf(ProviderKind kind)
        {
            return kind == ProviderKind.Anthropic ? Dialect.Anthropic : Dialect.OpenAi;
        }

        /// <summary>The OpenAI-compatible /v1 base: Ollama serves it under /v1 of its root.</summary>
        public static string OpenAiBase(ProviderConfig p)
        {
            var b = p.BaseUrl.TrimEnd('/');
            return p.Kind == ProviderKind.Ollama ? b + "/v1" : b;
        }

        public static Uri EndpointUri(ProviderConfig p, string operation)
        {
            if (p.Kind == ProviderKind.Anthropic)
            {
                var root = p.BaseUrl.TrimEnd('/');
                if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root.Substring(0, root.Length - 3);
                switch (operation)
                {
                    case "chat": return new Uri(root + "/v1/messages");
                    case "count_tokens": return new Uri(root + "/v1/messages/count_tokens");
                    case "models": return new Uri(root + "/v1/models?limit=1000");
                    default: throw new NotSupportedException("Anthropic does not serve '" + operation + "'.");
                }
            }
            var baseUrl = OpenAiBase(p);
            switch (operation)
            {
                case "chat": return new Uri(baseUrl + "/chat/completions");
                case "embeddings": return new Uri(baseUrl + "/embeddings");
                case "models": return new Uri(p.Kind == ProviderKind.Ollama ? p.BaseUrl.TrimEnd('/') + "/api/tags" : baseUrl + "/models");
                default: throw new NotSupportedException("'" + operation + "' is not a known operation.");
            }
        }

        /// <summary>
        /// Sends one request and returns once the response headers are in. Non-2xx answers, network failures and
        /// timeouts all surface as <see cref="UpstreamException"/> with a <see cref="FailureKind"/>.
        /// </summary>
        public async Task<UpstreamResponse> SendAsync(ProviderConfig p, string operation, JObject body, CancellationToken token, IDictionary<string, string> passHeaders = null)
        {
            var timeoutMs = Math.Max(5, p.TimeoutSeconds) * 1000;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);
            var request = new HttpRequestMessage(HttpMethod.Post, EndpointUri(p, operation))
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            ApplyAuth(request, p);
            if (passHeaders != null)
            {
                foreach (var h in passHeaders) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            if (body.Value<bool?>("stream") == true) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                cts.Dispose();
                if (token.IsCancellationRequested) throw;
                throw new UpstreamException(FailureKind.Timeout, p.Id + " did not answer within " + p.TimeoutSeconds + " s.", null, null, ex);
            }
            catch (HttpRequestException ex)
            {
                cts.Dispose();
                throw new UpstreamException(FailureKind.Network, p.Id + " is unreachable: " + Describe(ex), null, null, ex);
            }
            finally
            {
                request.Dispose();
            }
            var status = (int)response.StatusCode;
            if (status >= 200 && status < 300) return new UpstreamResponse(response, cts, timeoutMs, sw.Elapsed);
            string text;
            try { text = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch (Exception) { text = string.Empty; }
            var retryAfter = RetryAfter(response);
            response.Dispose();
            cts.Dispose();
            throw Classify(p, status, text, retryAfter);
        }

        public static UpstreamException Classify(ProviderConfig p, int status, string body, TimeSpan? retryAfter)
        {
            var message = ErrorMessage(body);
            var lower = (message + " " + body).ToLowerInvariant();
            FailureKind kind;
            if (status == 401 || status == 403) kind = FailureKind.Auth;
            else if (status == 429) kind = FailureKind.RateLimited;
            else if (status == 404 || (status == 400 && (lower.Contains("model not found") || lower.Contains("model_not_found") || lower.Contains("does not exist") || lower.Contains("not found, try pulling")))) kind = FailureKind.ModelNotFound;
            else if (status == 408 || status == 504) kind = FailureKind.Timeout;
            else if (status >= 500 || status == 529) kind = FailureKind.Server;
            else kind = FailureKind.BadRequest;
            var text = p.Id + " answered HTTP " + status + (string.IsNullOrEmpty(message) ? string.Empty : ": " + message);
            return new UpstreamException(kind, Redactor.Redact(text), status, retryAfter) { Body = Redactor.Redact(body.Length > 4000 ? body.Substring(0, 4000) : body) };
        }

        /// <summary>The model list. Ollama: /api/tags with capabilities and context; others: /models.</summary>
        public async Task<List<ModelInfo>> ListModelsAsync(ProviderConfig p, CancellationToken token, int timeoutMs = 8000)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(timeoutMs);
                using (var request = new HttpRequestMessage(HttpMethod.Get, EndpointUri(p, "models")))
                {
                    ApplyAuth(request, p);
                    HttpResponseMessage response;
                    try
                    {
                        response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        if (token.IsCancellationRequested) throw;
                        throw new UpstreamException(FailureKind.Timeout, p.Id + " did not list its models within " + (timeoutMs / 1000) + " s.", null, null, ex);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new UpstreamException(FailureKind.Network, p.Id + " is unreachable: " + Describe(ex), null, null, ex);
                    }
                    using (response)
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var status = (int)response.StatusCode;
                        if (status < 200 || status >= 300) throw Classify(p, status, text, RetryAfter(response));
                        return ParseModels(p, text);
                    }
                }
            }
        }

        /// <summary>Ollama only: the models loaded in memory (/api/ps). Empty when unknown; never throws.</summary>
        public async Task<List<string>> ListLoadedAsync(ProviderConfig p, CancellationToken token)
        {
            var list = new List<string>();
            if (p.Kind != ProviderKind.Ollama) return list;
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(3000);
                    using (var response = await _http.GetAsync(p.BaseUrl.TrimEnd('/') + "/api/ps", cts.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return list;
                        var root = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        foreach (var m in Translate.Arr(root["models"])) { var n = (string)m["name"]; if (!string.IsNullOrEmpty(n)) list.Add(n); }
                    }
                }
            }
            catch (Exception ex) { if (!(ex is HttpRequestException || ex is OperationCanceledException || ex is JsonException)) throw; }
            return list;
        }

        private static readonly System.Text.RegularExpressions.Regex NonChat = new System.Text.RegularExpressions.Regex(
            @"(dall-e|whisper|tts|moderation|davinci|babbage|-audio|realtime|transcribe|gpt-image|sora|computer-use|-search-preview)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static List<ModelInfo> ParseModels(ProviderConfig p, string text)
        {
            var list = new List<ModelInfo>();
            JObject root;
            try { root = JObject.Parse(text); }
            catch (JsonException) { return list; }
            if (p.Kind == ProviderKind.Ollama)
            {
                foreach (var m in Translate.Arr(root["models"]))
                {
                    var caps = Translate.Arr(m["capabilities"]).Select(c => (string)c).ToList();
                    var info = new ModelInfo
                    {
                        Id = (string)m["name"] ?? (string)m["model"] ?? string.Empty,
                        ContextWindow = (int?)m["details"].Get("context_length"),
                        Discovered = true,
                    };
                    if (caps.Count > 0)
                    {
                        info.Tools = caps.Contains("tools");
                        info.Vision = caps.Contains("vision");
                        info.Embedding = caps.Contains("embedding") && !caps.Contains("completion");
                    }
                    else
                    {
                        var family = ((string)m["details"].Get("family") ?? string.Empty).ToLowerInvariant();
                        info.Embedding = family.Contains("bert") || info.Id.IndexOf("embed", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    if (info.Id.Length > 0) list.Add(info);
                }
                return list;
            }
            foreach (var m in Translate.Arr(root["data"]))
            {
                var id = (string)m["id"];
                if (string.IsNullOrEmpty(id)) continue;
                // OpenAI lists image, audio and moderation models next to chat ones; they cannot answer /chat/completions.
                if (NonChat.IsMatch(id)) continue;
                var info = new ModelInfo { Id = id, Discovered = true };
                info.ContextWindow = (int?)m["context_length"] ?? (int?)m["context_window"] ?? (int?)m["max_context_length"];
                if (id.IndexOf("embed", StringComparison.OrdinalIgnoreCase) >= 0) info.Embedding = true;
                list.Add(info);
            }
            return list;
        }

        /// <summary>A TCP connect, nothing more: is anything listening at host:port? Used for the connectivity probes.</summary>
        public static async Task<bool> TcpReachableAsync(string host, int port, int timeoutMs, CancellationToken token)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    var connect = client.ConnectAsync(host, port);
                    var done = await Task.WhenAny(connect, Task.Delay(timeoutMs, token)).ConfigureAwait(false);
                    if (done != connect)
                    {
                        // Observe the abandoned connect so its exception is not unobserved.
                        var _ = connect.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        return false;
                    }
                    await connect.ConfigureAwait(false);
                    return client.Connected;
                }
                catch (SocketException) { return false; }
                catch (ObjectDisposedException) { return false; }
                catch (OperationCanceledException) { return false; }
            }
        }

        private static void ApplyAuth(HttpRequestMessage request, ProviderConfig p)
        {
            var key = SecretProtector.ApiKeyFor(p);
            if (p.Kind == ProviderKind.Anthropic)
            {
                if (!string.IsNullOrEmpty(key)) request.Headers.TryAddWithoutValidation("x-api-key", key);
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            }
            else if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            foreach (var h in p.Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var ra = response.Headers.RetryAfter;
            if (ra == null) return null;
            if (ra.Delta.HasValue) return ra.Delta;
            if (ra.Date.HasValue)
            {
                var d = ra.Date.Value - DateTimeOffset.UtcNow;
                return d > TimeSpan.Zero ? d : TimeSpan.Zero;
            }
            return null;
        }

        /// <summary>The human part of a provider error body (OpenAI, Anthropic and Ollama all differ).</summary>
        public static string ErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            try
            {
                var j = JToken.Parse(body);
                var err = j["error"];
                if (err != null && err.Type == JTokenType.String) return (string)err ?? string.Empty;
                var m = (string)err.Get("message") ?? (string)j["message"] ?? (string)j["detail"];
                if (!string.IsNullOrEmpty(m)) return m;
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
            var t = body.Trim();
            return t.Length > 300 ? t.Substring(0, 300) + "\u2026" : t;
        }

        private static string Describe(HttpRequestException ex)
        {
            var inner = ex.InnerException;
            while (inner != null && inner.InnerException != null && !(inner is SocketException)) inner = inner.InnerException;
            return inner != null ? inner.Message : ex.Message;
        }
    }
}
