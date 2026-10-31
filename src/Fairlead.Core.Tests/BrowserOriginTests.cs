using System;
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
    /// <summary>A browser page of a Fleet tool (Scrimshaw's editor, port 8775) can call the gateway, with the headers it sends.</summary>
    public sealed class BrowserOriginTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-origin-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private FairleadHost? _host;
        private string _key = "";

        private void Start(Action<FairleadConfig>? edit = null)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            _key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = _key.Substring(0, 7), Hash = GatewayEngine.HashKey(_key) });
            if (edit != null) edit(cfg);
            var fake = new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("ok")));
            _host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            _host.Start();
        }

        [Fact]
        public void AFirstRunConfigAllowsScrimshawsEditorOnItsDefaultPort()
        {
            var origins = FairleadConfig.CreateDefault().CorsOrigins;
            Assert.Equal(new[] { "http://localhost:8775", "http://127.0.0.1:8775" }, origins.ToArray());
        }

        [Fact]
        public async Task ThePreflightForScrimshawsRequestIsAnswered()
        {
            Start();
            var pre = new HttpRequestMessage(HttpMethod.Options, _host!.BaseUrl + "/v1/chat/completions");
            pre.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:8775");
            pre.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
            pre.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization,content-type,x-fairlead-privacy,x-fairlead-label");
            var r = await _http.SendAsync(pre);
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
            Assert.Equal("http://127.0.0.1:8775", r.Headers.GetValues("Access-Control-Allow-Origin").Single());
            var allowed = r.Headers.GetValues("Access-Control-Allow-Headers").Single().Split(',').Select(h => h.Trim().ToLowerInvariant()).ToList();
            foreach (var h in new[] { "authorization", "content-type", "x-fairlead-privacy", "x-fairlead-label", "x-fairlead-bearing" }) Assert.Contains(h, allowed);
        }

        [Fact]
        public async Task ARealCallFromTheEditorWorksAndAnotherPortOrSiteIsRefused()
        {
            Start();
            async Task<HttpResponseMessage> Call(string origin)
            {
                var body = new JObject { ["model"] = "groq/llama-fast", ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } } };
                var req = new HttpRequestMessage(HttpMethod.Post, _host!.BaseUrl + "/v1/chat/completions") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("Origin", origin);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
                req.Headers.TryAddWithoutValidation(GatewayEngine.LabelHeader, "scrimshaw-drafts");
                return await _http.SendAsync(req);
            }
            var ok = await Call("http://localhost:8775");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("http://localhost:8775", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("X-Fairlead-Target", ok.Headers.GetValues("Access-Control-Expose-Headers").Single());
            Assert.Equal(HttpStatusCode.Forbidden, (await Call("http://127.0.0.1:9999")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call("https://evil.example")).StatusCode);
        }

        [Fact]
        public async Task TheOriginsCanBeRemovedAgain()
        {
            Start(c => c.CorsOrigins.Clear());
            var pre = new HttpRequestMessage(HttpMethod.Options, _host!.BaseUrl + "/v1/chat/completions");
            pre.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:8775");
            Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(pre)).StatusCode);
        }

        public void Dispose()
        {
            try { _host?.Stop(); } catch (Exception) { }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
