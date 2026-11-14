using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Host;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>The admin page is opened with the session in the path, so a lost query string still signs the tab in.</summary>
    public sealed class SessionPageTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-session-" + Guid.NewGuid().ToString("N"));
        private FairleadHost? _host;

        public void Dispose()
        {
            if (_host != null) _host.Dispose();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Fact]
        public async Task LaunchUrlKeepsTheTokenInThePathAndAWrongPathIsABare404()
        {
            var web = Path.Combine(_dir, "web");
            Directory.CreateDirectory(Path.Combine(web, "assets"));
            File.WriteAllText(Path.Combine(web, "index.html"), "<!doctype html><title>Fairlead</title><script src=\"assets/app.js\"></script>");
            File.WriteAllText(Path.Combine(web, "assets", "app.js"), "console.log(1)");
            _host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = string.Empty, LogDirectory = Path.Combine(_dir, "logs") },
                Config = FairleadConfig.CreateDefault(),
                WebRoot = web,
                Monitor = false,
                PreferredPort = 0,
                LocalTokenFile = string.Empty,
            });
            var info = _host.Start();
            Assert.Contains("/s/" + info.SessionToken + "/", info.LaunchUrl);
            Assert.Contains("?session=" + info.SessionToken, info.LaunchUrl);
            Assert.Contains("#session=" + info.SessionToken, info.LaunchUrl);

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var shell = await http.GetStringAsync("http://127.0.0.1:" + info.Port + "/s/" + info.SessionToken + "/");
            Assert.Contains("Fairlead", shell);
            Assert.DoesNotContain(info.SessionToken, shell);
            var asset = await http.GetStringAsync("http://127.0.0.1:" + info.Port + "/s/" + info.SessionToken + "/assets/app.js");
            Assert.Equal("console.log(1)", asset);

            var bare = await http.GetStringAsync(info.Url);
            Assert.Contains("Fairlead", bare);
            Assert.DoesNotContain(info.SessionToken, bare);

            var wrongChars = info.SessionToken.ToCharArray();
            wrongChars[0] = wrongChars[0] == 'a' ? 'b' : 'a';
            var wrong = await http.GetAsync("http://127.0.0.1:" + info.Port + "/s/" + new string(wrongChars) + "/");
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
            Assert.DoesNotContain(info.SessionToken, await wrong.Content.ReadAsStringAsync());

        }
    }
}
