using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Host;
using Fairlead.Core.Serialization;
using Fairlead.Core.Telemetry;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>Request-log retention: off by default, by age and by total size, never today's file.</summary>
    public sealed class RequestLogRetentionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-retain-" + Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset _now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        public RequestLogRetentionTests()
        {
            Directory.CreateDirectory(_dir);
        }

        private string Day(int daysAgo, int bytes)
        {
            var path = RequestLog.FileFor(_dir, _now.AddDays(-daysAgo));
            File.WriteAllText(path, new string('x', bytes));
            return Path.GetFileName(path);
        }

        private string[] Names()
        {
            return Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x).ToArray()!;
        }

        [Fact]
        public void WithNoLimitNothingIsRemoved()
        {
            Day(0, 10); Day(1, 10); Day(400, 10);
            var r = RequestLog.Prune(_dir, 0, 0, _now, false);
            Assert.Empty(r.Deleted);
            Assert.Equal(3, Names().Length);
            Assert.Equal(3, r.KeptFiles);
        }

        [Fact]
        public void FilesOlderThanTheMaxAgeAreRemoved_TodayAndRecentOnesStay()
        {
            var today = Day(0, 10); var d3 = Day(3, 10); var d7 = Day(7, 10); var d8 = Day(8, 10); var d30 = Day(30, 10);
            File.WriteAllText(Path.Combine(_dir, "notes.txt"), "keep");
            File.WriteAllText(Path.Combine(_dir, "fairlead-requests-latest.jsonl"), "keep"); // not a dated log file
            var r = RequestLog.Prune(_dir, 7, 0, _now, false);
            Assert.Equal(new[] { d30, d8 }.OrderBy(x => x), r.Deleted.OrderBy(x => x));
            Assert.Equal(20, r.FreedBytes);
            Assert.Equal(new[] { "fairlead-requests-latest.jsonl", today, d3, d7, "notes.txt" }.OrderBy(x => x), Names().OrderBy(x => x));
            Assert.Equal(3, r.KeptFiles);
        }

        [Fact]
        public void TheSizeLimitRemovesTheOldestFirstAndNeverToday()
        {
            var today = Day(0, 500); var d1 = Day(1, 400); var d2 = Day(2, 300); var d3 = Day(3, 200);
            var r = RequestLog.Prune(_dir, 0, 950, _now, false);
            Assert.Equal(new[] { d3, d2 }, r.Deleted.ToArray());
            Assert.Equal(new[] { today, d1 }.OrderBy(x => x), Names().OrderBy(x => x));
            Assert.Equal(900, r.KeptBytes);
            // A limit below today's own size still keeps today's file.
            var again = RequestLog.Prune(_dir, 0, 100, _now, false);
            Assert.Equal(new[] { d1 }, again.Deleted.ToArray());
            Assert.Equal(new[] { today }, Names());
        }

        [Fact]
        public void ADryRunReportsAndDeletesNothing()
        {
            Day(0, 10); Day(20, 10);
            var r = RequestLog.Prune(_dir, 7, 0, _now, true);
            Assert.True(r.DryRun);
            Assert.Single(r.Deleted);
            Assert.Equal(2, Names().Length);
        }

        [Fact]
        public void RetainPrunesAtStartAndWhenTheFirstRecordOfADayArrives()
        {
            var oldPath = RequestLog.FileFor(_dir, DateTimeOffset.UtcNow.AddDays(-40)); // 40 days before the real today
            File.WriteAllText(oldPath, "x");
            var old = Path.GetFileName(oldPath);
            var metrics = new Metrics(_dir);
            Assert.Equal(1, Names().Length);
            metrics.Retain(0, 0); // off: nothing happens
            Assert.Equal(1, Names().Length);
            metrics.Retain(7, 0);
            Assert.Empty(Names().Where(n => n == old));
            Assert.Equal(7, metrics.MaxAgeDays);
            // A record written later in the same run still lands in today's file.
            metrics.Record(new RequestRecord { Id = "r1", Status = 200, Endpoint = "/v1/chat/completions" });
            Assert.Single(Names());
        }

        [Fact]
        public async Task TheAdminRouteDeletesOnRequestAndExplainsWhenNoLimitIsGiven()
        {
            var logs = Path.Combine(_dir, "logs");
            Directory.CreateDirectory(logs);
            var today = DateTimeOffset.UtcNow;
            File.WriteAllText(RequestLog.FileFor(logs, today), "{}\n");
            File.WriteAllText(RequestLog.FileFor(logs, today.AddDays(-30)), "{}\n");
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = logs }, Config = RoutingTests.Config(), UpstreamHandler = new FakeUpstream(), Monitor = false, PreferredPort = 0 });
            host.Start();
            try
            {
                using (var http = new HttpClient())
                {
                    Func<string, Task<HttpResponseMessage>> post = async body =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/api/requestlog/prune") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                        req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
                        return await http.SendAsync(req);
                    };
                    Assert.Equal(HttpStatusCode.BadRequest, (await post("{}")).StatusCode);
                    var unauth = await http.PostAsync(host.BaseUrl + "/api/requestlog/prune", new StringContent("{\"maxDays\":7}", Encoding.UTF8, "application/json"));
                    Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
                    var dry = JObject.Parse(await (await post("{\"maxDays\":7,\"dryRun\":true}")).Content.ReadAsStringAsync());
                    Assert.Equal(1, ((JArray)dry["deleted"]!).Count);
                    Assert.Equal(2, Directory.GetFiles(logs, "fairlead-requests-*.jsonl").Length);
                    var real = JObject.Parse(await (await post("{\"maxDays\":7}")).Content.ReadAsStringAsync());
                    Assert.Equal(1, ((JArray)real["deleted"]!).Count);
                    Assert.Equal(1, (int?)real["keptFiles"]);
                    Assert.Single(Directory.GetFiles(logs, "fairlead-requests-*.jsonl"));
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
