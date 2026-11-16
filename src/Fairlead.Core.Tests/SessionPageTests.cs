using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Host;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>The admin page is opened with a single-use launch ticket. The session token is not in the URL.</summary>
    public sealed class SessionPageTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-session-" + Guid.NewGuid().ToString("N"));
        private FairleadHost _host;
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Dispose()
        {
            if (_host != null) _host.Dispose();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Fact]
        public async Task LaunchUrlCarriesATicketAndThePageDoesNotEchoTheSessionToken()
        {
            var info = StartHost();
            var ticket = TicketFrom(info.LaunchUrl, info.Port);
            Assert.True(info.LaunchUrl.IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "The launch URL must not contain the session token.");

            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                var shellResponse = await http.GetAsync(info.LaunchUrl);
                Assert.Equal(HttpStatusCode.OK, shellResponse.StatusCode);
                var shell = await shellResponse.Content.ReadAsStringAsync();
                Assert.Contains("Fairlead", shell);
                Assert.True(shell.IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "The page must not contain the session token.");
                Assert.True(shell.IndexOf(ticket, StringComparison.Ordinal) < 0, "The page must not contain the launch ticket.");
                Assert.True(shellResponse.Headers.CacheControl != null && shellResponse.Headers.CacheControl.NoStore, "The launch page must not be cached.");

                var asset = await http.GetAsync(info.LaunchUrl.TrimEnd('/') + "/assets/app.js");
                Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
                Assert.Equal("console.log(1)", await asset.Content.ReadAsStringAsync());
                Assert.True(asset.Headers.CacheControl != null && asset.Headers.CacheControl.NoStore, "A file fetched through the launch URL must not be cached.");

                var bare = await http.GetStringAsync(info.Url);
                Assert.Contains("Fairlead", bare);
                Assert.True(bare.IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "The bare page must not contain the session token.");

                var wrongChars = ticket.ToCharArray();
                wrongChars[0] = wrongChars[0] == 'a' ? 'b' : 'a';
                var wrong = await http.GetAsync("http://127.0.0.1:" + info.Port + "/s/" + new string(wrongChars) + "/");
                Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
                var wrongBody = await wrong.Content.ReadAsStringAsync();
                Assert.True(wrongBody.IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "A wrong launch path must not echo the session token.");
                Assert.True(wrongBody.IndexOf(ticket, StringComparison.Ordinal) < 0, "A wrong launch path must not echo the launch ticket.");

                var asSession = await http.GetAsync("http://127.0.0.1:" + info.Port + "/s/" + info.SessionToken + "/");
                Assert.Equal(HttpStatusCode.NotFound, asSession.StatusCode);
                Assert.True((await asSession.Content.ReadAsStringAsync()).IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "The session token is not a launch path.");
            }
        }

        [Fact]
        public async Task ALaunchTicketCanBeClaimedOnce()
        {
            var info = StartHost();
            var ticket = TicketFrom(info.LaunchUrl, info.Port);
            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                var first = await Claim(http, info.Port, ticket);
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                var text = await first.Content.ReadAsStringAsync();
                Assert.True(text.IndexOf("\"token\":\"" + info.SessionToken + "\"", StringComparison.Ordinal) >= 0, "The first claim must return the session token.");
                Assert.True(text.IndexOf(ticket, StringComparison.Ordinal) < 0, "The claim response must not repeat the ticket.");

                var authed = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + info.Port + "/api/status");
                authed.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, info.SessionToken);
                var status = await http.SendAsync(authed);
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);

                var second = await Claim(http, info.Port, ticket);
                Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
                Assert.True((await second.Content.ReadAsStringAsync()).IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "A second claim must not return the session token.");

                var again = await http.GetAsync(info.LaunchUrl);
                Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
                Assert.True((await again.Content.ReadAsStringAsync()).IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "A spent ticket must not serve the page.");
            }
        }

        [Fact]
        public async Task AnExpiredLaunchTicketIsRejected()
        {
            var info = StartHost();
            var ticket = TicketFrom(info.LaunchUrl, info.Port);
            _now = _now.AddSeconds(FairleadHost.LaunchTicketSeconds + 1);
            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                var expired = await Claim(http, info.Port, ticket);
                Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
                Assert.True((await expired.Content.ReadAsStringAsync()).IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "An expired ticket must not return the session token.");

                var page = await http.GetAsync(info.LaunchUrl);
                Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
                Assert.True((await page.Content.ReadAsStringAsync()).IndexOf(info.SessionToken, StringComparison.Ordinal) < 0, "An expired ticket must not serve the page.");
            }
        }

        private HostInfo StartHost()
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
                Clock = () => _now,
            });
            return _host.Start();
        }

        private static string TicketFrom(string launchUrl, int port)
        {
            var prefix = "http://127.0.0.1:" + port + "/s/";
            Assert.True(launchUrl.StartsWith(prefix, StringComparison.Ordinal), "The launch URL must be http://127.0.0.1:<port>/s/<ticket>/.");
            Assert.True(launchUrl.EndsWith("/", StringComparison.Ordinal), "The launch URL must end with a slash.");
            Assert.True(launchUrl.IndexOf('?') < 0 && launchUrl.IndexOf('#') < 0, "The launch URL must not carry a query or a hash.");
            var ticket = launchUrl.Substring(prefix.Length, launchUrl.Length - prefix.Length - 1);
            Assert.Equal(48, ticket.Length);
            for (var i = 0; i < ticket.Length; i++)
            {
                var c = ticket[i];
                Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), "A launch ticket is lowercase hex.");
            }
            return ticket;
        }

        private static Task<HttpResponseMessage> Claim(HttpClient http, int port, string ticket)
        {
            var body = new StringContent("{\"ticket\":\"" + ticket + "\"}", Encoding.UTF8, "application/json");
            return http.PostAsync("http://127.0.0.1:" + port + "/api/session/claim", body);
        }
    }
}
