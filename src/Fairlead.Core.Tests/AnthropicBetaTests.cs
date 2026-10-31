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
    /// <summary>The anthropic-beta header reaches Anthropic targets, and only those.</summary>
    public sealed class AnthropicBetaTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-beta-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private FairleadHost? _host;
        private string _key = "";
        private string? _anthropicSaw;
        private string? _groqSaw;

        [Theory]
        [InlineData("prompt-caching-2024-07-31", "prompt-caching-2024-07-31")]
        [InlineData(" a-b, c_d.e:f ", "a-b, c_d.e:f")]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData(null, null)]
        [InlineData("bad<script>", null)]
        [InlineData("semi;colon", null)]
        public void OnlyFeatureNamesAreForwarded(string? value, string? expected)
        {
            Assert.Equal(expected, GatewayEngine.CleanBeta(value!));
        }

        [Fact]
        public void TheLengthIsCapped()
        {
            Assert.Null(GatewayEngine.CleanBeta(new string('a', 401)));
            Assert.NotNull(GatewayEngine.CleanBeta(new string('a', 400)));
        }

        private void Start()
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("anthropic")!.BaseUrl = "https://anthropic.test";
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            _key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = _key.Substring(0, 7), Hash = GatewayEngine.HashKey(_key) });
            var fake = new FakeUpstream()
                .On("anthropic.test", (r, b) =>
                {
                    _anthropicSaw = r.Headers.TryGetValues("anthropic-beta", out var v) ? string.Join("|", v) : null;
                    return FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.AnthropicAnswer("ok"));
                })
                .On("groq.test", (r, b) =>
                {
                    _groqSaw = r.Headers.TryGetValues("anthropic-beta", out var v) ? string.Join("|", v) : null;
                    return FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("ok"));
                });
            _host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            _host.Start();
        }

        private async Task<HttpResponseMessage> Messages(string model, string? beta)
        {
            var body = new JObject { ["model"] = model, ["max_tokens"] = 10, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } } };
            var req = new HttpRequestMessage(HttpMethod.Post, _host!.BaseUrl + "/v1/messages") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("x-api-key", _key);
            if (beta != null) req.Headers.TryAddWithoutValidation("anthropic-beta", beta);
            return await _http.SendAsync(req);
        }

        [Fact]
        public async Task ABetaHeaderFromTheCallerReachesAnAnthropicTarget()
        {
            Start();
            var r = await Messages("anthropic/claude-big", "prompt-caching-2024-07-31,max-tokens-3-5-sonnet-2024-07-15");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("prompt-caching-2024-07-31,max-tokens-3-5-sonnet-2024-07-15", _anthropicSaw);
        }

        [Fact]
        public async Task NoHeaderMeansNoneIsSent_AndAnUnsafeValueIsDropped()
        {
            Start();
            Assert.Equal(HttpStatusCode.OK, (await Messages("anthropic/claude-big", null)).StatusCode);
            Assert.Null(_anthropicSaw);
            Assert.Equal(HttpStatusCode.OK, (await Messages("anthropic/claude-big", "x<y>")).StatusCode);
            Assert.Null(_anthropicSaw);
        }

        [Fact]
        public async Task OtherProvidersNeverSeeIt()
        {
            Start();
            var r = await Messages("groq/llama-fast", "prompt-caching-2024-07-31");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Null(_groqSaw);
            Assert.Null(_anthropicSaw);
        }

        public void Dispose()
        {
            try { _host?.Stop(); } catch (Exception) { }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
