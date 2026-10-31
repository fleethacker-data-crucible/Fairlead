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
    public sealed class DetectTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-detect-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public async Task DetectionReportsEachAddressOnce()
        {
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = RoutingTests.Config(), UpstreamHandler = new FakeUpstream(), Monitor = false, PreferredPort = 0 });
            host.Start();
            try
            {
                using (var http = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/api/detect");
                    req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
                    var r = await http.SendAsync(req);
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                    var list = (JArray)JToken.Parse(await r.Content.ReadAsStringAsync());
                    var urls = list.Select(x => (string?)x["baseUrl"] ?? "").ToList();
                    Assert.Equal(urls.Count, urls.Distinct().Count());
                    var shared = list.Single(x => ((string?)x["baseUrl"])!.Contains(":1234"));
                    Assert.Equal("phoenix-lai", (string?)shared["preset"]);
                    Assert.Equal("phoenix-lai or LM Studio", (string?)shared["name"]);
                    Assert.Equal("lmstudio", (string?)shared["alsoPreset"]![0]);
                }
            }
            finally { host.Stop(); }
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
