using System;
using System.Collections.Generic;
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
using Fairlead.Core.Telemetry;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>The request log: who, what and which model, kept on disk, read back over the admin API, never with prompt text.</summary>
    public sealed class RequestLogTests : IDisposable
    {
        private const string PromptMarker = "PURPLE-ELEPHANT-7731";
        private const string AnswerMarker = "AMBER-HERON-4412";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-reqlog-" + Guid.NewGuid().ToString("N"));
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly HttpClient _http = new HttpClient();

        private FairleadHost Start(FakeUpstream fake, out string key)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            cfg.FindProvider("ollama")!.BaseUrl = "http://ollama.test:11434";
            cfg.LogContent = true; // even with content logging on, no text may reach the log files
            key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "report-bot", Prefix = key.Substring(0, 7), Hash = GatewayEngine.HashKey(key) });
            var host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") },
                Config = cfg,
                UpstreamHandler = fake,
                Monitor = false,
                PreferredPort = 0,
            });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private async Task<HttpResponseMessage> Chat(FairleadHost host, string? key, string? label, string prompt)
        {
            var body = new JObject { ["model"] = "fast", ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } } };
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/chat/completions") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            if (key != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (label != null) req.Headers.TryAddWithoutValidation(GatewayEngine.LabelHeader, label);
            return await _http.SendAsync(req);
        }

        private async Task<JToken> Admin(FairleadHost host, string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + path);
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
            var r = await _http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.DoesNotContain(PromptMarker, text);
            Assert.DoesNotContain(AnswerMarker, text);
            return JToken.Parse(text);
        }

        private static FakeUpstream Fake()
        {
            return new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer(AnswerMarker)));
        }

        [Fact]
        public async Task LogsWhoWhatAndWhichModelServedWithoutAnyPromptOrAnswerText()
        {
            var host = Start(Fake(), out var key);
            var ok = await Chat(host, key, "nightly-report", PromptMarker);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains(AnswerMarker, await ok.Content.ReadAsStringAsync());

            var files = Directory.GetFiles(Path.Combine(_dir, "logs"), "fairlead-requests-*.jsonl");
            Assert.Single(files);
            var raw = File.ReadAllText(files[0]);
            Assert.DoesNotContain(PromptMarker, raw);
            Assert.DoesNotContain(AnswerMarker, raw);
            Assert.DoesNotContain(key, raw);
            Assert.DoesNotContain(GatewayEngine.HashKey(key), raw);

            var entries = (JArray)await Admin(host, "/api/requestlog");
            var e = Assert.Single(entries);
            Assert.Equal("report-bot", (string?)e["client"]);
            Assert.Equal("k1", (string?)e["clientId"]);
            Assert.Equal(key.Substring(0, 7), (string?)e["keyPrefix"]);
            Assert.Equal("nightly-report", (string?)e["label"]);
            Assert.Equal("/v1/chat/completions", (string?)e["endpoint"]);
            Assert.Equal("fast", (string?)e["route"]);
            Assert.Equal("groq/llama-fast", (string?)e["servedBy"]);
            Assert.Equal(200, (int?)e["status"]);
            Assert.NotNull((int?)e["totalMs"]);
        }

        [Fact]
        public async Task FiltersAndTotalsByClientLabelStatusAndModel()
        {
            var host = Start(Fake(), out var key);
            await Chat(host, key, "job-a", "one");
            await Chat(host, key, "job-a", "two");
            await Chat(host, key, "job-b", "three");
            await Chat(host, null, "job-c", "no key"); // 401, still logged with its label

            Assert.Equal(4, ((JArray)await Admin(host, "/api/requestlog")).Count);
            Assert.Equal(2, ((JArray)await Admin(host, "/api/requestlog?label=job-a")).Count);
            Assert.Equal(3, ((JArray)await Admin(host, "/api/requestlog?client=report-bot")).Count);
            var errors = (JArray)await Admin(host, "/api/requestlog?status=error");
            Assert.Equal(401, (int?)Assert.Single(errors)["status"]);
            Assert.Equal("job-c", (string?)errors[0]["label"]);
            Assert.Equal(3, ((JArray)await Admin(host, "/api/requestlog?status=2xx")).Count);
            Assert.Equal(3, ((JArray)await Admin(host, "/api/requestlog?model=llama-fast")).Count);
            Assert.Equal(2, ((JArray)await Admin(host, "/api/requestlog?limit=2")).Count);
            Assert.Equal(4, ((JArray)await Admin(host, "/api/requestlog?since=1h")).Count);
            Assert.Empty((JArray)await Admin(host, "/api/requestlog?since=2000-01-01&until=2000-01-02"));

            var summary = await Admin(host, "/api/requestlog/summary");
            Assert.Equal(4, (int?)summary["requests"]);
            Assert.Equal(1, (int?)summary["failed"]);
            var byClient = ((JArray)summary["byClient"]!).ToDictionary(x => (string?)x["name"] ?? "", x => (int?)x["requests"]);
            Assert.Equal(3, byClient["report-bot"]);
            Assert.Equal(1, byClient["(no key)"]);
            var byLabel = ((JArray)summary["byLabel"]!).ToDictionary(x => (string?)x["name"] ?? "", x => (int?)x["requests"]);
            Assert.Equal(2, byLabel["job-a"]);
            Assert.Equal(1, byLabel["job-b"]);
            var byModel = ((JArray)summary["byModel"]!).ToDictionary(x => (string?)x["name"] ?? "", x => (int?)x["requests"]);
            Assert.Equal(3, byModel["groq/llama-fast"]);
        }

        [Fact]
        public async Task TheLogSurvivesARestartAndNeedsTheSessionToken()
        {
            var host = Start(Fake(), out var key);
            await Chat(host, key, null, "hi");
            Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync(host.BaseUrl + "/api/requestlog")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync(host.BaseUrl + "/api/requestlog/summary")).StatusCode);
            host.Stop();
            var again = Start(Fake(), out _);
            var entries = (JArray)await Admin(again, "/api/requestlog");
            var e = Assert.Single(entries);
            Assert.Null((string?)e["label"]);
        }

        [Fact]
        public void LabelsAreCleanedAndCapped()
        {
            Assert.Null(RequestLog.CleanLabel(null));
            Assert.Null(RequestLog.CleanLabel("   "));
            Assert.Equal("nightly-report_1", RequestLog.CleanLabel(" nightly-report_1 "));
            Assert.Equal("a__b_c", RequestLog.CleanLabel("a\r\nb<c"));
            Assert.Equal(RequestLog.MaxLabelLength, RequestLog.CleanLabel(new string('x', 500))!.Length);
        }

        [Fact]
        public void ReadsDailyFilesNewestFirstAndSkipsDamagedLines()
        {
            Directory.CreateDirectory(_dir);
            var now = DateTimeOffset.UtcNow;
            File.WriteAllText(RequestLog.FileFor(_dir, now.AddDays(-2)), Line("old", now.AddDays(-2), 200) + "\n");
            File.WriteAllText(RequestLog.FileFor(_dir, now), Line("a", now.AddMinutes(-5), 200) + "\n{ not json\n\n" + Line("b", now.AddMinutes(-1), 500) + "\n");
            var all = RequestLog.Read(_dir, new RequestLogQuery());
            Assert.Equal(new[] { "b", "a", "old" }, all.Select(e => e.Id).ToArray());
            var recent = RequestLog.Read(_dir, new RequestLogQuery { Since = now.AddDays(-1) });
            Assert.Equal(new[] { "b", "a" }, recent.Select(e => e.Id).ToArray());
            Assert.Equal(new[] { "b" }, RequestLog.Read(_dir, new RequestLogQuery { Status = "5xx" }).Select(e => e.Id).ToArray());
            Assert.Equal(new[] { "a" }, RequestLog.Read(_dir, new RequestLogQuery { Status = "200", Since = now.AddDays(-1) }).Select(e => e.Id).ToArray());
            Assert.Empty(RequestLog.Read(Path.Combine(_dir, "missing"), new RequestLogQuery()));
        }

        [Fact]
        public void SinceUnderstandsSpansAndDates()
        {
            var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            Assert.Equal(now.AddMinutes(-90), RequestLog.ParseSince("90m", now));
            Assert.Equal(now.AddHours(-24), RequestLog.ParseSince("24h", now));
            Assert.Equal(now.AddDays(-7), RequestLog.ParseSince("7d", now));
            Assert.Equal(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), RequestLog.ParseSince("2026-10-30", now));
            Assert.Null(RequestLog.ParseSince("", now));
            Assert.Null(RequestLog.ParseSince("soon", now));
        }

        private static string Line(string id, DateTimeOffset time, int status)
        {
            return new JObject { ["id"] = id, ["time"] = time.ToString("o"), ["client"] = "c", ["endpoint"] = "/v1/chat/completions", ["route"] = "fast", ["target"] = "groq/x", ["status"] = status, ["totalMs"] = 10 }.ToString(Formatting.None);
        }

        public void Dispose()
        {
            foreach (var h in _hosts) { try { h.Stop(); } catch (Exception) { } }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
