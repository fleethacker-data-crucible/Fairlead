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
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>POST /v1/completions through the gateway, against a fake provider only.</summary>
    public sealed class LegacyCompletionsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-completions-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private FairleadHost? _host;
        private string _key = "";
        private FakeUpstream? _fake;

        private void Start(FakeUpstream fake)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            _key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "autocomplete", Prefix = _key.Substring(0, 7), Hash = GatewayEngine.HashKey(_key) });
            _fake = fake;
            _host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            _host.Start();
        }

        private async Task<HttpResponseMessage> Complete(string json, bool withKey = true)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _host!.BaseUrl + "/v1/completions") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (withKey) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key);
            return await _http.SendAsync(req);
        }

        [Fact]
        public void TheRequestBecomesAChatRequestAndTheAnswerAText()
        {
            string error;
            var chat = LegacyCompletions.ToChat(JObject.Parse("{\"model\":\"fast\",\"prompt\":\"def add(a, b):\",\"max_tokens\":32,\"temperature\":0.2,\"stop\":[\"\\n\\n\"],\"unknown\":1}"), out error)!;
            Assert.Null(error);
            Assert.Equal("user", (string?)chat["messages"]![0]!["role"]);
            Assert.Equal("def add(a, b):", (string?)chat["messages"]![0]!["content"]);
            Assert.Equal(32, (int?)chat["max_tokens"]);
            Assert.Equal("\n\n", (string?)chat["stop"]![0]);
            Assert.Null(chat["unknown"]);
            var text = LegacyCompletions.FromChat(FakeUpstream.OpenAiAnswer("    return a + b"));
            Assert.Equal("text_completion", (string?)text["object"]);
            Assert.Equal("    return a + b", (string?)text["choices"]![0]!["text"]);
        }

        [Fact]
        public async Task ACompletionRequestIsServedThroughTheGateway()
        {
            Start(new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("    return a + b"))));
            var r = await Complete("{\"model\":\"groq/llama-fast\",\"prompt\":[\"def add(a, b):\"],\"max_tokens\":16,\"temperature\":0}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("groq/llama-fast", r.Headers.GetValues("X-Fairlead-Target").Single());
            var o = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal("text_completion", (string?)o["object"]);
            Assert.Equal("    return a + b", (string?)o["choices"]![0]!["text"]);
            Assert.Equal("stop", (string?)o["choices"]![0]!["finish_reason"]);
            var seen = _fake!.Seen.Single();
            Assert.EndsWith("/chat/completions", seen.Key);
            Assert.Equal("llama-fast", (string?)seen.Value["model"]);
            Assert.Equal("def add(a, b):", (string?)seen.Value["messages"]![0]!["content"]);
            Assert.Equal(16, (int?)seen.Value["max_tokens"]);
            var log = string.Join("\n", Directory.GetFiles(Path.Combine(_dir, "logs"), "fairlead-requests-*.jsonl").Select(File.ReadAllText));
            Assert.Contains("/v1/completions", log);
            Assert.Contains("autocomplete", log);
            Assert.DoesNotContain("def add", log);
        }

        [Fact]
        public async Task StreamingComesBackAsCompletionChunks()
        {
            Start(new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Sse(FakeUpstream.OpenAiChunk("    ret"), FakeUpstream.OpenAiChunk("urn a + b"), FakeUpstream.OpenAiFinish(), "data: [DONE]\n\n")));
            var r = await Complete("{\"model\":\"groq/llama-fast\",\"prompt\":\"def add(a, b):\",\"stream\":true}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var wire = await r.Content.ReadAsStringAsync();
            Assert.EndsWith("data: [DONE]\n\n", wire);
            var chunks = wire.Split('\n').Where(l => l.StartsWith("data: {", StringComparison.Ordinal)).Select(l => JObject.Parse(l.Substring(6))).ToList();
            Assert.All(chunks, c => Assert.Equal("text_completion", (string?)c["object"]));
            Assert.Equal("    return a + b", string.Concat(chunks.Select(c => (string?)c["choices"]![0]!["text"])));
            Assert.Equal("stop", (string?)chunks.Last()["choices"]![0]!["finish_reason"]);
        }

        [Fact]
        public async Task WhatCannotBeServedIsAClear400_AndTheKeyIsStillRequired()
        {
            Start(new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, FakeUpstream.OpenAiAnswer("x"))));
            foreach (var bad in new[] { "{\"model\":\"fast\"}", "{\"model\":\"fast\",\"prompt\":[\"a\",\"b\"]}", "{\"model\":\"fast\",\"prompt\":\"a\",\"n\":2}", "{\"model\":\"fast\",\"prompt\":\"a\",\"echo\":true}", "{\"model\":\"fast\",\"prompt\":\"a\",\"suffix\":\"b\"}", "not json" })
            {
                var r = await Complete(bad);
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
                Assert.Contains("\"error\"", await r.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await Complete("{\"model\":\"fast\",\"prompt\":\"a\"}", withKey: false)).StatusCode);
            Assert.Empty(_fake!.Seen);
        }

        public void Dispose()
        {
            try { _host?.Stop(); } catch (Exception) { }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
