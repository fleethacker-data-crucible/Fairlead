using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>What a provider with the openai preset (and one without) actually receives.</summary>
    public sealed class OpenAiParamsGatewayTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-oai-" + Guid.NewGuid().ToString("N"));
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly HttpClient _http = new HttpClient();

        private async Task<JObject> Upstream(string preset, string model)
        {
            JObject? seen = null;
            var fake = new FakeUpstream().On("groq.test", (r, b) => { seen = b; return FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("ok")); });
            var cfg = RoutingTests.Config();
            var p = cfg.FindProvider("groq")!;
            p.BaseUrl = "https://groq.test/openai/v1";
            p.Preset = preset;
            var key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = key.Substring(0, 7), Hash = GatewayEngine.HashKey(key) });
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            host.Start();
            _hosts.Add(host);
            var body = new JObject { ["model"] = "groq/" + model, ["max_tokens"] = 50, ["temperature"] = 0.2, ["top_p"] = 0.9, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } } };
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/chat/completions") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            var r = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return seen!;
        }

        [Fact]
        public async Task OpenAiPresetGetsMaxCompletionTokensAndReasoningModelsLoseSampling()
        {
            var reasoning = await Upstream("openai", "gpt-5-mini");
            Assert.Null(reasoning["max_tokens"]);
            Assert.Equal(50, (int?)reasoning["max_completion_tokens"]);
            Assert.Null(reasoning["temperature"]);
            Assert.Null(reasoning["top_p"]);
            var chat = await Upstream("openai", "gpt-4.1-mini");
            Assert.Equal(50, (int?)chat["max_completion_tokens"]);
            Assert.NotNull(chat["temperature"]);
        }

        [Fact]
        public async Task OtherProvidersGetTheBodyUnchanged()
        {
            var body = await Upstream("groq", "gpt-5-mini");
            Assert.Equal(50, (int?)body["max_tokens"]);
            Assert.Null(body["max_completion_tokens"]);
            Assert.NotNull(body["temperature"]);
        }

        public void Dispose()
        {
            foreach (var h in _hosts) { try { h.Stop(); } catch (Exception) { } }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
