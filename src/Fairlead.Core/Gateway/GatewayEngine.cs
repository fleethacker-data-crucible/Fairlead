using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Bearing;
using Fairlead.Core.Config;
using Fairlead.Core.Host;
using Fairlead.Core.Protocol;
using Fairlead.Core.Providers;
using Fairlead.Core.Resilience;
using Fairlead.Core.Routing;
using Fairlead.Core.Telemetry;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Gateway
{
    /// <summary>
    /// The model gateway: authenticates the calling tool, plans a route, tries targets in order (racing a second one
    /// when hedging is on), translates between dialects when the target speaks the other one, streams the answer
    /// back and records what happened. Failover happens only before the first byte reaches the caller; after that a
    /// failure ends the stream with an error event in the caller's dialect.
    /// </summary>
    public sealed class GatewayEngine
    {
        public const string RouteHeader = "X-Fairlead-Route";
        public const string PrivacyHeader = "X-Fairlead-Privacy";
        /// <summary>Optional free-text caller label (a tool or job name) kept in the request log next to the client key's name.</summary>
        public const string LabelHeader = "X-Fairlead-Label";
        private readonly ConfigStore _config;
        private readonly UpstreamClient _upstream;
        private readonly HealthMonitor _health;
        private readonly BreakerBoard _breakers;
        private readonly Metrics _metrics;
        private readonly Diagnostics _log;
        private readonly RoutePlanner _planner = new RoutePlanner();
        /// <summary>The multipart body of an audio-transcription request, by the placeholder body that travels through route planning.</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<JObject, RawUpload> _uploads = new System.Runtime.CompilerServices.ConditionalWeakTable<JObject, RawUpload>();

        public GatewayEngine(ConfigStore config, UpstreamClient upstream, HealthMonitor health, BreakerBoard breakers, Metrics metrics, Diagnostics log)
        {
            _config = config;
            _upstream = upstream;
            _health = health;
            _breakers = breakers;
            _metrics = metrics;
            _log = log;
        }

        public RoutePlanner Planner { get { return _planner; } }

        /// <summary>Cited answers; null or not enabled unless FAIRLEAD_BEARING_DIR names a folder. Only requests that send X-Fairlead-Bearing: cite use it.</summary>
        public BearingService CitedAnswers { get; set; }

        // ---------- client keys ----------

        public static string NewClientKey()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var sb = new StringBuilder("fl-");
            foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            return sb.ToString();
        }

        public static string HashKey(string key)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>The bearer or x-api-key value, whichever the tool sent.</summary>
        public static string PresentedKey(HttpRequest request)
        {
            var auth = request.Header("Authorization");
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth.Substring(7).Trim();
            var x = request.Header("x-api-key");
            return string.IsNullOrEmpty(x) ? null : x.Trim();
        }

        /// <summary>
        /// Finds the client for a request. Returns false (with the reason) when a key is required and missing or
        /// unknown. Remote (LAN) callers always need a valid key.
        /// </summary>
        public bool Authenticate(HttpRequest request, out ClientKey client, out string error)
        {
            var cfg = _config.Current;
            client = null;
            error = null;
            if (request.Internal) return true;
            var key = PresentedKey(request);
            if (!string.IsNullOrEmpty(key) && key.StartsWith("fl-", StringComparison.Ordinal))
            {
                var hash = HashKey(key);
                client = cfg.Clients.FirstOrDefault(c => FixedTimeEquals(c.Hash, hash));
            }
            if (client != null) return true;
            if (!request.IsLoopback) { error = "A Fairlead client key is required from another machine (Authorization: Bearer fl-...)."; return false; }
            if (!cfg.RequireClientKey) return true;
            error = string.IsNullOrEmpty(key)
                ? "Missing Fairlead client key. Create one in Fairlead > Clients and send it as Authorization: Bearer fl-... (or x-api-key)."
                : "Unknown Fairlead client key. Keys start with fl- and are created in Fairlead > Clients.";
            return false;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ---------- requests ----------

        /// <summary>An anthropic-beta header value fit to forward: feature names (letters, digits, - _ . , :) and spaces, at most 400 characters; null otherwise.</summary>
        public static string CleanBeta(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (v.Length > 400) return null;
            foreach (var ch in v)
            {
                var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.' || ch == ',' || ch == ':' || ch == ' ';
                if (!ok) return null;
            }
            return v;
        }

        private readonly ClientRateLimiter _limits = new ClientRateLimiter();

        /// <summary>The per-key request limit: null when the request may go on, else the 429 (in the caller's dialect, with Retry-After).</summary>
        private HttpResponse RateLimited(ClientKey client, Dialect dialect, RequestRecord record, System.Diagnostics.Stopwatch sw)
        {
            if (client == null || !client.RateLimitPerMinute.HasValue || client.RateLimitPerMinute.Value <= 0) return null;
            int retry;
            if (_limits.TryAcquire(client.Id, client.RateLimitPerMinute.Value, DateTime.UtcNow, out retry)) return null;
            var response = Fail(record, sw, dialect, 429, "Client key '" + client.Name + "' is limited to " + client.RateLimitPerMinute.Value + " requests a minute. Try again in " + retry + " s.");
            response.Headers["Retry-After"] = retry.ToString();
            return response;
        }

        /// <summary>Forgets a client key's request window (its limit changed or it was revoked).</summary>
        public void ForgetClient(string clientId)
        {
            _limits.Forget(clientId);
        }

        /// <summary>Handles POST /v1/chat/completions (OpenAI) and POST /v1/messages (Anthropic).</summary>
        public async Task<HttpResponse> HandleChatAsync(HttpRequest request, Dialect dialect)
        {
            var record = new RequestRecord { Id = request.RequestId, Dialect = dialect == Dialect.Anthropic ? "anthropic" : "openai", Endpoint = request.Path };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ClientKey client;
            string authError;
            record.Label = RequestLog.CleanLabel(request.Header(LabelHeader));
            if (!Authenticate(request, out client, out authError)) return Fail(record, sw, dialect, 401, authError);
            record.Client = (client == null ? null : client.Name);
            RequestLog.Identify(record, client, request.Header(LabelHeader));
            var limited = RateLimited(client, dialect, record, sw);
            if (limited != null) return limited;
            JObject body;
            try { body = JObject.Parse(request.BodyText); }
            catch (JsonException ex) { return Fail(record, sw, dialect, 400, "Body is not valid JSON: " + ex.Message); }
            if (!(body["messages"] is JArray)) return Fail(record, sw, dialect, 400, "'messages' must be an array.");

            var cfg = _config.Current;
            BearingRun bearing = null;
            if (BearingService.Requested(request.Header(BearingService.OptInHeader)))
            {
                if (CitedAnswers == null || !CitedAnswers.Enabled) return Fail(record, sw, dialect, 400, "Cited answers are not set up: set FAIRLEAD_BEARING_DIR to a folder of documents and restart Fairlead.");
                string bearingError;
                bearing = CitedAnswers.Prepare(body, dialect, BearingService.TopFrom(request.Header(BearingService.TopHeader)), out bearingError);
                if (bearing == null) return Fail(record, sw, dialect, 400, bearingError);
                if (bearing.NoSources) return BearingNoSources(record, sw, dialect, bearing, body);
            }
            var profile = RequestProfile.From(body, dialect, cfg.Privacy.DetectSecrets);
            profile.ClientName = (client == null ? null : client.Name);
            profile.RouteOverride = request.Header(RouteHeader);
            var beta = CleanBeta(request.Header("anthropic-beta"));
            if (beta != null) profile.PassHeaders["anthropic-beta"] = beta;
            // Excerpts of the user's own documents are in the prompt: they stay on this machine unless privacy routing is switched off.
            if (bearing != null) profile.MarkSensitive("documents");
            if (string.Equals(request.Header(PrivacyHeader), "local", StringComparison.OrdinalIgnoreCase)) profile.MarkSensitive("header");
            record.RequestedModel = profile.RequestedModel;
            record.Stream = profile.Stream;
            record.EstimatedTokens = profile.EstimatedTokens;
            record.Sensitive = profile.Sensitive;
            if (cfg.LogContent) record.PromptPreview = Preview(profile.LastUserText);

            var plan = _planner.Plan(cfg, profile, _health, _breakers, _metrics, client);
            record.Route = plan.Route;
            record.Decision = plan.Decision;
            record.Kedge = plan.Kedge;
            PublishStart(record, plan, client);
            if (plan.Error != null) return Fail(record, sw, dialect, plan.ErrorStatus, plan.Error);

            var outcome = await TryCandidatesAsync(plan, body, dialect, profile, record, sw, request.Aborted).ConfigureAwait(false);
            if (outcome.Error != null)
            {
                return Fail(record, sw, dialect, outcome.ErrorStatus, outcome.Error);
            }
            var winner = outcome.Winner;
            var response = outcome.Response;
            record.Target = winner.Ref;
            record.Local = winner.Local;
            record.Failover = record.Attempts.Count(a => a.Outcome != "skipped" && a.Outcome != "lost-race") > 1;
            // Answered from outside the route asked for (a local fallback): say so, so a client never labels
            // a local model's answer with the cloud model it named.
            if (!string.IsNullOrEmpty(winner.Via) && !string.Equals(winner.Via, plan.Route, StringComparison.OrdinalIgnoreCase)) record.FallbackVia = winner.Via;
            record.FirstByteMs = (int)response.HeadersAfter.TotalMilliseconds;
            if (!profile.Stream) PublishFirstByte(record, winner.Ref, (int)sw.ElapsedMilliseconds);
            if (!winner.Local && _health != null) _health.ReportCloudSuccess();

            if (profile.Stream) return StreamResponse(response, winner, dialect, profile, record, sw, bearing);
            return await BufferedResponse(response, winner, dialect, profile, record, sw, bearing).ConfigureAwait(false);
        }

        /// <summary>A cited-answer request nothing in the documents matched: answered here, no model called.</summary>
        private HttpResponse BearingNoSources(RequestRecord record, System.Diagnostics.Stopwatch sw, Dialect dialect, BearingRun bearing, JObject body)
        {
            record.Route = "bearing";
            record.Target = "bearing/none";
            record.Local = true;
            record.Status = 200;
            record.Decision.Add("Bearing: no passage in the documents matched the question; answered without calling a model.");
            record.TotalMs = (int)sw.ElapsedMilliseconds;
            _metrics.Record(record);
            var output = bearing.NoSourcesAnswer();
            var model = (string)body["model"] ?? "bearing";
            if (body["stream"] != null && body["stream"].Type == JTokenType.Boolean && (bool)body["stream"])
            {
                // A streaming caller gets a stream: the one sentence, the end of the answer and the (empty) citations event.
                var streamed = HttpResponse.EventStream(async (sink, token) =>
                {
                    var chunk = new JObject { { "id", "chatcmpl-bearing" }, { "object", "chat.completion.chunk" }, { "model", model } };
                    chunk["choices"] = new JArray { new JObject { { "index", 0 }, { "delta", new JObject { { "role", "assistant" }, { "content", BearingRun.NoSourcesText } } }, { "finish_reason", null } } };
                    var finish = new JObject { { "id", "chatcmpl-bearing" }, { "object", "chat.completion.chunk" }, { "model", model } };
                    finish["choices"] = new JArray { new JObject { { "index", 0 }, { "delta", new JObject() }, { "finish_reason", "stop" } } };
                    var payload = new JObject { { "fairlead_citations", output["fairlead_citations"].DeepClone() }, { "fairlead_bearing", output["fairlead_bearing"].DeepClone() } };
                    if (dialect == Dialect.Anthropic)
                    {
                        var o2a = new OpenAiToAnthropicStream(model);
                        foreach (var s in o2a.Feed(chunk)) await sink.WriteAsync(s, token).ConfigureAwait(false);
                        foreach (var s in o2a.Feed(finish)) await sink.WriteAsync(s, token).ConfigureAwait(false);
                        payload["type"] = "fairlead_citations";
                        await sink.WriteAsync(SseWriter.Named("fairlead_citations", payload), token).ConfigureAwait(false);
                        foreach (var s in o2a.Finish()) await sink.WriteAsync(s, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await sink.WriteAsync(SseWriter.Data(chunk), token).ConfigureAwait(false);
                        await sink.WriteAsync(SseWriter.Data(finish), token).ConfigureAwait(false);
                        payload["id"] = "chatcmpl-bearing";
                        payload["object"] = "chat.completion.chunk";
                        payload["model"] = model;
                        payload["choices"] = new JArray();
                        await sink.WriteAsync(SseWriter.Data(payload), token).ConfigureAwait(false);
                        await sink.WriteAsync(SseWriter.Done, token).ConfigureAwait(false);
                    }
                });
                streamed.Headers["X-Fairlead-Request-Id"] = record.Id;
                streamed.Headers["X-Fairlead-Route"] = "bearing";
                streamed.Headers["X-Fairlead-Bearing"] = "sources=0";
                return streamed;
            }
            var text = dialect == Dialect.Anthropic ? Translate.OpenAiToAnthropicResponse(output, model) : output;
            if (dialect == Dialect.Anthropic) { text["fairlead_citations"] = output["fairlead_citations"]; text["fairlead_bearing"] = output["fairlead_bearing"]; }
            var http = HttpResponse.Json(200, text.ToString(Formatting.None));
            http.Headers["X-Fairlead-Request-Id"] = record.Id;
            http.Headers["X-Fairlead-Route"] = "bearing";
            http.Headers["X-Fairlead-Bearing"] = "sources=0";
            return http;
        }

        /// <summary>POST /v1/embeddings: OpenAI dialect only, routed like chat.</summary>
        public async Task<HttpResponse> HandleEmbeddingsAsync(HttpRequest request)
        {
            var record = new RequestRecord { Id = request.RequestId, Dialect = "openai", Endpoint = request.Path };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ClientKey client;
            string authError;
            record.Label = RequestLog.CleanLabel(request.Header(LabelHeader));
            if (!Authenticate(request, out client, out authError)) return Fail(record, sw, Dialect.OpenAi, 401, authError);
            record.Client = (client == null ? null : client.Name);
            RequestLog.Identify(record, client, request.Header(LabelHeader));
            var limited = RateLimited(client, Dialect.OpenAi, record, sw);
            if (limited != null) return limited;
            JObject body;
            try { body = JObject.Parse(request.BodyText); }
            catch (JsonException ex) { return Fail(record, sw, Dialect.OpenAi, 400, "Body is not valid JSON: " + ex.Message); }
            var profile = new RequestProfile { Dialect = Dialect.OpenAi, RequestedModel = (string)body["model"] ?? string.Empty, ClientName = (client == null ? null : client.Name), RouteOverride = request.Header(RouteHeader) };
            if (string.Equals(request.Header(PrivacyHeader), "local", StringComparison.OrdinalIgnoreCase)) profile.MarkSensitive("header");
            record.RequestedModel = profile.RequestedModel;
            var plan = _planner.Plan(_config.Current, profile, _health, _breakers, _metrics, client, "embeddings");
            record.Route = plan.Route;
            record.Decision = plan.Decision;
            record.Kedge = plan.Kedge;
            PublishStart(record, plan, client);
            if (plan.Error != null) return Fail(record, sw, Dialect.OpenAi, plan.ErrorStatus, plan.Error);
            var outcome = await TryCandidatesAsync(plan, body, Dialect.OpenAi, profile, record, sw, request.Aborted, "embeddings").ConfigureAwait(false);
            if (outcome.Error != null) return Fail(record, sw, Dialect.OpenAi, outcome.ErrorStatus, outcome.Error);
            using (var response = outcome.Response)
            {
                var text = await response.ReadTextAsync().ConfigureAwait(false);
                record.Target = outcome.Winner.Ref;
                record.Local = outcome.Winner.Local;
                record.Status = 200;
                record.TotalMs = (int)sw.ElapsedMilliseconds;
                try { record.InputTokens = (int?)JObject.Parse(text)["usage"].At("prompt_tokens"); } catch (JsonException) { }
                _metrics.Record(record);
                var http = HttpResponse.Json(200, text);
                AddHeaders(http, record, outcome.Winner);
                return http;
            }
        }

        /// <summary>
        /// POST /v1/audio/speech (JSON in, audio out) and POST /v1/audio/transcriptions (multipart in, JSON or text out): the same
        /// keys, routes, privacy rules, failover and request log as chat. Only OpenAI-compatible providers can serve them.
        /// </summary>
        public async Task<HttpResponse> HandleAudioAsync(HttpRequest request, string operation)
        {
            var record = new RequestRecord { Id = request.RequestId, Dialect = "openai", Endpoint = request.Path };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ClientKey client;
            string authError;
            record.Label = RequestLog.CleanLabel(request.Header(LabelHeader));
            if (!Authenticate(request, out client, out authError)) return Fail(record, sw, Dialect.OpenAi, 401, authError);
            record.Client = (client == null ? null : client.Name);
            RequestLog.Identify(record, client, request.Header(LabelHeader));
            var cfg = _config.Current;
            JObject body;
            string spoken = null;
            if (operation == "speech")
            {
                try { body = JObject.Parse(request.BodyText); }
                catch (JsonException ex) { return Fail(record, sw, Dialect.OpenAi, 400, "Body is not valid JSON: " + ex.Message); }
                spoken = (string)body["input"];
                if (string.IsNullOrEmpty(spoken)) return Fail(record, sw, Dialect.OpenAi, 400, "'input' (the text to speak) is required.");
                record.EstimatedTokens = spoken.Length / 4;
            }
            else
            {
                string uploadError;
                var upload = RawUpload.Parse(request.Body, request.Header("Content-Type"), out uploadError);
                if (upload == null) return Fail(record, sw, Dialect.OpenAi, 400, uploadError);
                if (!upload.HasFile) return Fail(record, sw, Dialect.OpenAi, 400, "The audio is missing: send it as the 'file' field.");
                body = new JObject { { "model", upload.Field("model") ?? string.Empty } };
                _uploads.Add(body, upload);
            }
            var profile = new RequestProfile { Dialect = Dialect.OpenAi, RequestedModel = (string)body["model"] ?? string.Empty, ClientName = (client == null ? null : client.Name), RouteOverride = request.Header(RouteHeader) };
            if (string.Equals(request.Header(PrivacyHeader), "local", StringComparison.OrdinalIgnoreCase)) profile.MarkSensitive("header");
            if (spoken != null && cfg.Privacy.DetectSecrets) foreach (var reason in SecretScanner.Scan(spoken)) profile.MarkSensitive(reason);
            record.RequestedModel = profile.RequestedModel;
            record.Sensitive = profile.Sensitive;
            var plan = _planner.Plan(cfg, profile, _health, _breakers, _metrics, client, operation);
            record.Route = plan.Route;
            record.Decision = plan.Decision;
            record.Kedge = plan.Kedge;
            PublishStart(record, plan, client);
            if (plan.Error != null) return Fail(record, sw, Dialect.OpenAi, plan.ErrorStatus, plan.Error);
            var outcome = await TryCandidatesAsync(plan, body, Dialect.OpenAi, profile, record, sw, request.Aborted, operation).ConfigureAwait(false);
            if (outcome.Error != null) return Fail(record, sw, Dialect.OpenAi, outcome.ErrorStatus, outcome.Error);
            using (var response = outcome.Response)
            {
                byte[] bytes;
                try { bytes = await response.ReadBytesAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is OperationCanceledException || ex is System.Net.Http.HttpRequestException)) throw;
                    return Fail(record, sw, Dialect.OpenAi, 502, outcome.Winner.Ref + " dropped the connection while answering.");
                }
                record.Target = outcome.Winner.Ref;
                record.Local = outcome.Winner.Local;
                record.Failover = record.Attempts.Count(a => a.Outcome != "skipped" && a.Outcome != "lost-race") > 1;
                record.Status = 200;
                record.TotalMs = (int)sw.ElapsedMilliseconds;
                _metrics.Record(record);
                var http = HttpResponse.Bytes(200, bytes, response.ContentType ?? (operation == "speech" ? "audio/mpeg" : "application/json"));
                AddHeaders(http, record, outcome.Winner);
                return http;
            }
        }

        /// <summary>Dry run: what would happen to this request, without calling anything.</summary>
        public object Explain(JObject body, Dialect dialect, string routeHeader, bool privateHeader, string clientName)
        {
            var cfg = _config.Current;
            var profile = RequestProfile.From(body, dialect, cfg.Privacy.DetectSecrets);
            profile.RouteOverride = routeHeader;
            profile.ClientName = clientName;
            if (privateHeader) profile.MarkSensitive("header");
            var client = clientName == null ? null : cfg.Clients.FirstOrDefault(c => string.Equals(c.Name, clientName, StringComparison.OrdinalIgnoreCase));
            var plan = _planner.Plan(cfg, profile, _health, _breakers, _metrics, client);
            return new
            {
                route = plan.Route,
                decision = plan.Decision,
                error = plan.Error,
                kedge = plan.Kedge,
                localOnly = plan.LocalOnly,
                profile = new { profile.EstimatedTokens, profile.HasTools, profile.HasImages, profile.Sensitive, profile.SensitiveReasons, profile.Stream, profile.RequestedModel },
                candidates = plan.Candidates.Select(c => new { target = c.Ref, local = c.Local, parked = c.Parked, parkedBecause = c.ParkedBecause, dialect = UpstreamClient.DialectOf(c.Provider.Kind) == Dialect.Anthropic ? "anthropic" : "openai" }),
            };
        }

        // ---------- attempts ----------

        private sealed class Outcome
        {
            public Candidate Winner;
            public UpstreamResponse Response;
            public string Error;
            public int ErrorStatus = 502;
        }

        private async Task<Outcome> TryCandidatesAsync(RoutePlan plan, JObject body, Dialect dialect, RequestProfile profile, RequestRecord record, System.Diagnostics.Stopwatch sw, CancellationToken aborted, string operation = "chat")
        {
            UpstreamException last = null;
            var queue = new Queue<Candidate>(plan.Candidates);
            while (queue.Count > 0)
            {
                var first = queue.Dequeue();
                if (!Acquire(first, record)) { continue; }
                var cancels = new Dictionary<Task<UpstreamResponse>, CancellationTokenSource>();
                var retries = new Dictionary<Task<UpstreamResponse>, RetryCount>();
                var primaryCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                var primaryRetries = new RetryCount();
                PublishAttemptBegin(record, first, false);
                Task<UpstreamResponse> primary = SendAsync(first, body, dialect, operation, primaryCts.Token, primaryRetries, profile.PassHeaders);
                cancels[primary] = primaryCts;
                retries[primary] = primaryRetries;
                var primaryStarted = sw.ElapsedMilliseconds;
                Candidate second = null;
                Task<UpstreamResponse> backup = null;
                long backupStarted = 0;
                if (plan.HedgeAfterMs > 0 && queue.Count > 0)
                {
                    var delay = Task.Delay(plan.HedgeAfterMs, aborted);
                    var done = await Task.WhenAny(primary, delay).ConfigureAwait(false);
                    if (done == delay && !primary.IsCompleted)
                    {
                        while (queue.Count > 0 && second == null)
                        {
                            var next = queue.Dequeue();
                            if (Acquire(next, record)) second = next;
                        }
                        if (second != null)
                        {
                            record.Decision.Add("Hedge: " + first.Ref + " slow after " + plan.HedgeAfterMs + " ms; racing " + second.Ref + ".");
                            var backupCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                            var backupRetries = new RetryCount();
                            PublishAttemptBegin(record, second, true);
                            backup = SendAsync(second, body, dialect, operation, backupCts.Token, backupRetries, profile.PassHeaders);
                            cancels[backup] = backupCts;
                            retries[backup] = backupRetries;
                            backupStarted = sw.ElapsedMilliseconds;
                        }
                    }
                }
                var racing = new List<KeyValuePair<Candidate, Task<UpstreamResponse>>> { new KeyValuePair<Candidate, Task<UpstreamResponse>>(first, primary) };
                if (backup != null) racing.Add(new KeyValuePair<Candidate, Task<UpstreamResponse>>(second, backup));
                while (racing.Count > 0)
                {
                    var finished = await Task.WhenAny(racing.Select(r => r.Value)).ConfigureAwait(false);
                    var entry = racing.First(r => r.Value == finished);
                    racing.Remove(entry);
                    var started = entry.Key == first ? primaryStarted : backupStarted;
                    try
                    {
                        var response = await finished.ConfigureAwait(false);
                        Breaker(entry.Key).Success();
                        _breakers.For(entry.Key.Provider.Id).Success();
                        AddAttempt(record, new AttemptRecord { Target = entry.Key.Ref, Outcome = "ok", Status = response.Status, Ms = (int)(sw.ElapsedMilliseconds - started), Retries = retries[finished].Value });
                        foreach (var loser in racing)
                        {
                            AddAttempt(record, new AttemptRecord { Target = loser.Key.Ref, Outcome = "lost-race", Ms = (int)(sw.ElapsedMilliseconds - (loser.Key == first ? primaryStarted : backupStarted)) });
                            DisposeWhenDone(loser.Value, cancels[loser.Value]);
                        }
                        return new Outcome { Winner = entry.Key, Response = response };
                    }
                    catch (UpstreamException ex)
                    {
                        last = ex;
                        AddAttempt(record, new AttemptRecord { Target = entry.Key.Ref, Outcome = ex.Kind.ToString().ToLowerInvariant(), Status = ex.Status, Ms = (int)(sw.ElapsedMilliseconds - started), Error = ex.Message, Retries = retries[finished].Value });
                        OnFailure(entry.Key, ex);
                        if (ex.Kind == FailureKind.BadRequest)
                        {
                            foreach (var loser in racing) DisposeWhenDone(loser.Value, cancels[loser.Value]);
                            return new Outcome { Error = ex.Message, ErrorStatus = ex.Status ?? 400 };
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        AddAttempt(record, new AttemptRecord { Target = entry.Key.Ref, Outcome = "cancelled", Ms = (int)(sw.ElapsedMilliseconds - started) });
                        foreach (var loser in racing) DisposeWhenDone(loser.Value, cancels[loser.Value]);
                        return new Outcome { Error = "The request was cancelled.", ErrorStatus = 499 };
                    }
                }
            }
            if (last == null) return new Outcome { Error = "Every target for route '" + plan.Route + "' is unavailable right now.", ErrorStatus = 503 };
            var status = last.Kind == FailureKind.RateLimited ? 429 : last.Kind == FailureKind.Auth ? 502 : last.Kind == FailureKind.Timeout ? 504 : last.Kind == FailureKind.ModelNotFound ? 404 : (last.Kind == FailureKind.Server && (last.Status == 503 || last.Status == 529)) ? 503 : 502;
            var tried = record.Attempts.Count(a => a.Outcome != "skipped");
            return new Outcome { Error = (tried > 1 ? "All " + tried + " targets failed. Last: " : string.Empty) + last.Message, ErrorStatus = status };
        }

        // ---------- live view (/api/live) ----------

        private void PublishStart(RequestRecord record, RoutePlan plan, ClientKey client)
        {
            _metrics.Live.Publish("start", () => Json.Serialize(new
            {
                id = record.Id,
                time = record.Time,
                client = record.Client,
                clientId = client == null ? null : client.Id,
                endpoint = record.Endpoint,
                requestedModel = record.RequestedModel,
                requestedRoute = plan.RequestedRoute,
                route = string.IsNullOrEmpty(plan.Route) ? null : plan.Route,
                kedge = plan.Kedge,
                localOnly = plan.LocalOnly,
                sensitive = record.Sensitive,
                stream = record.Stream,
                decision = plan.Decision.ToList(),
                candidates = plan.Candidates.Select(c => new { target = c.Ref, local = c.Local, parked = c.Parked, via = c.Via }).ToList(),
                error = plan.Error,
            }));
        }

        private void PublishAttemptBegin(RequestRecord record, Candidate c, bool hedge)
        {
            _metrics.Live.Publish("attempt", () => Json.Serialize(new { id = record.Id, target = c.Ref, local = c.Local, via = c.Via, phase = "begin", hedge = hedge }));
        }

        private void PublishFirstByte(RequestRecord record, string target, int ms)
        {
            _metrics.Live.Publish("firstByte", () => Json.Serialize(new { id = record.Id, target = target, ms = ms }));
        }

        private void AddAttempt(RequestRecord record, AttemptRecord a)
        {
            record.Attempts.Add(a);
            _metrics.Live.Publish("attempt", () => Json.Serialize(new { id = record.Id, target = a.Target, phase = "end", outcome = a.Outcome, status = a.Status, ms = a.Ms, error = a.Error, retries = a.Retries }));
        }

        private bool Acquire(Candidate c, RequestRecord record)
        {
            var now = DateTimeOffset.UtcNow;
            var pb = _breakers.For(c.Provider.Id);
            var mb = Breaker(c);
            // Parked candidates are last resorts: try them even with an open breaker rather than fail outright.
            if (!c.Parked && (!pb.TryAcquire(now) || !mb.TryAcquire(now)))
            {
                AddAttempt(record, new AttemptRecord { Target = c.Ref, Outcome = "skipped", Error = "circuit open" });
                return false;
            }
            return true;
        }

        private CircuitBreaker Breaker(Candidate c)
        {
            return _breakers.For(c.Ref);
        }

        private void OnFailure(Candidate c, UpstreamException ex)
        {
            if (ex.Kind == FailureKind.ModelNotFound) Breaker(c).Failure(ex.Kind, ex.Message, ex.RetryAfter);
            else _breakers.For(c.Provider.Id).Failure(ex.Kind, ex.Message, ex.RetryAfter);
            _log.Warn("UPSTREAM_FAILED", c.Ref + ": " + ex.Message);
            if (!c.Local && (ex.Kind == FailureKind.Network || ex.Kind == FailureKind.Timeout) && _health != null) _health.Nudge();
        }

        /// <summary>Stops a losing or abandoned attempt now and releases its connection when it settles.</summary>
        private static void DisposeWhenDone(Task<UpstreamResponse> task, CancellationTokenSource cts)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            task.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) { t.Result.Cancel(); t.Result.Dispose(); }
                else { var ignored = t.Exception; }
            }, TaskScheduler.Default);
        }

        private sealed class RetryCount
        {
            public int Value;
        }

        private static readonly Random Jitter = new Random();

        /// <summary>
        /// Exponential backoff with jitter: the window doubles from <paramref name="baseMs"/> up to <paramref name="maxMs"/>,
        /// and the wait is a random point in its upper half (<paramref name="random01"/> in [0, 1)).
        /// </summary>
        public static int BackoffMs(int attempt, int baseMs, int maxMs, double random01)
        {
            var window = Math.Min((double)maxMs, baseMs * Math.Pow(2, Math.Max(0, attempt)));
            return (int)(window / 2 + random01 * window / 2);
        }

        /// <summary>Failures worth another try on the same target: a 5xx, 429, a timeout or a dropped connection. Never 401/403, 400 or 404.</summary>
        public static bool IsTransient(FailureKind kind)
        {
            return kind == FailureKind.Server || kind == FailureKind.RateLimited || kind == FailureKind.Timeout || kind == FailureKind.Network;
        }

        /// <summary>Sends to one target, retrying transient failures as many times as the provider allows before giving up on it.</summary>
        private async Task<UpstreamResponse> SendWithRetriesAsync(Candidate c, string operation, JObject outgoing, CancellationToken aborted, RetryCount count, RawUpload upload = null, IDictionary<string, string> pass = null)
        {
            var p = c.Provider;
            var max = Math.Max(0, p.Retries ?? 0);
            var baseMs = Math.Max(50, p.RetryBaseMs ?? 500);
            var maxMs = Math.Max(baseMs, p.RetryMaxMs ?? 8000);
            for (var attempt = 0; ; attempt++)
            {
                int delay;
                try
                {
                    return await _upstream.SendAsync(p, operation, outgoing, aborted, pass, upload).ConfigureAwait(false);
                }
                catch (UpstreamException ex)
                {
                    if (attempt >= max || !IsTransient(ex.Kind)) throw;
                    double r;
                    lock (Jitter) r = Jitter.NextDouble();
                    delay = BackoffMs(attempt, baseMs, maxMs, r);
                    if (ex.RetryAfter.HasValue)
                    {
                        // A Retry-After longer than the backoff ceiling means "not soon": fail over instead of waiting.
                        if (ex.RetryAfter.Value.TotalMilliseconds > maxMs) throw;
                        delay = Math.Max(delay, (int)ex.RetryAfter.Value.TotalMilliseconds);
                    }
                    count.Value++;
                    _log.Warn("UPSTREAM_RETRY", c.Ref + ": " + ex.Kind.ToString().ToLowerInvariant() + "; try " + (attempt + 2) + " of " + (max + 1) + " in " + delay + " ms.");
                }
                await Task.Delay(delay, aborted).ConfigureAwait(false);
            }
        }

        private Task<UpstreamResponse> SendAsync(Candidate c, JObject body, Dialect dialect, string operation, CancellationToken aborted, RetryCount count, IDictionary<string, string> pass = null)
        {
            var target = UpstreamClient.DialectOf(c.Provider.Kind);
            JObject outgoing;
            if (operation == "transcriptions")
            {
                RawUpload original;
                if (!_uploads.TryGetValue(body, out original)) throw new InvalidOperationException("No upload for this transcription request.");
                return SendWithRetriesAsync(c, operation, new JObject(), aborted, count, original.WithField("model", c.Model));
            }
            if (operation == "embeddings" || operation == "speech")
            {
                outgoing = (JObject)body.DeepClone();
                outgoing["model"] = c.Model;
            }
            else if (target == dialect)
            {
                outgoing = (JObject)body.DeepClone();
                outgoing["model"] = c.Model;
                if (target == Dialect.OpenAi && outgoing.Value<bool?>("stream") == true && c.Provider.StreamUsage && outgoing["stream_options"] == null)
                    outgoing["stream_options"] = new JObject { { "include_usage", true } };
                if (target == Dialect.OpenAi && string.Equals(c.Provider.Preset, "openai", StringComparison.OrdinalIgnoreCase))
                    Translate.AdaptForOpenAi(outgoing, c.Model);
            }
            else if (dialect == Dialect.Anthropic)
            {
                outgoing = Translate.AnthropicToOpenAiRequest(body, c.Model, string.Equals(c.Provider.Preset, "openai", StringComparison.OrdinalIgnoreCase), c.Provider.StreamUsage);
            }
            else
            {
                outgoing = Translate.OpenAiToAnthropicRequest(body, c.Model);
            }
            // anthropic-beta means something to Anthropic only; any other provider never sees it.
            return SendWithRetriesAsync(c, operation, outgoing, aborted, count, null, c.Provider.Kind == ProviderKind.Anthropic && pass != null && pass.Count > 0 ? pass : null);
        }

        // ---------- responses ----------

        private async Task<HttpResponse> BufferedResponse(UpstreamResponse response, Candidate winner, Dialect dialect, RequestProfile profile, RequestRecord record, System.Diagnostics.Stopwatch sw, BearingRun bearing = null)
        {
            using (response)
            {
                string text;
                try { text = await response.ReadTextAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is OperationCanceledException || ex is System.Net.Http.HttpRequestException)) throw;
                    if (response.CapReached) return Fail(record, sw, dialect, 504, winner.Ref + " did not finish within the " + _upstream.TotalCapSeconds + " s total limit (FAIRLEAD_MAX_REQUEST_SECONDS).");
                    return Fail(record, sw, dialect, 502, winner.Ref + " dropped the connection while answering.");
                }
                var target = UpstreamClient.DialectOf(winner.Provider.Kind);
                JObject parsed;
                try { parsed = JObject.Parse(text); }
                catch (JsonException) { return Fail(record, sw, dialect, 502, winner.Ref + " returned a body that is not JSON."); }
                var usage = target == Dialect.Anthropic ? TokenUsage.FromAnthropic(parsed["usage"]) : TokenUsage.FromOpenAi(parsed["usage"]);
                JObject output;
                if (target == dialect) output = parsed;
                else if (dialect == Dialect.Anthropic) output = Translate.OpenAiToAnthropicResponse(parsed, profile.RequestedModel.Length > 0 ? profile.RequestedModel : winner.Ref);
                else output = Translate.AnthropicToOpenAiResponse(parsed, profile.RequestedModel.Length > 0 ? profile.RequestedModel : winner.Ref);
                if (bearing != null) bearing.Apply(output, dialect);
                Finish(record, sw, 200, usage, winner);
                if (_config.Current.LogContent)
                    record.ResponsePreview = Preview(target == Dialect.Anthropic ? Translate.BlocksText(parsed["content"]) : Translate.ContentText(parsed["choices"].At(0).At("message").At("content")));
                _metrics.Record(record);
                var http = HttpResponse.Json(200, output.ToString(Formatting.None));
                AddHeaders(http, record, winner);
                if (bearing != null) http.Headers["X-Fairlead-Bearing"] = "sources=" + bearing.Hits.Count + ";cited=" + bearing.Citations.Count;
                return http;
            }
        }

        private HttpResponse StreamResponse(UpstreamResponse response, Candidate winner, Dialect dialect, RequestProfile profile, RequestRecord record, System.Diagnostics.Stopwatch sw, BearingRun bearing = null)
        {
            var target = UpstreamClient.DialectOf(winner.Provider.Kind);
            var modelName = profile.RequestedModel.Length > 0 ? profile.RequestedModel : winner.Ref;
            var http = HttpResponse.EventStream(async (output, token) =>
            {
                var usage = new TokenUsage();
                var text = new StringBuilder();
                string error = null;
                var o2a = target == Dialect.OpenAi && dialect == Dialect.Anthropic ? new OpenAiToAnthropicStream(modelName) : null;
                var a2o = target == Dialect.Anthropic && dialect == Dialect.OpenAi ? new AnthropicToOpenAiStream(modelName, true) : null;
                var sawDone = false;
                var firstEvent = false;
                var answer = new StringBuilder();
                var cited = false;
                // Cited answers, streamed: the text goes through as it comes, and once it is complete the citations follow as
                // one last event (a chunk with no choices for an OpenAI client, a named event for an Anthropic client).
                Func<Task> citeNow = async () =>
                {
                    if (bearing == null || cited) return;
                    cited = true;
                    bearing.Check(answer.ToString());
                    var payload = new JObject { { "fairlead_citations", bearing.CitationsJson }, { "fairlead_bearing", bearing.BearingJson } };
                    if (dialect == Dialect.Anthropic)
                    {
                        payload["type"] = "fairlead_citations";
                        await output.WriteAsync(SseWriter.Named("fairlead_citations", payload), token).ConfigureAwait(false);
                    }
                    else
                    {
                        payload["id"] = "chatcmpl-bearing";
                        payload["object"] = "chat.completion.chunk";
                        payload["model"] = modelName;
                        payload["choices"] = new JArray();
                        await output.WriteAsync(SseWriter.Data(payload), token).ConfigureAwait(false);
                    }
                };
                try
                {
                    using (response)
                    using (var stream = await response.OpenStreamAsync().ConfigureAwait(false))
                    using (var reader = new SseReader(stream) { EmitComments = true })
                    {
                        while (true)
                        {
                            response.Touch();
                            SseEvent ev;
                            try { ev = await reader.NextAsync(response.Token).ConfigureAwait(false); }
                            catch (Exception ex)
                            {
                                if (!(ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)) throw;
                                // The idle timer is Fairlead's own: say so, instead of blaming the upstream for a limit Fairlead applied.
                                error = token.IsCancellationRequested ? "Fairlead is stopping."
                                    : response.CapReached ? winner.Ref + " was still answering after the " + _upstream.TotalCapSeconds + " s total limit (FAIRLEAD_MAX_REQUEST_SECONDS), so Fairlead ended the stream."
                                    : response.Token.IsCancellationRequested ? winner.Ref + " sent nothing for " + winner.Provider.TimeoutSeconds + " s, so Fairlead ended the stream (the provider's timeout is an idle limit; keep-alives and data reset it)."
                                    : winner.Ref + " stopped sending (" + ex.GetType().Name + ").";
                                break;
                            }
                            if (ev == null) break;
                            if (ev.IsComment)
                            {
                                // Pass the upstream's keep-alive on, so the caller's own timeout does not fire while the model is still working.
                                await output.WriteAsync(SseWriter.KeepAlive, token).ConfigureAwait(false);
                                continue;
                            }
                            if (bearing != null && !ev.IsDone)
                            {
                                var piece = ev.Json();
                                if (piece != null)
                                {
                                    if (target == Dialect.OpenAi) { var d = piece["choices"] == null ? null : (string)piece["choices"].FirstOrDefault().At("delta").At("content"); if (d != null) answer.Append(d); }
                                    else if ((string)piece["type"] == "content_block_delta") { var d = (string)piece["delta"].At("text"); if (d != null) answer.Append(d); }
                                    if (target == Dialect.Anthropic && (string)piece["type"] == "message_stop") await citeNow().ConfigureAwait(false);
                                }
                            }
                            if (ev.IsDone)
                            {
                                sawDone = true;
                                await citeNow().ConfigureAwait(false);
                                if (o2a != null) { foreach (var s in o2a.Finish()) await output.WriteAsync(s, token).ConfigureAwait(false); }
                                else if (a2o == null) await output.WriteAsync(SseWriter.Done, token).ConfigureAwait(false);
                                break;
                            }
                            var json = ev.Json();
                            if (!firstEvent) { firstEvent = true; record.FirstByteMs = (int)sw.ElapsedMilliseconds; PublishFirstByte(record, winner.Ref, record.FirstByteMs.Value); }
                            if (o2a != null)
                            {
                                if (json == null) continue;
                                if (json["error"] != null) { error = UpstreamClient.ErrorMessage(ev.Data); break; }
                                foreach (var s in o2a.Feed(json)) await output.WriteAsync(s, token).ConfigureAwait(false);
                            }
                            else if (a2o != null)
                            {
                                if (json == null) continue;
                                foreach (var s in a2o.Feed(ev.Name, json)) await output.WriteAsync(s, token).ConfigureAwait(false);
                                if (a2o.Error != null) { error = a2o.Error; sawDone = true; break; }
                                if ((string)json["type"] == "message_stop") { sawDone = true; break; }
                            }
                            else
                            {
                                // Same dialect: relay the event as it came, and read usage on the way through.
                                await output.WriteAsync(ev.ToWire(), token).ConfigureAwait(false);
                                if (json != null) Observe(target, json, usage, text);
                                if (target == Dialect.Anthropic && json != null && (string)json["type"] == "message_stop") { sawDone = true; break; }
                                if (target == Dialect.Anthropic && json != null && (string)json["type"] == "error") { error = (string)json["error"].At("message") ?? "Upstream error"; sawDone = true; break; }
                            }
                        }
                        if (!sawDone && error == null)
                        {
                            // The upstream closed without its terminator: finish the caller's stream cleanly.
                            await citeNow().ConfigureAwait(false);
                            if (o2a != null) foreach (var s in o2a.Finish()) await output.WriteAsync(s, token).ConfigureAwait(false);
                            else if (a2o != null) foreach (var s in a2o.Finish()) await output.WriteAsync(s, token).ConfigureAwait(false);
                            else if (target == Dialect.OpenAi) await output.WriteAsync(SseWriter.Done, token).ConfigureAwait(false);
                        }
                        if (error != null) await WriteStreamError(output, dialect, error, o2a, a2o, token).ConfigureAwait(false);
                    }
                }
                catch (IOException)
                {
                    // The tool hung up: stop the upstream generation too (disposing the response cancels it).
                    error = error ?? "client disconnected";
                }
                catch (ObjectDisposedException)
                {
                    error = error ?? "client disconnected";
                }
                finally
                {
                    if (o2a != null) { usage.Merge(o2a.Usage); text.Append(o2a.Text); }
                    if (a2o != null) { usage.Merge(a2o.Usage); text.Append(a2o.Text); }
                    record.Error = error;
                    Finish(record, sw, error == null || error == "client disconnected" ? 200 : 502, usage, winner);
                    if (_config.Current.LogContent) record.ResponsePreview = Preview(text.ToString());
                    _metrics.Record(record);
                    if (error != null && error != "client disconnected") _log.Warn("STREAM_FAILED", winner.Ref + ": " + error, null, record.Id);
                }
            });
            AddHeaders(http, record, winner);
            if (bearing != null) http.Headers["X-Fairlead-Bearing"] = "sources=" + bearing.Hits.Count + ";citations=at-end";
            return http;
        }

        private static async Task WriteStreamError(IResponseStream output, Dialect dialect, string error, OpenAiToAnthropicStream o2a, AnthropicToOpenAiStream a2o, CancellationToken token)
        {
            if (dialect == Dialect.Anthropic)
            {
                await output.WriteAsync(SseWriter.Named("error", Translate.Error(Dialect.Anthropic, 502, error)), token).ConfigureAwait(false);
            }
            else if (a2o == null || a2o.Error == null)
            {
                await output.WriteAsync(SseWriter.Data(Translate.Error(Dialect.OpenAi, 502, error)), token).ConfigureAwait(false);
                await output.WriteAsync(SseWriter.Done, token).ConfigureAwait(false);
            }
        }

        private static void Observe(Dialect target, JObject json, TokenUsage usage, StringBuilder text)
        {
            if (target == Dialect.OpenAi)
            {
                if (json["usage"] != null && json["usage"].Type == JTokenType.Object) usage.Merge(TokenUsage.FromOpenAi(json["usage"]));
                var choices = json["choices"];
                var delta = choices == null ? null : (string)choices.FirstOrDefault().At("delta").At("content");
                if (delta != null && text.Length < 4000) text.Append(delta);
                return;
            }
            var type = (string)json["type"];
            if (type == "message_start") usage.Merge(TokenUsage.FromAnthropic(json["message"].At("usage")));
            else if (type == "message_delta") usage.Merge(TokenUsage.FromAnthropic(json["usage"]));
            else if (type == "content_block_delta" && text.Length < 4000) text.Append((string)json["delta"].At("text"));
        }

        private void Finish(RequestRecord record, System.Diagnostics.Stopwatch sw, int status, TokenUsage usage, Candidate winner)
        {
            record.Status = status;
            record.TotalMs = (int)sw.ElapsedMilliseconds;
            record.InputTokens = usage.Input;
            record.OutputTokens = usage.Output;
            var info = winner.Info ?? winner.Provider.FindModel(winner.Model);
            if (info != null && (info.InputPricePerMTok.HasValue || info.OutputPricePerMTok.HasValue) && usage.Known)
            {
                record.Cost = ((usage.Input ?? 0) * (info.InputPricePerMTok ?? 0m) + (usage.Output ?? 0) * (info.OutputPricePerMTok ?? 0m)) / 1000000m;
            }
        }

        private static void AddHeaders(HttpResponse http, RequestRecord record, Candidate winner)
        {
            http.Headers["X-Fairlead-Route"] = record.Route ?? string.Empty;
            http.Headers["X-Fairlead-Target"] = winner.Ref;
            http.Headers["X-Fairlead-Mode"] = record.Kedge ? "kedge" : "normal";
            http.Headers["X-Fairlead-Attempts"] = record.Attempts.Count(a => a.Outcome != "skipped").ToString();
            http.Headers["X-Fairlead-Request-Id"] = record.Id;
            if (record.Sensitive) http.Headers["X-Fairlead-Private"] = "1";
            if (record.FallbackVia != null)
            {
                http.Headers["X-Fairlead-Fallback"] = record.FallbackVia;
                var why = record.Decision.LastOrDefault(d => d.Contains("falling back") || d.Contains("found on a local server"));
                if (why != null) http.Headers["X-Fairlead-Fallback-Reason"] = HeaderSafe(why);
            }
        }

        /// <summary>Printable ASCII only, as an HTTP header value must be.</summary>
        private static string HeaderSafe(string s)
        {
            var sb = new System.Text.StringBuilder(Math.Min(s.Length, 300));
            foreach (var ch in s.Length > 300 ? s.Substring(0, 300) : s) sb.Append(ch >= 0x20 && ch < 0x7F ? ch : '?');
            return sb.ToString();
        }

        private HttpResponse Fail(RequestRecord record, System.Diagnostics.Stopwatch sw, Dialect dialect, int status, string message)
        {
            record.Status = status;
            record.Error = message;
            record.TotalMs = (int)sw.ElapsedMilliseconds;
            _metrics.Record(record);
            var http = HttpResponse.Json(status == 499 ? 400 : status, Translate.Error(dialect, status, message).ToString(Formatting.None));
            http.Headers["X-Fairlead-Request-Id"] = record.Id;
            if (record.Route != null) http.Headers["X-Fairlead-Route"] = record.Route;
            return http;
        }

        private static string Preview(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var t = Redactor.Redact(text.Trim());
            return t.Length > 2000 ? t.Substring(0, 2000) + "\u2026" : t;
        }
    }
}
