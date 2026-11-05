using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Host;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>A local server is the authority on what is installed: Fetch model list drops discoveries it no longer lists.</summary>
    public sealed class LocalModelTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-localmodels-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private FairleadHost? _host;

        private void Start(string tagsJson)
        {
            var cfg = RoutingTests.Config();
            var ollama = cfg.FindProvider("ollama")!;
            ollama.BaseUrl = "http://ollama.test:11434";
            ollama.Models.Add(new ModelInfo { Id = "gone:latest", Discovered = true });
            var fake = new FakeUpstream().On("ollama.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, JObject.Parse(tagsJson)));
            _host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            _host.Start();
        }

        private async Task<JObject> Discover()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _host!.BaseUrl + "/api/providers/ollama/discover") { Content = new StringContent("{}") };
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, _host.SessionToken);
            var r = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return JObject.Parse(await r.Content.ReadAsStringAsync());
        }

        private static string[] Ollama(JObject cfg)
        {
            return ((JArray)cfg["providers"]!).Single(p => (string?)p["id"] == "ollama")["models"]!.Select(m => (string)m["id"]!).ToArray();
        }

        [Fact]
        public async Task AnEmptyServerDropsEarlierDiscoveriesButKeepsHandTypedEntries()
        {
            Start("{\"models\":[]}");
            var ids = Ollama(await Discover());
            Assert.DoesNotContain("gone:latest", ids);
            Assert.Contains("phi4", ids); // typed by hand in the config, not discovered
        }

        [Fact]
        public async Task InstalledModelsAreAddedAndStaleOnesDropped()
        {
            Start("{\"models\":[{\"name\":\"qwen2.5:1.5b\"}]}");
            var ids = Ollama(await Discover());
            Assert.Contains("qwen2.5:1.5b", ids);
            Assert.DoesNotContain("gone:latest", ids);
        }

        public void Dispose()
        {
            _host?.Dispose();
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }
    }
}
