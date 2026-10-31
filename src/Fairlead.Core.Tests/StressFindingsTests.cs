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
    /// <summary>Findings from the long-running 70B stress test: keep-alives, and the status codes callers see.</summary>
    public sealed class StressFindingsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-stress-" + Guid.NewGuid().ToString("N"));
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly HttpClient _http = new HttpClient();

        private FairleadHost Start(FakeUpstream fake, Action<FairleadConfig>? edit, out string key)
        {
            var cfg = RoutingTests.Config();
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

        private async Task<HttpResponseMessage> Chat(FairleadHost host, string key, string model, string prompt, bool stream)
        {
            var body = new JObject { ["model"] = model, ["stream"] = stream, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } } };
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/chat/completions") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return await _http.SendAsync(req);
        }

        [Fact]
        public void TheSseReaderCanReportKeepAlivesSoACallerCanResetItsIdleTimer()
        {
            var text = ": keep-alive\n\n: keep-alive\n\ndata: {\"a\":1}\n\n";
            using (var reader = new SseReader(new MemoryStream(Encoding.UTF8.GetBytes(text))) { EmitComments = true })
            {
                Assert.True(reader.NextAsync(default).Result!.IsComment);
                Assert.True(reader.NextAsync(default).Result!.IsComment);
                var e = reader.NextAsync(default).Result!;
                Assert.False(e.IsComment);
                Assert.Equal(1, (int)e.Json()!["a"]!);
                Assert.Null(reader.NextAsync(default).Result);
            }
        }

        [Fact]
        public async Task KeepAlivesFromTheUpstreamReachTheCaller()
        {
            var fake = new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Sse(": keep-alive\n\n", ": keep-alive\n\n", FakeUpstream.OpenAiChunk("hi"), FakeUpstream.OpenAiFinish(), "data: [DONE]\n\n"));
            var host = Start(fake, null, out var key);
            var r = await Chat(host, key, "fast", "hello", true);
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(2, text.Split(new[] { ": keep-alive" }, StringSplitOptions.None).Length - 1);
            Assert.Contains("\"content\":\"hi\"", text);
            Assert.EndsWith("data: [DONE]\n\n", text);
        }

        [Fact]
        public async Task APromptLongerThanEveryWindowIs400NotA503()
        {
            var fake = new FakeUpstream().On("ollama.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("never")));
            var host = Start(fake, c => { var fast = c.FindRoute("fast")!; fast.Targets = new List<string> { "ollama/tiny" }; fast.LocalFallback = false; }, out var key);
            var r = await Chat(host, key, "fast", new string('x', 20000), false);
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("context window", text);
            Assert.Contains("2048", text);
            // A prompt that fits is still served.
            Assert.Equal(HttpStatusCode.OK, (await Chat(host, key, "fast", "short", false)).StatusCode);
        }

        [Fact]
        public async Task AnUpstreamThatIsBusyIsReportedAs503NotAsABadGateway()
        {
            var busy = FakeUpstream.Json(HttpStatusCode.ServiceUnavailable, new JObject { ["error"] = new JObject { ["message"] = "queue is full" } });
            var fake = new FakeUpstream().On("ollama.test", (r, b) => FakeUpstream.Json(HttpStatusCode.ServiceUnavailable, new JObject { ["error"] = new JObject { ["message"] = "queue is full" } }));
            var host = Start(fake, c => { var fast = c.FindRoute("fast")!; fast.Targets = new List<string> { "ollama/tiny" }; fast.LocalFallback = false; }, out var key);
            var r = await Chat(host, key, "fast", "hello", false);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Contains("queue is full", await r.Content.ReadAsStringAsync());
            busy.Dispose();
        }

        public void Dispose()
        {
            foreach (var h in _hosts) { try { h.Stop(); } catch (Exception) { } }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
