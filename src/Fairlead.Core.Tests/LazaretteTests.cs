using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Host;
using Fairlead.Core.Secrets;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>Fairlead's half of the Lazarette hand-off against vectors made by Lazarette's reference (dev/fairlead-peer.cjs).</summary>
    public class LazaretteProtocolTests
    {
        // node -e with Lazarette dev/fairlead-peer.cjs: secret = bytes 1..32, time 1793050000, names DATABRICKS_TOKEN,OPENAI_API_KEY.
        internal const string Code = "lzfl1.0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0.AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
        private const string Nonce = "bm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm9uY2Utbm8";
        private const string Proof = "eBYxqlJ_cXFkEysu5owpz3Wm811PGspXSYIGbJu53IQ";
        private const string Revision = "0123456789abcdef0123456789abcdef";
        private const string Sealed = "R1HiPt57s75oTwxbBbht9Xt5wYKvzb2tzFnM/j+Ml5LjzUIOu46UxXM9MYpuD4E+Mtm3I2o0GZpT7287yKIT3m/TmWeIkyY3Vib+4PEKgzwb57UmdGDJi2R3vRIDpEgqdv/xvXmM8PAuXJ5OVhLJ0jB9iXC3plZL1XEnisCxpB2hISSt9pe3cgG5PiBm9x+0t8CwScB4b2vgquQ6SSRzFP9nUtvV0aV5lidi2iZ382ylRs2hEoQQkCWO5VFeDgIo4PuA9GDPdCNjTFrt7My3pjb2Hwz0ZWQg0LLd5fv+6A8ahTzYczUewm1wHl0C4FjgC7D2izYJmrf5zt8zwoNd8g==";

        [Fact]
        public void ParsesTheCodeAndMatchesTheReferenceProof()
        {
            var p = LazarettePairing.TryParse(Code)!;
            Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", p.Id);
            Assert.Equal(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), p.Secret);
            Assert.Equal(Code, p.ToCode());
            Assert.Equal(Proof, LazaretteProtocol.Proof(p, 1793050000, Nonce, new[] { "DATABRICKS_TOKEN", "OPENAI_API_KEY" }, ""));
            Assert.Null(LazarettePairing.TryParse("lzfl1.not-a-code"));
            Assert.Null(LazarettePairing.TryParse(Code.ToUpperInvariant()));
        }

        [Fact]
        public void OpensTheReferenceSealAndRefusesATamperedOne()
        {
            var p = LazarettePairing.TryParse(Code)!;
            var blob = Convert.FromBase64String(Sealed);
            var plain = LazaretteProtocol.Open(p, Nonce, "ok", Revision, blob);
            Assert.NotNull(plain);
            var o = JObject.Parse(Encoding.UTF8.GetString(plain!));
            Assert.Equal("tok-vector", (string?)o.At("secrets").At("DATABRICKS_TOKEN").At("value"));

            Assert.Null(LazaretteProtocol.Open(p, Nonce, "unchanged", Revision, blob));     // state is authenticated
            Assert.Null(LazaretteProtocol.Open(p, "x" + Nonce.Substring(1), "ok", Revision, blob));
            var bad = (byte[])blob.Clone();
            bad[20] ^= 1;
            Assert.Null(LazaretteProtocol.Open(p, Nonce, "ok", Revision, bad));
        }
    }

    /// <summary>A stand-in Lazarette: a loopback server speaking the peer route, with a runtime file Fairlead discovers.</summary>
    internal sealed class FakeLazarette : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly LazarettePairing _pairing;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public string Mode = "ok";              // ok | locked | not-paired | forged
        public int Requests;
        public readonly List<JObject> Seen = new List<JObject>();

        public FakeLazarette(string dataDir, string code)
        {
            _pairing = LazarettePairing.TryParse(code)!;
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            _listener.Start();
            Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
            File.WriteAllText(Path.Combine(dataDir, "logs", "lazarette-server-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json"),
                "{\"app\":\"lazarette\",\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + ",\"port\":" + Port + ",\"url\":\"http://127.0.0.1:" + Port + "/\",\"startedAt\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\"}");
            Task.Run(Loop);
        }

        public int Port { get; }

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                try { Handle(ctx); } catch { ctx.Response.StatusCode = 500; ctx.Response.Close(); }
            }
        }

        private void Reply(HttpListenerContext ctx, int code, JObject body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private void Handle(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref Requests);
            var req = JObject.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd());
            lock (Seen) Seen.Add(req);
            if (ctx.Request.Url!.AbsolutePath != LazaretteProtocol.PeerPath || ctx.Request.Headers["X-Fleet-Peer"] != "fairlead/1" || ctx.Request.Headers["Origin"] != null)
            { Reply(ctx, 400, new JObject { ["state"] = "request" }); return; }
            if (Mode == "locked") { Reply(ctx, 423, new JObject { ["state"] = "locked", ["message"] = "locked" }); return; }
            var names = ((JArray)req["names"]!).Select(n => (string)n!).ToList();
            var nonce = (string)req["nonce"]!;
            var expected = LazaretteProtocol.Proof(_pairing, (long)req["time"]!, nonce, names, (string)req["ifRevision"]!);
            if (Mode == "not-paired" || (string)req["pairing"]! != _pairing.Id || (string)req["proof"]! != expected)
            { Reply(ctx, 401, new JObject { ["state"] = "not-paired", ["message"] = "pair again" }); return; }

            var secrets = new JObject();
            var lines = new StringBuilder();
            foreach (var n in names.OrderBy(x => x, StringComparer.Ordinal))
            {
                Values.TryGetValue(n, out var v);
                lines.Append(n).Append('\t').Append(v == null ? "not-stowed" : "ok").Append('\t').Append(v ?? "").Append('\n');
            }
            foreach (var n in names)
                secrets[n] = Values.TryGetValue(n, out var v) ? new JObject { ["state"] = "ok", ["value"] = v } : new JObject { ["state"] = "not-stowed" };
            var revision = BitConverter.ToString(LazaretteProtocol.Hmac(_pairing.Secret, Encoding.UTF8.GetBytes("rev\n" + lines))).Replace("-", "").ToLowerInvariant().Substring(0, 32);
            var unchanged = (string)req["ifRevision"]! == revision;
            var state = unchanged ? "unchanged" : "ok";
            var plain = unchanged ? "{\"state\":\"unchanged\"}" : new JObject { ["format"] = LazaretteProtocol.PayloadFormat, ["revision"] = revision, ["secrets"] = secrets }.ToString(Formatting.None);
            var seal = Seal(_pairing, nonce, state, revision, Encoding.UTF8.GetBytes(plain));
            if (Mode == "forged") seal[seal.Length - 1] ^= 1;
            Reply(ctx, 200, new JObject { ["state"] = state, ["pairing"] = _pairing.Id, ["nonce"] = nonce, ["revision"] = revision, ["sealed"] = Convert.ToBase64String(seal) });
        }

        private static byte[] Seal(LazarettePairing p, string nonce, string state, string revision, byte[] plain)
        {
            var k = LazaretteProtocol.Hkdf(p.Secret, LazaretteProtocol.Label + " response|" + nonce);
            using var aes = Aes.Create();
            aes.Key = LazaretteProtocol.Hkdf(k, LazaretteProtocol.SealDomain + "-enc");
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            byte[] ct;
            using (var enc = aes.CreateEncryptor()) ct = enc.TransformFinalBlock(plain, 0, plain.Length);
            var blob = new byte[16 + ct.Length + 32];
            Buffer.BlockCopy(aes.IV, 0, blob, 0, 16);
            Buffer.BlockCopy(ct, 0, blob, 16, ct.Length);
            var aad = Encoding.UTF8.GetBytes(LazaretteProtocol.Label + "|" + p.Id + "|" + nonce + "|" + state + "|" + revision);
            var tag = LazaretteProtocol.Tag(LazaretteProtocol.Hkdf(k, LazaretteProtocol.SealDomain + "-mac"), aad, blob, ct.Length);
            Buffer.BlockCopy(tag, 0, blob, 16 + ct.Length, 32);
            return blob;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }

    public sealed class LazaretteLinkTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-lz-" + Guid.NewGuid().ToString("N"));
        private readonly GatewayFixture _fx = new GatewayFixture();
        private readonly HttpClient _http = new HttpClient();
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public LazaretteLinkTests() { Directory.CreateDirectory(_dir); }

        public void Dispose()
        {
            foreach (var d in _owned) d.Dispose();
            _http.Dispose();
            _fx.Unexport();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static string Wrap(string s) { return "t:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
        private static string Unwrap(string s) { return s != null && s.StartsWith("t:") ? Encoding.UTF8.GetString(Convert.FromBase64String(s.Substring(2))) : null; }

        private FairleadHost Start(out FakeLazarette lazarette)
        {
            var lzData = Path.Combine(_dir, "lazarette");
            lazarette = new FakeLazarette(lzData, LazaretteProtocolTests.Code);
            _owned.Add(lazarette);
            var store = new GatewayStore(Path.Combine(_dir, "fairlead"), Wrap, Unwrap);
            store.SaveFile("models", _fx.Yaml());
            store.SetValues(new Dictionary<string, string> { { _fx.BaseVar, GatewayFixture.BaseUrl } });
            var host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = Path.Combine(_dir, "fairlead"), LogDirectory = Path.Combine(_dir, "logs") },
                Config = RoutingTests.Config(),
                GatewayStore = store,
                LazaretteDataDirectory = lzData,
                Protect = Wrap,
                Unprotect = Unwrap,
                UpstreamHandler = new FakeUpstream(),
                Monitor = false,
                PreferredPort = 0,
            });
            host.Start();
            _owned.Add(host);
            return host;
        }

        private async Task<JObject> Admin(FairleadHost host, HttpMethod method, string path, JObject? body = null)
        {
            var req = new HttpRequestMessage(method, host.BaseUrl + path);
            if (body != null) req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            req.Headers.Add("X-Fairlead-Session", host.SessionToken);
            var r = await _http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode, text);
            return JObject.Parse(text);
        }

        [Fact]
        public async Task TheGatewayTokenComesFromLazaretteAndNeverReachesTheBrowser()
        {
            var host = Start(out var lz);
            lz.Values[_fx.TokenVar] = GatewayFixture.Token;
            Assert.Null(host.Config.Snapshot().FindProvider("gw_openai"));   // token missing until paired

            var view = await Admin(host, HttpMethod.Post, "/api/lazarette/pair", new JObject { ["code"] = LazaretteProtocolTests.Code });
            Assert.Equal("ok", (string?)view["state"]);
            Assert.NotNull(host.Config.Snapshot().FindProvider("gw_openai"));
            var status = await Admin(host, HttpMethod.Get, "/api/status");
            var tokenVar = ((JArray)status.At("gatewayModels").At("variables")!).First(v => (string?)v["name"] == _fx.TokenVar);
            Assert.Equal("lazarette", (string?)tokenVar["source"]);

            var all = (await Admin(host, HttpMethod.Get, "/api/lazarette")).ToString() + (await Admin(host, HttpMethod.Get, "/api/status")).ToString() + (await Admin(host, HttpMethod.Get, "/api/config")).ToString();
            Assert.DoesNotContain(GatewayFixture.Token, all);
            Assert.DoesNotContain("AQIDBAUG", all);   // the pairing secret
            Assert.Contains("…e1f0", all);

            var saved = File.ReadAllText(Path.Combine(_dir, "fairlead", LazaretteLink.PairingFile));
            Assert.DoesNotContain("AQIDBAUG", saved);
        }

        [Fact]
        public async Task KeepsValuesWhileLockedWithinTheHoldAndDropsThemWhenUnpairedThere()
        {
            var host = Start(out var lz);
            lz.Values[_fx.TokenVar] = GatewayFixture.Token;
            await Admin(host, HttpMethod.Post, "/api/lazarette/pair", new JObject { ["code"] = LazaretteProtocolTests.Code });
            Assert.Equal(GatewayFixture.Token, host.Lazarette.Value(_fx.TokenVar));

            await Admin(host, HttpMethod.Post, "/api/lazarette/refresh");
            Assert.Equal("", (string?)lz.Seen[0]["ifRevision"]);
            Assert.Matches("^[0-9a-f]{32}$", (string?)lz.Seen[1]["ifRevision"]);   // second ask is "unchanged"
            Assert.Equal("ok", host.Lazarette.State);

            lz.Mode = "locked";
            await host.Lazarette.PollAsync();
            Assert.Equal("locked", host.Lazarette.State);
            Assert.Equal(GatewayFixture.Token, host.Lazarette.Value(_fx.TokenVar));
            Assert.NotNull(host.Config.Snapshot().FindProvider("gw_openai"));

            lz.Mode = "not-paired";
            await host.Lazarette.PollAsync();
            Assert.Equal("not-paired", host.Lazarette.State);
            Assert.Null(host.Lazarette.Value(_fx.TokenVar));
            Assert.Null(host.Config.Snapshot().FindProvider("gw_openai"));
        }

        [Fact]
        public async Task HoldZeroForgetsOnLockAndAForgedAnswerIsIgnored()
        {
            var host = Start(out var lz);
            lz.Values[_fx.TokenVar] = GatewayFixture.Token;
            await Admin(host, HttpMethod.Post, "/api/settings", new JObject { ["lazaretteHoldHours"] = 0 });
            await Admin(host, HttpMethod.Post, "/api/lazarette/pair", new JObject { ["code"] = LazaretteProtocolTests.Code });
            Assert.Equal(GatewayFixture.Token, host.Lazarette.Value(_fx.TokenVar));   // just confirmed

            lz.Mode = "locked";
            await host.Lazarette.PollAsync();
            Assert.Null(host.Lazarette.Value(_fx.TokenVar));

            lz.Mode = "forged";
            lz.Values[_fx.TokenVar] = "tok-from-a-forger";
            await host.Lazarette.PollAsync();
            Assert.Equal("error", host.Lazarette.State);
            Assert.Null(host.Lazarette.Value(_fx.TokenVar));
        }

        [Fact]
        public async Task NotRunningIsAStateAndUnpairForgetsEverything()
        {
            var host = Start(out var lz);
            lz.Values[_fx.TokenVar] = GatewayFixture.Token;
            await Admin(host, HttpMethod.Post, "/api/lazarette/pair", new JObject { ["code"] = LazaretteProtocolTests.Code });
            lz.Dispose();
            await host.Lazarette.PollAsync();
            Assert.Equal("not-running", host.Lazarette.State);
            Assert.Equal(GatewayFixture.Token, host.Lazarette.Value(_fx.TokenVar));

            var view = await Admin(host, HttpMethod.Delete, "/api/lazarette/pair");
            Assert.False((bool)view["paired"]!);
            Assert.Null(host.Lazarette.Value(_fx.TokenVar));
            Assert.False(File.Exists(Path.Combine(_dir, "fairlead", LazaretteLink.PairingFile)));
        }

        [Fact]
        public async Task ServesTheFleetIdEndpoint()
        {
            var host = Start(out _);
            var o = JObject.Parse(await _http.GetStringAsync(host.BaseUrl + "/__fleethacker/id"));
            Assert.Equal("fairlead", (string?)o["tool"]);
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, (int)o["pid"]!);
            // No launcher script in tests: the id names this process's executable.
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName, (string?)o["path"]);
        }

        [Fact]
        public void TheIdPathIsTheGivenScriptMadeAbsolute()
        {
            var relative = Path.Combine("scripts", "..", "scripts", "Start-Fairlead.ps1");
            var expected = Path.GetFullPath(relative);
            Assert.Equal(expected, FairleadHost.ResolveProcessPath(relative));
            Assert.Equal(expected, FairleadHost.ResolveProcessPath("  " + relative + "  "));
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName, FairleadHost.ResolveProcessPath(null));
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName, FairleadHost.ResolveProcessPath(""));
        }
    }
}
