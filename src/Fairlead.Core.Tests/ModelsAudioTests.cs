using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Gateway;
using Fairlead.Core.Host;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    public sealed class ModelsAudioTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-models-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private FairleadHost? _host;
        private string _key = "";

        [Theory]
        [InlineData("whisper-1", "transcription")]
        [InlineData("whisper-large-v3-turbo", "transcription")]
        [InlineData("gpt-4o-transcribe", "transcription")]
        [InlineData("gpt-4o-mini-tts", "speech")]
        [InlineData("tts-1-hd", "speech")]
        [InlineData("playai-tts", "speech")]
        [InlineData("llama-3.1-8b", null)]
        [InlineData("gpt-4o", null)]
        [InlineData("text-embedding-3-small", null)]
        [InlineData("", null)]
        public void ModelsAreAudioModelsByName(string id, string? kind)
        {
            Assert.Equal(kind == null ? new string[0] : new[] { kind }, AudioModels.KindsOf(id).ToArray());
        }

        private async Task<JToken> Get(string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, _host!.BaseUrl + path);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
            var r = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return JToken.Parse(await r.Content.ReadAsStringAsync());
        }

        private void Start()
        {
            var cfg = RoutingTests.Config();
            var groq = cfg.FindProvider("groq")!;
            groq.Models.Add(new ModelInfo { Id = "whisper-large-v3" });
            groq.Models.Add(new ModelInfo { Id = "playai-tts" });
            cfg.FindProvider("ollama")!.Models.Add(new ModelInfo { Id = "whisper-local" }); // Ollama has no audio endpoints
            cfg.FindProvider("anthropic")!.Models.Add(new ModelInfo { Id = "tts-claude" });
            cfg.Routes.Add(new RouteConfig { Id = "speech", Targets = new System.Collections.Generic.List<string> { "groq/playai-tts" } });
            _key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = _key.Substring(0, 7), Hash = GatewayEngine.HashKey(_key) });
            _host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = new FakeUpstream(), Monitor = false, PreferredPort = 0 });
            _host.Start();
        }

        [Fact]
        public async Task ModelListMarksTheAudioModelsAndRoutesThatServeThem()
        {
            Start();
            var list = (JArray)(await Get("/v1/models"))["data"]!;
            JToken Entry(string id) { return list.Single(e => (string?)e["id"] == id); }
            Assert.Equal("transcription", (string?)Entry("groq/whisper-large-v3")["audio"]![0]);
            Assert.Equal("speech", (string?)Entry("groq/playai-tts")["audio"]![0]);
            Assert.Equal("speech", (string?)Entry("speech")["audio"]![0]); // the route that reaches it
            Assert.Null(Entry("fast")["audio"]); // a chat route
            Assert.Null(Entry("ollama/whisper-local")["audio"]);
            Assert.Null(Entry("anthropic/tts-claude")["audio"]);
            Assert.Null(Entry("groq/llama-fast")["audio"]);
        }

        [Fact]
        public async Task CapabilityAudioListsOnlyAudioModels()
        {
            Start();
            var ids = ((JArray)(await Get("/v1/models?capability=audio"))["data"]!).Select(e => (string?)e["id"]).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "groq/playai-tts", "groq/whisper-large-v3", "speech" }, ids);
            var one = await Get("/v1/models/speech");
            Assert.Equal("speech", (string?)one["audio"]![0]);
        }

        public void Dispose()
        {
            try { _host?.Stop(); } catch (Exception) { }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
