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
    /// <summary>Per-key request limits: off by default, a 429 with Retry-After over the limit.</summary>
    public sealed class RateLimitTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-rate-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private FairleadHost? _host;
        private string _limitedKey = "";
        private string _freeKey = "";

        [Fact]
        public void TheWindowCountsPerKeyAndSlidesAfterAMinute()
        {
            var limiter = new ClientRateLimiter();
            var t = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            int retry;
            Assert.True(limiter.TryAcquire("a", 2, t, out retry));
            Assert.True(limiter.TryAcquire("a", 2, t.AddSeconds(10), out retry));
            Assert.False(limiter.TryAcquire("a", 2, t.AddSeconds(20), out retry));
            Assert.Equal(40, retry); // the first request leaves the window at +60 s
            Assert.True(limiter.TryAcquire("b", 2, t.AddSeconds(20), out retry)); // another key has its own window
            Assert.True(limiter.TryAcquire("a", 2, t.AddSeconds(61), out retry)); // the first one has aged out
            Assert.False(limiter.TryAcquire("a", 2, t.AddSeconds(62), out retry));
            Assert.True(limiter.TryAcquire("a", 0, t.AddSeconds(62), out retry)); // no limit
            limiter.Forget("a");
            Assert.True(limiter.TryAcquire("a", 1, t.AddSeconds(62), out retry));
        }

        private void Start(int? limit)
        {
            var cfg = RoutingTests.Config();
            _limitedKey = GatewayEngine.NewClientKey();
            _freeKey = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "lim", Name = "limited", Prefix = _limitedKey.Substring(0, 7), Hash = GatewayEngine.HashKey(_limitedKey), RateLimitPerMinute = limit });
            cfg.Clients.Add(new ClientKey { Id = "free", Name = "free", Prefix = _freeKey.Substring(0, 7), Hash = GatewayEngine.HashKey(_freeKey) });
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            var fake = new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("ok")));
            _host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            _host.Start();
        }

        private async Task<HttpResponseMessage> Chat(string key, string path = "/v1/chat/completions")
        {
            var body = new JObject { ["model"] = "fast", ["max_tokens"] = 10, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } } };
            var req = new HttpRequestMessage(HttpMethod.Post, _host!.BaseUrl + path) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return await _http.SendAsync(req);
        }

        private async Task<HttpResponseMessage> Admin(HttpMethod method, string path, string? body = null)
        {
            var req = new HttpRequestMessage(method, _host!.BaseUrl + path);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, _host.SessionToken);
            return await _http.SendAsync(req);
        }

        [Fact]
        public async Task OverTheLimitIs429WithRetryAfter_OtherKeysAndUnlimitedKeysAreUntouched()
        {
            Start(2);
            Assert.Equal(HttpStatusCode.OK, (await Chat(_limitedKey)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Chat(_limitedKey)).StatusCode);
            var third = await Chat(_limitedKey);
            Assert.Equal((HttpStatusCode)429, third.StatusCode);
            var wait = int.Parse(third.Headers.GetValues("Retry-After").Single());
            Assert.InRange(wait, 1, 60);
            var text = await third.Content.ReadAsStringAsync();
            Assert.Contains("limited to 2 requests a minute", text);
            Assert.Contains("rate_limit", text);
            for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await Chat(_freeKey)).StatusCode);
            // The Anthropic endpoint counts against the same window and answers in its own shape.
            var anthropic = await Chat(_limitedKey, "/v1/messages");
            Assert.Equal((HttpStatusCode)429, anthropic.StatusCode);
            Assert.Equal("error", (string?)JObject.Parse(await anthropic.Content.ReadAsStringAsync())["type"]);
        }

        [Fact]
        public async Task WithoutALimitNothingHappens()
        {
            Start(null);
            for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.OK, (await Chat(_limitedKey)).StatusCode);
        }

        [Fact]
        public async Task TheLimitCanBeSetAndClearedThroughTheAdminApi_AndShowsInTheConfig()
        {
            Start(null);
            Assert.Equal(HttpStatusCode.OK, (await Chat(_limitedKey)).StatusCode);
            var set = await Admin(HttpMethod.Post, "/api/clients/lim/rate-limit", "{\"requestsPerMinute\":1}");
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal(1, (int?)JObject.Parse(await set.Content.ReadAsStringAsync())["rateLimitPerMinute"]);
            var config = JObject.Parse(await (await Admin(HttpMethod.Get, "/api/config")).Content.ReadAsStringAsync());
            Assert.Equal(1, (int?)((JArray)config["clients"]!).Single(c => (string?)c["id"] == "lim")["rateLimitPerMinute"]);
            Assert.Equal(HttpStatusCode.OK, (await Chat(_limitedKey)).StatusCode);
            Assert.Equal((HttpStatusCode)429, (await Chat(_limitedKey)).StatusCode);
            var clear = await Admin(HttpMethod.Post, "/api/clients/lim/rate-limit", "{\"requestsPerMinute\":0}");
            Assert.Null((int?)JObject.Parse(await clear.Content.ReadAsStringAsync())["rateLimitPerMinute"]);
            Assert.Equal(HttpStatusCode.OK, (await Chat(_limitedKey)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Admin(HttpMethod.Post, "/api/clients/nope/rate-limit", "{\"requestsPerMinute\":5}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Admin(HttpMethod.Post, "/api/clients/lim/rate-limit", "{\"requestsPerMinute\":-3}")).StatusCode);
        }

        [Fact]
        public async Task ANewKeyCanBeCreatedWithALimit()
        {
            Start(null);
            var r = await Admin(HttpMethod.Post, "/api/clients", "{\"name\":\"nightly\",\"rateLimitPerMinute\":3}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var made = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal(3, (int?)made["client"]!["rateLimitPerMinute"]);
            var key = (string)made["key"]!;
            for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await Chat(key)).StatusCode);
            Assert.Equal((HttpStatusCode)429, (await Chat(key)).StatusCode);
        }

        public void Dispose()
        {
            try { _host?.Stop(); } catch (Exception) { }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
