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
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>The exact-match response cache: off by default, temperature-0 non-streaming only, never across keys.</summary>
    public sealed class ResponseCacheTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-cache-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private int _calls;
        private string _keyA = "";
        private string _keyB = "";

        [Fact]
        public void TheKeyIgnoresPropertyOrderButNotContentOrKey()
        {
            var a = JObject.Parse("{\"model\":\"fast\",\"temperature\":0,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
            var b = JObject.Parse("{ \"messages\": [ { \"content\": \"hi\", \"role\": \"user\" } ], \"temperature\": 0, \"model\": \"fast\" }");
            var c = JObject.Parse("{\"model\":\"fast\",\"temperature\":0,\"messages\":[{\"role\":\"user\",\"content\":\"hi!\"}]}");
            Assert.Equal(ResponseCache.KeyFor("k1", Dialect.OpenAi, null, a), ResponseCache.KeyFor("k1", Dialect.OpenAi, null, b));
            Assert.NotEqual(ResponseCache.KeyFor("k1", Dialect.OpenAi, null, a), ResponseCache.KeyFor("k1", Dialect.OpenAi, null, c));
            Assert.NotEqual(ResponseCache.KeyFor("k1", Dialect.OpenAi, null, a), ResponseCache.KeyFor("k2", Dialect.OpenAi, null, a));
            Assert.NotEqual(ResponseCache.KeyFor("k1", Dialect.OpenAi, null, a), ResponseCache.KeyFor("k1", Dialect.Anthropic, null, a));
            Assert.NotEqual(ResponseCache.KeyFor("k1", Dialect.OpenAi, null, a), ResponseCache.KeyFor("k1", Dialect.OpenAi, "local", a));
        }

        [Fact]
        public void OnlyTemperatureZeroNonStreamingRequestsAreDeterministic()
        {
            Assert.True(ResponseCache.IsDeterministic(JObject.Parse("{\"temperature\":0}")));
            Assert.True(ResponseCache.IsDeterministic(JObject.Parse("{\"temperature\":0.0,\"stream\":false}")));
            Assert.False(ResponseCache.IsDeterministic(JObject.Parse("{}")));
            Assert.False(ResponseCache.IsDeterministic(JObject.Parse("{\"temperature\":0.7}")));
            Assert.False(ResponseCache.IsDeterministic(JObject.Parse("{\"temperature\":0,\"stream\":true}")));
            Assert.False(ResponseCache.IsDeterministic(JObject.Parse("{\"temperature\":\"zero\"}")));
        }

        [Fact]
        public void EntriesExpireAndTheOldestLeaveFirst()
        {
            var cache = new ResponseCache(60, 2);
            var t = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            CachedAnswer got;
            cache.Put("a", new CachedAnswer { Body = "A", StoredUtc = t });
            cache.Put("b", new CachedAnswer { Body = "B", StoredUtc = t });
            Assert.True(cache.TryGet("a", t.AddSeconds(5), out got)); // a is now the freshest
            cache.Put("c", new CachedAnswer { Body = "C", StoredUtc = t.AddSeconds(6) });
            Assert.False(cache.TryGet("b", t.AddSeconds(7), out got)); // b was the least recently used
            Assert.True(cache.TryGet("a", t.AddSeconds(7), out got));
            Assert.False(cache.TryGet("a", t.AddSeconds(61), out got)); // past its lifetime
            Assert.Equal(1, cache.Clear());
            Assert.False(new ResponseCache(0, 10).Enabled);
        }

        private FairleadHost Start(int seconds)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            _keyA = GatewayEngine.NewClientKey();
            _keyB = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "a", Name = "a", Prefix = _keyA.Substring(0, 7), Hash = GatewayEngine.HashKey(_keyA) });
            cfg.Clients.Add(new ClientKey { Id = "b", Name = "b", Prefix = _keyB.Substring(0, 7), Hash = GatewayEngine.HashKey(_keyB) });
            var fake = new FakeUpstream().On("groq.test", (r, b) =>
            {
                var n = ++_calls;
                return FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("answer " + n));
            });
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs"), ResponseCacheSeconds = seconds }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private async Task<HttpResponseMessage> Chat(FairleadHost host, string key, string prompt, double? temperature, bool stream = false, string path = "/v1/chat/completions")
        {
            var body = new JObject { ["model"] = "groq/llama-fast", ["max_tokens"] = 10, ["stream"] = stream, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } } };
            if (temperature.HasValue) body["temperature"] = temperature.Value;
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + path) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return await _http.SendAsync(req);
        }

        private static async Task<string> Answer(HttpResponseMessage r)
        {
            return (string)JObject.Parse(await r.Content.ReadAsStringAsync())["choices"]![0]!["message"]!["content"]!;
        }

        [Fact]
        public async Task ARepeatedTemperatureZeroRequestIsAnsweredFromTheCache()
        {
            var host = Start(60);
            var first = await Chat(host, _keyA, "what is 2+2", 0);
            Assert.Equal("miss", first.Headers.GetValues("X-Fairlead-Cache").Single());
            Assert.Equal("answer 1", await Answer(first));
            var second = await Chat(host, _keyA, "what is 2+2", 0);
            Assert.Equal("hit", second.Headers.GetValues("X-Fairlead-Cache").Single());
            Assert.Equal("answer 1", await Answer(second));
            Assert.Equal("groq/llama-fast", second.Headers.GetValues("X-Fairlead-Target").Single());
            Assert.Equal(1, _calls);
            // A different prompt, another client key, a non-zero temperature and a missing temperature all go to the model.
            Assert.Equal("answer 2", await Answer(await Chat(host, _keyA, "what is 3+3", 0)));
            Assert.Equal("answer 3", await Answer(await Chat(host, _keyB, "what is 2+2", 0)));
            Assert.Equal("answer 4", await Answer(await Chat(host, _keyA, "what is 2+2", 0.7)));
            Assert.Equal("answer 5", await Answer(await Chat(host, _keyA, "what is 2+2", null)));
            Assert.Equal(5, _calls);
            var status = JObject.Parse(await (await Admin(host, HttpMethod.Get, "/api/cache")).Content.ReadAsStringAsync());
            Assert.True((bool?)status["enabled"]);
            Assert.Equal(1, (int?)status["hits"]);
            Assert.Equal(3, (int?)status["entries"]);
            // The request log records the hit as served by the cache, with the route it had.
            var log = string.Join("\n", Directory.GetFiles(Path.Combine(_dir, "logs"), "fairlead-requests-*.jsonl").Select(File.ReadAllText));
            Assert.Contains("\"target\":\"cache\"", log);
        }

        [Fact]
        public async Task StreamingAndPrivateRequestsAreNeverCached()
        {
            var host = Start(60);
            var priv = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/chat/completions") { Content = new StringContent("{\"model\":\"groq/llama-fast\",\"temperature\":0,\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", Encoding.UTF8, "application/json") };
            priv.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _keyA);
            priv.Headers.TryAddWithoutValidation(GatewayEngine.PrivacyHeader, "local");
            var r = await _http.SendAsync(priv);
            Assert.False(r.Headers.Contains("X-Fairlead-Cache")); // private: local only, and not remembered
            var s = await Chat(host, _keyA, "streamed", 0, stream: true);
            Assert.False(s.Headers.Contains("X-Fairlead-Cache"));
        }

        [Fact]
        public async Task OffByDefault_AndClearEmptiesIt()
        {
            var off = Start(0);
            await Chat(off, _keyA, "same", 0);
            var again = await Chat(off, _keyA, "same", 0);
            Assert.False(again.Headers.Contains("X-Fairlead-Cache"));
            Assert.Equal(2, _calls);
            Assert.False((bool?)JObject.Parse(await (await Admin(off, HttpMethod.Get, "/api/cache")).Content.ReadAsStringAsync())["enabled"]);

            var on = Start(60);
            await Chat(on, _keyA, "same", 0);
            var cleared = JObject.Parse(await (await Admin(on, HttpMethod.Post, "/api/cache/clear")).Content.ReadAsStringAsync());
            Assert.Equal(1, (int?)cleared["cleared"]);
            Assert.Equal("miss", (await Chat(on, _keyA, "same", 0)).Headers.GetValues("X-Fairlead-Cache").Single());
        }

        private async Task<HttpResponseMessage> Admin(FairleadHost host, HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, host.BaseUrl + path);
            if (method == HttpMethod.Post) req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
            return await _http.SendAsync(req);
        }

        public void Dispose()
        {
            foreach (var h in _hosts) { try { h.Stop(); } catch (Exception) { } }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
