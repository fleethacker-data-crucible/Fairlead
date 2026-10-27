using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Gateway;
using Fairlead.Core.Host;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>Real loopback sockets, real HTTP, fake providers: the gateway end to end.</summary>
    public sealed class GatewayTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-tests-" + Guid.NewGuid().ToString("N"));
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly HttpClient _http = new HttpClient();

        private FairleadHost Start(FakeUpstream fake, Action<FairleadConfig>? edit, out string key)
        {
            var cfg = RoutingTests.Config();
            // Point the cloud providers at hosts the fake knows.
            cfg.FindProvider("anthropic")!.BaseUrl = "https://anthropic.test";
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            cfg.FindProvider("ollama")!.BaseUrl = "http://ollama.test:11434";
            key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = key.Substring(0, 7), Hash = GatewayEngine.HashKey(key) });
            edit?.Invoke(cfg);
            var host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") },
                Config = cfg,
                UpstreamHandler = fake,
                Monitor = false,
                PreferredPort = 0,
            });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private async Task<HttpResponseMessage> Post(FairleadHost host, string path, JObject body, string? key, Dictionary<string, string>? headers = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + path) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            if (key != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (headers != null) foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return await _http.SendAsync(req);
        }

        private static JObject Chat(string model, string prompt, bool stream = false)
        {
            return new JObject { ["model"] = model, ["stream"] = stream, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } } };
        }

        [Fact]
        public async Task RequiresAClientKey()
        {
            var host = Start(new FakeUpstream(), null, out _);
            var r = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), null);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            var bad = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), "fl-wrongwrongwrongwrongwrong");
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        [Fact]
        public async Task FailsOverToTheNextTargetBeforeAnyByteIsSent()
        {
            var fake = new FakeUpstream()
                .On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.ServiceUnavailable, new JObject { ["error"] = new JObject { ["message"] = "overloaded" } }))
                .On("ollama.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("from local")));
            var host = Start(fake, null, out var key);
            var r = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), key);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("ollama/tiny", r.Headers.GetValues("X-Fairlead-Target").Single());
            Assert.Equal("2", r.Headers.GetValues("X-Fairlead-Attempts").Single());
            var body = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal("from local", (string?)body["choices"]![0]!["message"]!["content"]);
            Assert.Equal("tiny", (string?)fake.Seen.Last().Value["model"]);
            var rec = host.Metrics.Recent(1).Single();
            Assert.True(rec.Failover);
            Assert.Equal("server", rec.Attempts[0].Outcome);
        }

        [Fact]
        public async Task OfflineCloudFallsBackToLocalAndNudgesTheMonitor()
        {
            // No handler for groq.test: the fake throws like a DNS failure.
            var fake = new FakeUpstream().On("ollama.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("still here")));
            var host = Start(fake, null, out var key);
            var r = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), key);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("network", host.Metrics.Recent(1).Single().Attempts[0].Outcome);
            // The provider breaker is open now: the next call goes straight to local.
            var again = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), key);
            Assert.Equal("1", again.Headers.GetValues("X-Fairlead-Attempts").Single());
        }

        [Fact]
        public async Task AnthropicCallerIsServedByAnOpenAiModelWithStreaming()
        {
            var fake = new FakeUpstream().On("ollama.test", (r, b) => FakeUpstream.Sse(FakeUpstream.OpenAiChunk("Hel"), FakeUpstream.OpenAiChunk("lo"), FakeUpstream.OpenAiFinish(), FakeUpstream.OpenAiUsage(5, 2), "data: [DONE]\n\n"));
            var host = Start(fake, c => c.Kedge = KedgeMode.On, out var key);
            var body = new JObject { ["model"] = "claude-sonnet-5-5", ["max_tokens"] = 100, ["stream"] = true, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } } };
            var r = await Post(host, "/v1/messages", body, null, new Dictionary<string, string> { { "x-api-key", key } });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("kedge", r.Headers.GetValues("X-Fairlead-Mode").Single());
            var text = await r.Content.ReadAsStringAsync();
            Assert.Contains("event: message_start", text);
            Assert.Contains("\"text\":\"Hel\"", text);
            Assert.Contains("\"text\":\"lo\"", text);
            Assert.Contains("event: message_stop", text);
            // The upstream got an OpenAI request asking for usage, for the local model.
            var sent = fake.Seen.Single().Value;
            Assert.Equal("qwen", (string?)sent["model"]);
            Assert.True((bool)sent["stream_options"]!["include_usage"]!);
            await Task.Delay(100);
            var rec = host.Metrics.Recent(1).Single();
            Assert.Equal(5, rec.InputTokens);
            Assert.Equal(2, rec.OutputTokens);
        }

        [Fact]
        public async Task OpenAiCallerIsServedByAnthropicNonStreaming()
        {
            var fake = new FakeUpstream().On("anthropic.test", (r, b) =>
            {
                Assert.Equal("2023-06-01", r.Headers.GetValues("anthropic-version").Single());
                return FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.AnthropicAnswer("bonjour"));
            });
            var host = Start(fake, null, out var key);
            var r = await Post(host, "/v1/chat/completions", Chat("anthropic/claude-big", "salut"), key);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal("bonjour", (string?)body["choices"]![0]!["message"]!["content"]);
            Assert.Equal(4096, (int)fake.Seen.Single().Value["max_tokens"]!);
            var rec = host.Metrics.Recent(1).Single();
            Assert.Equal(9, rec.InputTokens);
            Assert.NotNull(rec.Cost);
        }

        [Fact]
        public async Task HedgingRacesASlowTargetAndTakesTheFirstAnswer()
        {
            var fake = new FakeUpstream()
                .On("groq.test", async (r, b) => { await Task.Delay(3000); return FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("slow")); })
                .On("ollama.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("quick")));
            var host = Start(fake, c => { c.FindRoute("fast")!.HedgeAfterMs = 200; c.FindRoute("fast")!.Strategy = RouteStrategy.Failover; }, out var key);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), key);
            Assert.True(sw.ElapsedMilliseconds < 2500, "took " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal("ollama/tiny", r.Headers.GetValues("X-Fairlead-Target").Single());
            Assert.Contains(host.Metrics.Recent(1).Single().Attempts, a => a.Outcome == "lost-race");
        }

        [Fact]
        public async Task BadRequestIsNotRetriedElsewhere()
        {
            var fake = new FakeUpstream()
                .On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.BadRequest, new JObject { ["error"] = new JObject { ["message"] = "messages: invalid" } }))
                .On("ollama.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("should not be called")));
            var host = Start(fake, null, out var key);
            var r = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), key);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Single(fake.Seen);
        }

        [Fact]
        public async Task ModelsListsRoutesThenTargets()
        {
            var host = Start(new FakeUpstream(), null, out var key);
            var req = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/v1/models");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            var r = await _http.SendAsync(req);
            var ids = JObject.Parse(await r.Content.ReadAsStringAsync())["data"]!.Select(d => (string?)d["id"]).ToList();
            Assert.Equal("auto", ids[0]);
            Assert.Contains("ollama/qwen", ids);
            Assert.Contains("anthropic/claude-big", ids);
        }

        [Fact]
        public async Task BrowserOriginsAreRefusedUnlessListed()
        {
            var fake = new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("ok")));
            var host = Start(fake, c => c.CorsOrigins.Add("http://localhost:5173"), out var key);
            var evil = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), key, new Dictionary<string, string> { { "Origin", "https://evil.example" } });
            Assert.Equal(HttpStatusCode.Forbidden, evil.StatusCode);
            var ok = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), key, new Dictionary<string, string> { { "Origin", "http://localhost:5173" } });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("http://localhost:5173", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }

        [Fact]
        public async Task AdminApiNeedsTheSessionTokenAndNeverReturnsKeys()
        {
            var host = Start(new FakeUpstream(), c => c.FindProvider("anthropic")!.ApiKeyProtected = SecretProtector.Protect("sk-ant-supersecretsupersecret"), out _);
            var anon = await _http.GetAsync(host.BaseUrl + "/api/config");
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
            var req = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/api/config");
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
            var r = await _http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.DoesNotContain("supersecret", text);
            Assert.DoesNotContain("dpapi:", text);
            Assert.Contains("\"keyHint\":\"…cret\"", text);
        }

        [Fact]
        public async Task CliReachesTheAdminApiThroughItsDpapiTokenFile()
        {
            var host = Start(new FakeUpstream(), null, out _);
            var file = Path.Combine(_dir, CliAccess.FileName);
            Assert.True(File.Exists(file));
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(host.CliToken, text);
            Assert.DoesNotContain(host.SessionToken, text);
            var info = JObject.Parse(text);
            Assert.Equal(host.Port, (int)info["port"]!);
            var token = CliAccess.Unprotect((string?)info["token"]);
            Assert.Equal(host.CliToken, token);

            var req = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/api");
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, token);
            var r = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var routes = JObject.Parse(await r.Content.ReadAsStringAsync())["routes"]!.Select(x => (string?)x).ToList();
            Assert.Contains("GET /api/status", routes);
            Assert.Contains("POST /api/providers/{id}/test", routes);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync(host.BaseUrl + "/api")).StatusCode);

            host.Stop();
            Assert.False(File.Exists(file));
        }

        [Fact]
        public async Task ClientKeyIsShownOnceAndThenWorks()
        {
            var fake = new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("ok")));
            var host = Start(fake, null, out _);
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/api/clients") { Content = new StringContent("{\"name\":\"claude-code\"}", Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
            var created = JObject.Parse(await (await _http.SendAsync(req)).Content.ReadAsStringAsync());
            var newKey = (string)created["key"]!;
            Assert.StartsWith("fl-", newKey);
            Assert.DoesNotContain(newKey, ConfigStore.Serialize(host.Config.Snapshot()));
            var r = await Post(host, "/v1/chat/completions", Chat("fast", "hi"), newKey);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("claude-code", host.Metrics.Recent(1).Single().Client);
        }

        public void Dispose()
        {
            foreach (var h in _hosts) h.Dispose();
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
