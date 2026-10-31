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
using Fairlead.Core.Telemetry;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>Daily token budgets per client key: off by default, a 429 over the limit, smart becomes fast over the soft limit.</summary>
    public sealed class BudgetTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-budget-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private string _key = "";
        private string _other = "";

        [Fact]
        public void UsageCountsPerKeyRollsAtMidnightAndIsSeededFromTheLog()
        {
            var usage = new ClientUsage();
            var t = new DateTime(2026, 11, 1, 23, 0, 0, DateTimeKind.Utc);
            usage.Add("a", 100, t);
            usage.Add("a", 50, t);
            usage.Add("b", 7, t);
            usage.Add(null, 99, t);
            usage.Add("a", 0, t);
            Assert.Equal(150, usage.Used("a", t));
            Assert.Equal(7, usage.Used("b", t));
            Assert.Equal(0, usage.Used("nobody", t));
            Assert.Equal(3600, ClientUsage.SecondsToReset(t));
            Assert.Equal(0, usage.Used("a", t.AddHours(1))); // a new UTC day
            usage.Seed(new[] { new RequestLogEntry { ClientId = "a", InputTokens = 10, OutputTokens = 5 }, new RequestLogEntry { ClientId = "a" }, new RequestLogEntry { ClientId = "b", InputTokens = 1 } }, t.AddHours(1));
            Assert.Equal(15, usage.Used("a", t.AddHours(1)));
            Assert.Equal(1, usage.Used("b", t.AddHours(1)));
        }

        private FairleadHost Start(Action<ClientKey> edit, string? seedLogForClient = null)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("anthropic")!.BaseUrl = "https://anthropic.test";
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            cfg.FindProvider("ollama")!.BaseUrl = "http://ollama.test:11434";
            _key = GatewayEngine.NewClientKey();
            _other = GatewayEngine.NewClientKey();
            var limited = new ClientKey { Id = "lim", Name = "limited", Prefix = _key.Substring(0, 7), Hash = GatewayEngine.HashKey(_key) };
            edit(limited);
            cfg.Clients.Add(limited);
            cfg.Clients.Add(new ClientKey { Id = "free", Name = "free", Prefix = _other.Substring(0, 7), Hash = GatewayEngine.HashKey(_other) });
            if (seedLogForClient != null)
            {
                Directory.CreateDirectory(Path.Combine(_dir, "logs"));
                var line = new JObject { ["id"] = "old", ["time"] = DateTimeOffset.UtcNow.ToString("o"), ["clientId"] = seedLogForClient, ["endpoint"] = "/v1/chat/completions", ["status"] = 200, ["inputTokens"] = 400, ["outputTokens"] = 200 }.ToString(Formatting.None);
                File.AppendAllText(RequestLog.FileFor(Path.Combine(_dir, "logs"), DateTimeOffset.UtcNow), line + "\n");
            }
            var fake = new FakeUpstream()
                .On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, Answer("fast", 60, 50)))
                .On("ollama.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, Answer("local", 60, 50)))
                .On("anthropic.test", (r, b) =>
                {
                    var a = FakeUpstream.AnthropicAnswer("smart answer");
                    a["usage"] = new JObject { ["input_tokens"] = 60, ["output_tokens"] = 50 };
                    return FakeUpstream.Json(HttpStatusCode.OK, a);
                });
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private static JObject Answer(string text, int input, int output)
        {
            var a = FakeUpstream.OpenAiAnswer(text);
            a["usage"] = new JObject { ["prompt_tokens"] = input, ["completion_tokens"] = output, ["total_tokens"] = input + output };
            return a;
        }

        private async Task<HttpResponseMessage> Chat(FairleadHost host, string key, string route, string path = "/v1/chat/completions")
        {
            var body = new JObject { ["model"] = route, ["max_tokens"] = 10, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } } };
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + path) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return await _http.SendAsync(req);
        }

        private async Task<HttpResponseMessage> Admin(FairleadHost host, HttpMethod method, string path, string? body = null)
        {
            var req = new HttpRequestMessage(method, host.BaseUrl + path);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
            return await _http.SendAsync(req);
        }

        [Fact]
        public async Task OverTheDailyLimitIs429UntilMidnight_OtherKeysAndUnlimitedKeysAreUntouched()
        {
            var host = Start(k => k.DailyTokenLimit = 100);
            Assert.Equal(HttpStatusCode.OK, (await Chat(host, _key, "fast")).StatusCode); // 110 tokens used, over 100
            var second = await Chat(host, _key, "fast");
            Assert.Equal((HttpStatusCode)429, second.StatusCode);
            var wait = int.Parse(second.Headers.GetValues("Retry-After").Single());
            Assert.InRange(wait, 1, 86400);
            var text = await second.Content.ReadAsStringAsync();
            Assert.Contains("daily budget", text);
            Assert.Contains("110 of 100", text);
            Assert.Equal((HttpStatusCode)429, (await Chat(host, _key, "fast", "/v1/messages")).StatusCode); // same budget, Anthropic shape
            for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await Chat(host, _other, "fast")).StatusCode);
        }

        [Fact]
        public async Task WithoutALimitNothingIsEnforced()
        {
            var host = Start(k => { });
            for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.OK, (await Chat(host, _key, "fast")).StatusCode);
        }

        [Fact]
        public async Task PastTheSoftLimitSmartBecomesFast()
        {
            var host = Start(k => k.DailyTokenSoftLimit = 100);
            var first = await Chat(host, _key, "smart");
            Assert.Equal("smart", first.Headers.GetValues("X-Fairlead-Route").Single());
            Assert.StartsWith("anthropic/", first.Headers.GetValues("X-Fairlead-Target").Single());
            var second = await Chat(host, _key, "smart"); // 110 tokens are used now, past the soft limit of 100
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal("fast", second.Headers.GetValues("X-Fairlead-Route").Single());
            Assert.StartsWith("groq/", second.Headers.GetValues("X-Fairlead-Target").Single());
            // Another key is not downgraded, and the fast route itself is not touched.
            Assert.Equal("smart", (await Chat(host, _other, "smart")).Headers.GetValues("X-Fairlead-Route").Single());
        }

        [Fact]
        public async Task TodaysLogSeedsTheUsageAfterARestart()
        {
            var host = Start(k => k.DailyTokenLimit = 500, seedLogForClient: "lim"); // the log already shows 600 tokens today
            Assert.Equal((HttpStatusCode)429, (await Chat(host, _key, "fast")).StatusCode);
            var usage = JArray.Parse(await (await Admin(host, HttpMethod.Get, "/api/clients/usage")).Content.ReadAsStringAsync());
            var row = usage.Single(u => (string?)u["id"] == "lim");
            Assert.Equal(600, (long?)row["usedToday"]);
            Assert.Equal(500, (long?)row["dailyTokenLimit"]);
        }

        [Fact]
        public async Task BudgetsAreSetAndClearedThroughTheAdminApi()
        {
            var host = Start(k => { });
            var set = await Admin(host, HttpMethod.Post, "/api/clients/lim/budget", "{\"dailyTokens\":100,\"softDailyTokens\":50}");
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            var body = JObject.Parse(await set.Content.ReadAsStringAsync());
            Assert.Equal(100, (long?)body["dailyTokenLimit"]);
            Assert.Equal(50, (long?)body["dailyTokenSoftLimit"]);
            Assert.Equal(HttpStatusCode.OK, (await Chat(host, _key, "fast")).StatusCode);
            Assert.Equal((HttpStatusCode)429, (await Chat(host, _key, "fast")).StatusCode);
            var cleared = JObject.Parse(await (await Admin(host, HttpMethod.Post, "/api/clients/lim/budget", "{\"dailyTokens\":0}")).Content.ReadAsStringAsync());
            Assert.Null((long?)cleared["dailyTokenLimit"]);
            Assert.Equal(50, (long?)cleared["dailyTokenSoftLimit"]); // only what was given changes
            Assert.Equal(HttpStatusCode.OK, (await Chat(host, _key, "fast")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Admin(host, HttpMethod.Post, "/api/clients/lim/budget", "{\"dailyTokens\":10,\"softDailyTokens\":20}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Admin(host, HttpMethod.Post, "/api/clients/nope/budget", "{\"dailyTokens\":10}")).StatusCode);
            var made = JObject.Parse(await (await Admin(host, HttpMethod.Post, "/api/clients", "{\"name\":\"nightly\",\"dailyTokenLimit\":1000,\"dailyTokenSoftLimit\":800}")).Content.ReadAsStringAsync());
            Assert.Equal(1000, (long?)made["client"]!["dailyTokenLimit"]);
            Assert.Equal(800, (long?)made["client"]!["dailyTokenSoftLimit"]);
        }

        public void Dispose()
        {
            foreach (var h in _hosts) { try { h.Stop(); } catch (Exception) { } }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
