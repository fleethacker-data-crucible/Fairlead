using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Gateway;
using Fairlead.Core.Host;
using Fairlead.Core.Protocol;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>The optional total-time cap (FAIRLEAD_MAX_REQUEST_SECONDS): off by default, ends a stream that outlives it.</summary>
    public sealed class TotalTimeCapTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-cap-" + Guid.NewGuid().ToString("N"));
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly HttpClient _http = new HttpClient();

        /// <summary>Sends one chunk, then keep-alive comments every 150 ms for as long as it is read, or nothing at all.</summary>
        private sealed class EndlessStream : Stream
        {
            private readonly byte[] _first;
            private readonly bool _keepAlives;
            private bool _sent;

            public EndlessStream(string first, bool keepAlives)
            {
                _first = Encoding.UTF8.GetBytes(first);
                _keepAlives = keepAlives;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                byte[] data;
                if (!_sent) { _sent = true; data = _first; }
                else if (_keepAlives) { await Task.Delay(150).ConfigureAwait(false); data = Encoding.UTF8.GetBytes(": keep-alive\n\n"); }
                else { await Task.Delay(Timeout.Infinite).ConfigureAwait(false); return 0; } // a stall the HTTP stack never interrupts
                Array.Copy(data, 0, buffer, offset, data.Length);
                return data.Length;
            }
        }

        private FairleadHost Start(FakeUpstream fake, int capSeconds, out string key)
        {
            var cfg = RoutingTests.Config();
            cfg.FindProvider("groq")!.BaseUrl = "https://groq.test/openai/v1";
            key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = key.Substring(0, 7), Hash = GatewayEngine.HashKey(key) });
            var host = new FairleadHost(new HostOptions { Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs"), MaxRequestSeconds = capSeconds }, Config = cfg, UpstreamHandler = fake, Monitor = false, PreferredPort = 0 });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private static FakeUpstream Endless(bool keepAlives)
        {
            return new FakeUpstream().On("groq.test", (r, b) =>
            {
                var content = new StreamContent(new EndlessStream(FakeUpstream.OpenAiChunk("partial"), keepAlives));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });
        }

        private async Task<string> Stream(FairleadHost host, string key)
        {
            var body = new JObject { ["model"] = "groq/llama-fast", ["stream"] = true, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } } };
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/chat/completions") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            var r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        [Fact]
        public async Task AStreamThatStallsIsEndedAtTheCapWithAnError()
        {
            var host = Start(Endless(false), 1, out var key);
            var clock = Stopwatch.StartNew();
            var wire = await Stream(host, key);
            Assert.InRange(clock.Elapsed.TotalSeconds, 0.8, 8);
            Assert.Contains("\"content\":\"partial\"", wire);
            Assert.Contains("total limit", wire);
            Assert.EndsWith("data: [DONE]\n\n", wire);
        }

        [Fact]
        public async Task KeepAlivesDoNotLetAStreamOutliveTheCap()
        {
            var host = Start(Endless(true), 1, out var key);
            var clock = Stopwatch.StartNew();
            var wire = await Stream(host, key);
            Assert.InRange(clock.Elapsed.TotalSeconds, 0.8, 8);
            Assert.Contains(": keep-alive", wire); // they were passed on while the cap had not been reached
            Assert.Contains("total limit", wire);
        }

        [Fact]
        public async Task WithoutACapAStreamThatFinishesIsUntouched()
        {
            var fake = new FakeUpstream().On("groq.test", (r, b) => FakeUpstream.Sse(": keep-alive\n\n", FakeUpstream.OpenAiChunk("whole answer"), FakeUpstream.OpenAiFinish(), "data: [DONE]\n\n"));
            var host = Start(fake, 0, out var key);
            var wire = await Stream(host, key);
            Assert.Contains("whole answer", wire);
            Assert.DoesNotContain("total limit", wire);
        }

        public void Dispose()
        {
            foreach (var h in _hosts) { try { h.Stop(); } catch (Exception) { } }
            _http.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }
}
