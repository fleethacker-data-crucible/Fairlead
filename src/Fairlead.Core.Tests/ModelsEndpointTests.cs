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
    public sealed class ModelsEndpointTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-models2-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private FairleadHost? _host;
        private string _key = "";

        private void Start()
        {
            var cfg = RoutingTests.Config();
            _key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = _key.Substring(0, 7), Hash = GatewayEngine.HashKey(_key) });
            _host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = new FakeUpstream(), Monitor = false, PreferredPort = 0 });
            _host.Start();
        }

        private async Task<HttpResponseMessage> Get(string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, _host!.BaseUrl + path);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
            return await _http.SendAsync(req);
        }

        [Fact]
        public async Task AProviderModelIdCanBeFetchedWithItsSlash()
        {
            Start();
            var r = await Get("/v1/models/ollama/tiny");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var m = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal("ollama/tiny", (string?)m["id"]);
            Assert.Equal("ollama", (string?)m["owned_by"]);
            Assert.Equal(HttpStatusCode.OK, (await Get("/v1/models/ollama%2Ftiny")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Get("/v1/models/fast")).StatusCode); // routes still work
            Assert.Equal(HttpStatusCode.NotFound, (await Get("/v1/models/ollama/nope")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Get("/v1/models/nothing")).StatusCode);
        }

        [Fact]
        public async Task TheContextWindowIsShownOnlyWhereItIsKnown_AndARouteShowsItsSmallestWindow()
        {
            Start();
            var list = (JArray)JObject.Parse(await (await Get("/v1/models")).Content.ReadAsStringAsync())["data"]!;
            JToken Entry(string id) { return list.Single(e => (string?)e["id"] == id); }
            Assert.Equal(2048, (int?)Entry("ollama/tiny")["context_window"]);
            Assert.Null(Entry("groq/llama-fast")["context_window"]); // no window known: no number invented
            Assert.Equal(2048, (int?)Entry("fast")["context_window"]); // the route's smallest known target window
            var one = JObject.Parse(await (await Get("/v1/models/ollama/tiny")).Content.ReadAsStringAsync());
            Assert.Equal(2048, (int?)one["context_window"]);
        }

        public void Dispose()
        {
            try { _host?.Stop(); } catch (Exception) { }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
