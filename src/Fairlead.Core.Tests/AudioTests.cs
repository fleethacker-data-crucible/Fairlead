using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Gateway;
using Fairlead.Core.Host;
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>/v1/audio/speech and /v1/audio/transcriptions through the gateway, against a fake provider only.</summary>
    public sealed class AudioTests : IDisposable
    {
        private static readonly byte[] Speech = { 0x49, 0x44, 0x33, 0x00, 0xFF, 0xFB, 0x90, 0x0D, 0x0A, 0x2D, 0x2D, 0x42, 0x4F, 0x55 }; // includes CR LF and "--BOU"
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-audio-" + Guid.NewGuid().ToString("N"));
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly HttpClient _http = new HttpClient();

        private FairleadHost Start(FakeUpstream fake, out string key)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            cfg.FindProvider("ollama")!.BaseUrl = "http://ollama.test:11434";
            key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "voice-app", Prefix = key.Substring(0, 7), Hash = GatewayEngine.HashKey(key) });
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private async Task<HttpResponseMessage> Speak(FairleadHost host, string? key, string model, string input, string? privacy = null)
        {
            var body = new JObject { ["model"] = model, ["input"] = input, ["voice"] = "alloy" };
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/audio/speech") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            if (key != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (privacy != null) req.Headers.TryAddWithoutValidation(GatewayEngine.PrivacyHeader, privacy);
            return await _http.SendAsync(req);
        }

        private static byte[] WavBytes()
        {
            return new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x0D, 0x0A, 0x2D, 0x2D, 0xFF, 0xFE, 0x00, 0x57, 0x41, 0x56, 0x45 };
        }

        private async Task<HttpResponseMessage> Transcribe(FairleadHost host, string? key, string model, bool withFile = true)
        {
            var form = new MultipartFormDataContent("fl-test-boundary");
            if (withFile)
            {
                var file = new ByteArrayContent(WavBytes());
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "clip.wav");
            }
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("en"), "language");
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/audio/transcriptions") { Content = form };
            if (key != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return await _http.SendAsync(req);
        }

        [Fact]
        public async Task SpeechComesBackAsTheUpstreamsAudioWithTheTargetModelNamed()
        {
            var fake = new FakeUpstream().On("groq.test", (r, b) =>
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Speech) };
                res.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                return res;
            });
            var host = Start(fake, out var key);
            var r = await Speak(host, key, "fast", "Hello from the fleet.");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("audio/mpeg", r.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Speech, await r.Content.ReadAsByteArrayAsync());
            Assert.Equal("groq/llama-fast", r.Headers.GetValues("X-Fairlead-Target").Single());
            var seen = fake.Seen.Single();
            Assert.Equal("groq.test/openai/v1/audio/speech", seen.Key);
            Assert.Equal("llama-fast", (string?)seen.Value["model"]);
            Assert.Equal("Hello from the fleet.", (string?)seen.Value["input"]);
            Assert.Equal("alloy", (string?)seen.Value["voice"]);
        }

        [Fact]
        public async Task TranscriptionForwardsTheFileByteForByteWithTheTargetModel()
        {
            byte[]? upstreamBody = null;
            string? upstreamType = null;
            var fake = new FakeUpstream().On("groq.test", (r, b) =>
            {
                upstreamBody = r.Content!.ReadAsByteArrayAsync().Result;
                upstreamType = r.Content.Headers.ContentType!.ToString();
                return FakeUpstream.Json(HttpStatusCode.OK, new JObject { ["text"] = "hello there" });
            });
            var host = Start(fake, out var key);
            var r = await Transcribe(host, key, "fast");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("hello there", (string?)JObject.Parse(await r.Content.ReadAsStringAsync())["text"]);
            Assert.Equal("groq/llama-fast", r.Headers.GetValues("X-Fairlead-Target").Single());
            Assert.Contains("multipart/form-data", upstreamType);
            Assert.Equal("groq.test/openai/v1/audio/transcriptions", fake.Seen.Single().Key);
            string error;
            var parsed = RawUpload.Parse(upstreamBody!, upstreamType, out error)!;
            Assert.Null(error);
            Assert.Equal("llama-fast", parsed.Field("model"));
            Assert.Equal("en", parsed.Field("language"));
            Assert.True(parsed.HasFile);
            var wav = WavBytes();
            Assert.True(IndexOf(upstreamBody!, wav) >= 0, "the audio bytes were changed on the way");
        }

        [Fact]
        public async Task SameKeysAsChat_AndTheRequestLogRecordsThemWithoutTheText()
        {
            var fake = new FakeUpstream().On("groq.test", (r, b) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Speech) });
            var host = Start(fake, out var key);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Speak(host, null, "fast", "hi")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Transcribe(host, null, "fast")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Speak(host, key, "fast", "A UNIQUE SPOKEN SENTENCE")).StatusCode);
            var logs = Directory.GetFiles(Path.Combine(_dir, "logs"), "fairlead-requests-*.jsonl");
            var text = string.Join("\n", logs.Select(File.ReadAllText));
            Assert.Contains("/v1/audio/speech", text);
            Assert.Contains("voice-app", text);
            Assert.DoesNotContain("A UNIQUE SPOKEN SENTENCE", text);
        }

        [Fact]
        public async Task PrivateRequestsAndNonOpenAiTargetsNeverReachTheCloudOrFail503()
        {
            var fake = new FakeUpstream().On("groq.test", (r, b) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Speech) });
            var host = Start(fake, out var key);
            // Private: the only local target is Ollama, which has no audio endpoints, so nothing may answer and nothing goes out.
            var priv = await Speak(host, key, "fast", "secret words", privacy: "local");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, priv.StatusCode);
            // The smart route holds Anthropic and Ollama only.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Speak(host, key, "smart", "hello")).StatusCode);
            Assert.Empty(fake.Seen);
        }

        [Fact]
        public async Task BadRequestsAreClearErrors()
        {
            var fake = new FakeUpstream().On("groq.test", (r, b) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Speech) });
            var host = Start(fake, out var key);
            var noInput = await Speak(host, key, "fast", "");
            Assert.Equal(HttpStatusCode.BadRequest, noInput.StatusCode);
            Assert.Contains("input", await noInput.Content.ReadAsStringAsync());
            var noFile = await Transcribe(host, key, "fast", withFile: false);
            Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/audio/transcriptions") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            var notMultipart = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, notMultipart.StatusCode);
            Assert.Contains("multipart", await notMultipart.Content.ReadAsStringAsync());
            Assert.Empty(fake.Seen);
        }

        [Fact]
        public void MultipartRoundTripsAndOnlyTheNamedFieldChanges()
        {
            var form = "--B\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.wav\"\r\nContent-Type: audio/wav\r\n\r\nDATA\r\n--B\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\nwhisper-1\r\n--B--\r\n";
            string error;
            var up = RawUpload.Parse(Encoding.ASCII.GetBytes(form), "multipart/form-data; boundary=B", out error)!;
            Assert.Null(error);
            Assert.Equal("whisper-1", up.Field("model"));
            Assert.Null(up.Field("file"));
            Assert.Equal(form, Encoding.ASCII.GetString(up.ToBytes()));
            var changed = Encoding.ASCII.GetString(up.WithField("model", "other").ToBytes());
            Assert.Equal(form.Replace("whisper-1", "other"), changed);
            Assert.Contains("name=\"temperature\"", Encoding.ASCII.GetString(up.WithField("temperature", "0").ToBytes()));
            Assert.Null(RawUpload.Parse(new byte[] { 1 }, "text/plain", out error));
            Assert.NotNull(error);
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        public void Dispose()
        {
            foreach (var h in _hosts) { try { h.Stop(); } catch (Exception) { } }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
