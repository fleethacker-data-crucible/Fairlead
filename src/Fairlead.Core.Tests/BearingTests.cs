using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fairlead.Core.Bearing;
using Fairlead.Core.Config;
using Fairlead.Core.Gateway;
using Fairlead.Core.Host;
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>Cited answers (Bearing) over a small synthetic folder of documents, offline.</summary>
    public sealed class BearingTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-bearing-" + Guid.NewGuid().ToString("N"));
        private readonly string _docs;
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly HttpClient _http = new HttpClient();

        public BearingTests()
        {
            _docs = Path.Combine(_dir, "docs");
            Directory.CreateDirectory(Path.Combine(_docs, "policies"));
            Directory.CreateDirectory(Path.Combine(_docs, ".hidden"));
            File.WriteAllText(Path.Combine(_docs, "policies", "leave-policy.md"),
                "# Leave policy\n\nFull-time employees earn 20 days of annual leave per year.\n\nContractors receive 10 days of paid leave per year, taken with manager approval.\n\n# Remote work\n\nStaff may work remotely up to three days per week.\n");
            File.WriteAllText(Path.Combine(_docs, "server-room.txt"), "The server room badge reader resets every Sunday at midnight.\r\nCall the facilities desk when it fails.\r\n");
            File.WriteAllText(Path.Combine(_docs, "ignored.bin"), "leave policy in a binary file");
            File.WriteAllText(Path.Combine(_docs, ".hidden", "secret.md"), "The secret leave policy for contractors is 99 days.");
        }

        // ---------- the index ----------

        [Fact]
        public void FindsThePassageAndSaysWhereItIs()
        {
            var index = new BearingIndex(_docs);
            var hits = index.Search("How many days of leave do contractors get?", 3);
            Assert.NotEmpty(hits);
            var top = hits[0].Passage;
            Assert.Equal("policies/leave-policy.md", top.File);
            Assert.Equal("Leave policy", top.Heading);
            Assert.Equal(1, top.FromLine);
            Assert.Equal(5, top.ToLine);
            Assert.Equal("lines 1-5", top.Location);
            Assert.StartsWith("file:///", top.Link);
            Assert.EndsWith("leave-policy.md#L1-L5", top.Link);
            Assert.Equal("server-room.txt", index.Search("when does the badge reader reset", 3)[0].Passage.File);
        }

        [Fact]
        public void IndexesOnlyTextFilesInsideTheFolderAndIgnoresHiddenOnes()
        {
            var index = new BearingIndex(_docs);
            Assert.Empty(index.Search("binary file", 3));
            Assert.Empty(index.Search("secret 99 days", 3));
            index.Refresh(true);
            Assert.Equal(2, index.Files);
        }

        [Fact]
        public void SomethingUnrelatedFindsNothing()
        {
            Assert.Empty(new BearingIndex(_docs).Search("What is the capital of Australia?", 4));
            Assert.Empty(new BearingIndex(Path.Combine(_dir, "missing")).Search("leave", 4));
        }

        [Fact]
        public void PicksUpNewAndChangedFiles()
        {
            var index = new BearingIndex(_docs);
            Assert.Empty(index.Search("parking permit renewal", 3));
            File.WriteAllText(Path.Combine(_docs, "parking.md"), "Parking permit renewal happens every January.\n");
            index.Refresh(true);
            Assert.Equal("parking.md", index.Search("parking permit renewal", 3)[0].Passage.File);
        }

        [Fact]
        public void LongLinesAreSplitAndLineNumbersSurvive()
        {
            var text = "intro line\n\n" + string.Join(" ", Enumerable.Repeat("word", 400)) + "\n";
            var passages = BearingIndex.Split("x.txt", "/x.txt", text);
            Assert.True(passages.Count >= 3);
            Assert.All(passages.Skip(1), p => Assert.Equal(3, p.FromLine));
            Assert.All(passages, p => Assert.True(p.Text.Length <= 900));
        }

        // ---------- the request and the answer ----------

        [Fact]
        public void AddsNumberedSourcesToAnOpenAiRequest()
        {
            var service = new BearingService(_docs);
            var body = JObject.Parse("{\"model\":\"m\",\"messages\":[{\"role\":\"system\",\"content\":\"Be brief.\"},{\"role\":\"user\",\"content\":\"How many days of leave do contractors get?\"}]}");
            string error;
            var run = service.Prepare(body, Dialect.OpenAi, 4, out error);
            Assert.Null(error);
            Assert.False(run!.NoSources);
            var messages = (JArray)body["messages"]!;
            Assert.Equal(3, messages.Count);
            Assert.Equal("system", (string?)messages[0]["role"]);
            Assert.Contains("[1] policies/leave-policy.md (lines 1-5)", (string?)messages[0]["content"]);
            Assert.Contains("Contractors receive 10 days", (string?)messages[0]["content"]);
            Assert.Equal("Be brief.", (string?)messages[1]["content"]);
            Assert.Equal("How many days of leave do contractors get?", (string?)messages[2]["content"]);
        }

        [Fact]
        public void AddsNumberedSourcesToAnAnthropicRequestAndKeepsItsOwnSystemText()
        {
            var service = new BearingService(_docs);
            var body = JObject.Parse("{\"model\":\"m\",\"system\":\"Be brief.\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"How many days of leave do contractors get?\"}]}]}");
            string error;
            var run = service.Prepare(body, Dialect.Anthropic, 4, out error);
            Assert.False(run!.NoSources);
            var system = (string?)body["system"]!;
            Assert.Contains("[1] policies/leave-policy.md", system);
            Assert.EndsWith("Be brief.", system);
            Assert.Equal(1, ((JArray)body["messages"]!).Count);
        }

        [Fact]
        public void CitationCarriesSourceLocationQuoteAndLink_AndChecksTheQuote()
        {
            var run = RunFor("How many days of leave do contractors get?");
            var output = FakeUpstream.OpenAiAnswer("Contractors get \"10 days of paid leave per year\" [1]. They also get a pony [4].");
            run.Apply(output, Dialect.OpenAi);
            var c = Assert.Single((JArray)output["fairlead_citations"]!);
            Assert.Equal(1, (int?)c["n"]);
            Assert.Equal("policies/leave-policy.md", (string?)c["source"]);
            Assert.Equal("lines 1-5", (string?)c["location"]);
            Assert.True((bool?)c["verified"]);
            Assert.Equal("10 days of paid leave per year", (string?)c["quote"]);
            Assert.EndsWith("#L1-L5", (string?)c["link"]);
            Assert.Equal(4, (int?)output["fairlead_bearing"]!["unknownSources"]![0]);
            var text = (string?)output["choices"]![0]!["message"]!["content"];
            Assert.Contains("\n\nSources:\n[1] policies/leave-policy.md (lines 1-5): \"10 days of paid leave per year\" file:///", text);
        }

        [Fact]
        public void AQuoteThatIsNotInTheSourceIsNotCalledVerified()
        {
            var run = RunFor("How many days of leave do contractors get?");
            var output = FakeUpstream.OpenAiAnswer("Contractors get \"twelve weeks of sabbatical each year\" [1].");
            run.Apply(output, Dialect.OpenAi);
            var c = Assert.Single((JArray)output["fairlead_citations"]!);
            Assert.False((bool?)c["verified"]);
            Assert.Contains("Contractors receive 10 days", (string?)c["quote"]); // the passage's own closest sentence, not the model's words
        }

        [Fact]
        public void AnAnswerWithoutMarkersGetsNoCitationsAndNoSourcesList()
        {
            var run = RunFor("How many days of leave do contractors get?");
            var output = FakeUpstream.OpenAiAnswer("Ten.");
            run.Apply(output, Dialect.OpenAi);
            Assert.Empty((JArray)output["fairlead_citations"]!);
            Assert.Equal(0, (int?)output["fairlead_bearing"]!["cited"]);
            Assert.Equal("Ten.", (string?)output["choices"]![0]!["message"]!["content"]);
        }

        [Fact]
        public void AnthropicAnswersAreAnnotatedToo()
        {
            var run = RunFor("How many days of leave do contractors get?");
            var output = FakeUpstream.AnthropicAnswer("Contractors get \"10 days of paid leave per year\" [1, 2].");
            run.Apply(output, Dialect.Anthropic);
            Assert.Equal(1, ((JArray)output["fairlead_citations"]!).Count);
            Assert.Equal(2, (int?)output["fairlead_bearing"]!["unknownSources"]![0]);
            Assert.Contains("Sources:", (string?)output["content"]![0]!["text"]);
        }

        private BearingRun RunFor(string question)
        {
            string error;
            var body = new JObject { ["model"] = "m", ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = question } } };
            var run = new BearingService(_docs).Prepare(body, Dialect.OpenAi, 4, out error)!;
            Assert.Single(run.Hits.Select(h => h.Passage.File).Distinct());
            return run;
        }

        // ---------- through the gateway ----------

        private FairleadHost Start(FakeUpstream fake, string? bearingDir, out string key)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            cfg.FindProvider("ollama")!.BaseUrl = "http://ollama.test:11434";
            key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = key.Substring(0, 7), Hash = GatewayEngine.HashKey(key) });
            var host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs"), BearingDirectory = bearingDir },
                Config = cfg,
                UpstreamHandler = fake,
                Monitor = false,
                PreferredPort = 0,
            });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private async Task<HttpResponseMessage> Ask(FairleadHost host, string key, string path, string question, bool cite, bool stream = false, bool anthropic = false)
        {
            var body = new JObject { ["model"] = "fast", ["stream"] = stream, ["max_tokens"] = 100, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = question } } };
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + path) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (cite) req.Headers.TryAddWithoutValidation(BearingService.OptInHeader, "cite");
            return await _http.SendAsync(req);
        }

        private static FakeUpstream Local(List<JObject> seen, string answer)
        {
            return new FakeUpstream().On("ollama.test", (r, b) => { seen.Add(b); return FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer(answer)); })
                .On("groq.test", (r, b) => { seen.Add(b); return FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("cloud")); });
        }

        [Fact]
        public async Task EndToEnd_AnswerCitesItsSourceAndTheDocumentsStayOnLocalModels()
        {
            var seen = new List<JObject>();
            var host = Start(Local(seen, "Contractors receive \"10 days of paid leave per year\" [1]."), _docs, out var key);
            var r = await Ask(host, key, "/v1/chat/completions", "How many days of leave do contractors get?", true);
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("ollama/tiny", r.Headers.GetValues("X-Fairlead-Target").Single()); // the cloud target was skipped
            Assert.Equal("sources=1;cited=1", r.Headers.GetValues(BearingService.OptInHeader).Single());
            var json = JObject.Parse(text);
            var c = Assert.Single((JArray)json["fairlead_citations"]!);
            Assert.Equal("policies/leave-policy.md", (string?)c["source"]);
            Assert.Equal("lines 1-5", (string?)c["location"]);
            Assert.True((bool?)c["verified"]);
            Assert.Contains("Sources:", (string?)json["choices"]![0]!["message"]!["content"]);
            var upstream = Assert.Single(seen);
            Assert.Equal("system", (string?)upstream["messages"]![0]!["role"]);
            Assert.Contains("[1] policies/leave-policy.md", (string?)upstream["messages"]![0]!["content"]);
        }

        [Fact]
        public async Task EndToEnd_AnthropicClientsGetCitationsToo()
        {
            var seen = new List<JObject>();
            var host = Start(Local(seen, "Contractors receive \"10 days of paid leave per year\" [1]."), _docs, out var key);
            var r = await Ask(host, key, "/v1/messages", "How many days of leave do contractors get?", true);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var json = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal("message", (string?)json["type"]);
            Assert.Equal(1, ((JArray)json["fairlead_citations"]!).Count);
            Assert.Contains("Sources:", (string?)json["content"]![0]!["text"]);
        }

        [Fact]
        public async Task WithoutTheHeaderNothingChanges()
        {
            var seen = new List<JObject>();
            var host = Start(Local(seen, "plain answer"), _docs, out var key);
            var r = await Ask(host, key, "/v1/chat/completions", "How many days of leave do contractors get?", false);
            var json = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Null(json["fairlead_citations"]);
            Assert.False(r.Headers.Contains(BearingService.OptInHeader));
            var messages = (JArray)Assert.Single(seen)["messages"]!;
            Assert.Equal(1, messages.Count);
        }

        [Fact]
        public async Task OptingInWithoutAFolderIsAClearError()
        {
            var seen = new List<JObject>();
            var off = Start(Local(seen, "x"), null, out var key1);
            var r1 = await Ask(off, key1, "/v1/chat/completions", "leave days", true);
            Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);
            Assert.Contains("FAIRLEAD_BEARING_DIR", await r1.Content.ReadAsStringAsync());
            var r2 = await Ask(off, key1, "/v1/chat/completions", "leave days", true, stream: true);
            Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
            Assert.Empty(seen);
        }

        private static FakeUpstream LocalStream(List<JObject> seen, params string[] pieces)
        {
            var events = pieces.Select(FakeUpstream.OpenAiChunk).Concat(new[] { FakeUpstream.OpenAiFinish(), "data: [DONE]\n\n" }).ToArray();
            return new FakeUpstream().On("ollama.test", (r, b) => { seen.Add(b); return FakeUpstream.Sse(events); });
        }

        private static List<JObject> DataEvents(string wire)
        {
            return wire.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(e => e.Split('\n')).Where(l => l.StartsWith("data: {", StringComparison.Ordinal)).Select(l => JObject.Parse(l.Substring(6))).ToList();
        }

        [Fact]
        public async Task StreamingCitesAfterTheText_OpenAiClient()
        {
            var seen = new List<JObject>();
            var host = Start(LocalStream(seen, "Contractors receive \"10 days of ", "paid leave per year\" ", "[1]."), _docs, out var key);
            var r = await Ask(host, key, "/v1/chat/completions", "How many days of leave do contractors get?", true, stream: true);
            var wire = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("ollama/tiny", r.Headers.GetValues("X-Fairlead-Target").Single());
            Assert.Equal("sources=1;citations=at-end", r.Headers.GetValues(BearingService.OptInHeader).Single());
            Assert.EndsWith("data: [DONE]\n\n", wire);
            var events = DataEvents(wire);
            var text = string.Concat(events.Where(e => e["choices"] != null && ((JArray)e["choices"]!).Count > 0).Select(e => (string?)e["choices"]![0]!["delta"]!["content"]));
            Assert.Equal("Contractors receive \"10 days of paid leave per year\" [1].", text);
            var last = events.Last();
            Assert.Empty((JArray)last["choices"]!);
            var c = Assert.Single((JArray)last["fairlead_citations"]!);
            Assert.Equal("policies/leave-policy.md", (string?)c["source"]);
            Assert.Equal("lines 1-5", (string?)c["location"]);
            Assert.True((bool?)c["verified"]);
            Assert.Equal("10 days of paid leave per year", (string?)c["quote"]);
            Assert.Equal(1, (int?)last["fairlead_bearing"]!["cited"]);
            Assert.True(wire.LastIndexOf("fairlead_citations", StringComparison.Ordinal) < wire.LastIndexOf("[DONE]", StringComparison.Ordinal));
            Assert.Equal(true, (bool?)Assert.Single(seen)["stream"]);
            Assert.Contains("[1] policies/leave-policy.md", (string?)seen[0]["messages"]![0]!["content"]);
        }

        [Fact]
        public async Task StreamingCitesAfterTheText_AnthropicClient()
        {
            var seen = new List<JObject>();
            var host = Start(LocalStream(seen, "Contractors get \"10 days of paid leave per year\" [1]."), _docs, out var key);
            var r = await Ask(host, key, "/v1/messages", "How many days of leave do contractors get?", true, stream: true);
            var wire = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var cite = wire.IndexOf("event: fairlead_citations", StringComparison.Ordinal);
            Assert.True(cite > 0, wire);
            Assert.True(cite < wire.IndexOf("event: message_stop", StringComparison.Ordinal));
            Assert.True(wire.IndexOf("event: content_block_delta", StringComparison.Ordinal) < cite);
            var payload = DataEvents(wire).Single(e => (string?)e["type"] == "fairlead_citations");
            Assert.Equal("policies/leave-policy.md", (string?)payload["fairlead_citations"]![0]!["source"]);
            Assert.True((bool?)payload["fairlead_citations"]![0]!["verified"]);
        }

        [Fact]
        public async Task StreamingWithNothingFoundStillStreamsAndCallsNoModel()
        {
            var seen = new List<JObject>();
            var host = Start(LocalStream(seen, "invented"), _docs, out var key);
            var r = await Ask(host, key, "/v1/chat/completions", "What is the capital of Australia?", true, stream: true);
            var wire = await r.Content.ReadAsStringAsync();
            Assert.Equal("text/event-stream", r.Content.Headers.ContentType!.MediaType);
            Assert.Contains("found nothing", wire);
            Assert.EndsWith("data: [DONE]\n\n", wire);
            Assert.Empty((JArray)DataEvents(wire).Last()["fairlead_citations"]!);
            var a = await Ask(host, key, "/v1/messages", "What is the capital of Australia?", true, stream: true);
            var awire = await a.Content.ReadAsStringAsync();
            Assert.Contains("event: fairlead_citations", awire);
            Assert.Contains("event: message_stop", awire);
            Assert.Empty(seen);
        }

        [Fact]
        public async Task WhenNothingMatchesNoModelIsCalled()
        {
            var seen = new List<JObject>();
            var host = Start(Local(seen, "an invented answer"), _docs, out var key);
            var r = await Ask(host, key, "/v1/chat/completions", "What is the capital of Australia?", true);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var json = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Contains("found nothing", (string?)json["choices"]![0]!["message"]!["content"]);
            Assert.Empty((JArray)json["fairlead_citations"]!);
            Assert.Empty(seen);
            Assert.Equal("sources=0", r.Headers.GetValues(BearingService.OptInHeader).Single());
        }

        [Fact]
        public async Task AdminApiShowsTheIndexAndWhatAQuestionWouldFind()
        {
            var host = Start(new FakeUpstream(), _docs, out _);
            var status = JObject.Parse(await Admin(host, "/api/bearing"));
            Assert.True((bool?)status["enabled"]);
            Assert.Equal(2, (int?)status["files"]);
            var found = JArray.Parse(await Admin(host, "/api/bearing/search?q=contractors+leave&limit=2"));
            Assert.Equal("policies/leave-policy.md", (string?)found[0]["source"]);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync(host.BaseUrl + "/api/bearing")).StatusCode);
            var off = Start(new FakeUpstream(), null, out _);
            Assert.False((bool?)JObject.Parse(await Admin(off, "/api/bearing"))["enabled"]);
        }

        private async Task<string> Admin(FairleadHost host, string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + path);
            req.Headers.TryAddWithoutValidation(FairleadHost.SessionHeader, host.SessionToken);
            var r = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        public void Dispose()
        {
            foreach (var h in _hosts) { try { h.Stop(); } catch (Exception) { } }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
