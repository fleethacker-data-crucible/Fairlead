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
    public sealed class CommandCatalogTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-cmds-" + Guid.NewGuid().ToString("N"));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "fairlead.ps1"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact]
        public void ParsesTheHelpTextIntoCommands()
        {
            var help = "fairlead <command>\n\nEngine\n  stop --yes                          stop the engine            POST /api/session/stop\n  status                              engine state   GET /api/status\n\nProviders\n  providers set <id> [--preset P]\n                [--key-stdin]   create or change\n\nOptions for every command: --json\n";
            var list = CommandCatalog.FromHelp(help);
            Assert.Equal(new[] { "stop", "status", "providers" }, list.Select(c => c.Name).ToArray());
            Assert.True(list[0].Destructive);
            Assert.False(list[1].Destructive);
            Assert.Equal(new[] { "POST /api/session/stop" }, list[0].Routes.ToArray());
            Assert.Equal("stop the engine", list[0].Description);
            Assert.Equal("Providers", list[2].Group);
            Assert.Contains("[--key-stdin]", list[2].Usage + " " + list[2].Description);
        }

        [Fact]
        public async Task TheApiServesTheCommandsOfTheRealScript()
        {
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = RepoRoot(), DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = RoutingTests.Config(), UpstreamHandler = new FakeUpstream(), Monitor = false, PreferredPort = 0 });
            host.Start();
            try
            {
                using (var http = new HttpClient())
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(host.BaseUrl + "/api/commands")).StatusCode);
                    var req = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/api/commands");
                    req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
                    var r = await http.SendAsync(req);
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                    var doc = JObject.Parse(await r.Content.ReadAsStringAsync());
                    Assert.Equal(1, (int?)doc["schemaVersion"]);
                    Assert.Equal("fairlead", (string?)doc["tool"]);
                    var commands = ((JArray)doc["commands"]!).ToList();
                    Assert.True(commands.Select(c => (string?)c["name"]).Distinct().Count() > 20);
                    var stop = commands.First(c => (string?)c["name"] == "stop");
                    Assert.True((bool?)stop["destructive"]);
                    var status = commands.First(c => (string?)c["name"] == "status");
                    Assert.Equal("GET /api/status", (string?)status["routes"]![0]);
                    Assert.False((bool?)status["destructive"]);
                }
            }
            finally { host.Stop(); }
        }

        [Fact]
        public async Task WithoutTheScriptTheRouteSaysSo()
        {
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = RoutingTests.Config(), UpstreamHandler = new FakeUpstream(), Monitor = false, PreferredPort = 0 });
            host.Start();
            try
            {
                using (var http = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/api/commands");
                    req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
                    Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(req)).StatusCode);
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
