using System;
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
    /// <summary>The gateway accepts the local token from this machine, names the caller by X-Fairlead-Label, and still refuses unknown keys.</summary>
    public sealed class LocalTokenGatewayTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-localtoken-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private FairleadHost? _host;

        private void Start(string? tokenFile)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            cfg.RequireClientKey = true;
            var fake = new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("ok")));
            _host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0, LocalTokenFile = tokenFile });
            _host.Start();
        }

        private async Task<HttpStatusCode> Call(string? key, string? app = null)
        {
            var body = new JObject { ["model"] = "groq/llama-fast", ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } } };
            var req = new HttpRequestMessage(HttpMethod.Post, _host!.BaseUrl + "/v1/chat/completions") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            if (key != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (app != null) req.Headers.TryAddWithoutValidation(GatewayEngine.LabelHeader, app);
            return (await _http.SendAsync(req)).StatusCode;
        }

        [Fact]
        public async Task TheLocalTokenIsAcceptedAndOtherKeysAreNot()
        {
            var file = Path.Combine(_dir, "local-token");
            Start(file);
            var local = LocalToken.Read(file);
            Assert.NotNull(local);
            Assert.Equal(HttpStatusCode.OK, await Call(local, "Semaphore"));
            Assert.Equal(HttpStatusCode.OK, await Call(local));
            Assert.Equal(HttpStatusCode.Unauthorized, await Call(GatewayEngine.NewClientKey(), "Semaphore"));
            Assert.Equal(HttpStatusCode.Unauthorized, await Call(null));
        }

        [Fact]
        public async Task ATestHostHasNoLocalTokenUnlessGivenOne()
        {
            Start(null);
            Assert.Equal(string.Empty, _host!.LocalTokenFile);
            Assert.Equal(HttpStatusCode.Unauthorized, await Call(null));
        }

        public void Dispose()
        {
            _host?.Dispose();
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
