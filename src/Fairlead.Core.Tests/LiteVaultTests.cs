using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Host;
using Fairlead.Core.Secrets;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>Lazarette Lite's items, read from a fake Credential Manager: target name to secret.</summary>
    internal sealed class FakeCredentialManager
    {
        public readonly Dictionary<string, string> Items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Broken;
        public int Lists;
        public int Reads;

        public IList<string> List(string filter)
        {
            Lists++;
            if (Broken) throw new InvalidOperationException("Windows Credential Manager is not available on this system.");
            var prefix = filter.TrimEnd('*');
            return Items.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public string Read(string target)
        {
            Reads++;
            string v;
            return Items.TryGetValue(target, out v) ? v : null;
        }

        public LiteVault Vault(double cacheSeconds = 0)
        {
            return new LiteVault(List, Read) { CacheSeconds = cacheSeconds };
        }
    }

    public class LiteVaultTests
    {
        [Fact]
        public void ReadsTheItemWhoseServiceIsTheVariableName()
        {
            var cm = new FakeCredentialManager();
            cm.Items["lazarette-lite/DATABRICKS_TOKEN/DATABRICKS_TOKEN"] = "tok-lite";
            cm.Items["lazarette-lite/gitlab/anthony"] = "glpat";
            cm.Items["Fairlead/Semaphore"] = "client-key";                  // not Lite's
            cm.Items["PM/DATABRICKS_TOKEN/x"] = "bridge";                   // Lazarette's bridge, not Lite's
            var lite = cm.Vault();

            Assert.Equal("tok-lite", lite.Value("DATABRICKS_TOKEN"));
            Assert.Equal("ok", lite.StateOf("DATABRICKS_TOKEN"));
            Assert.Equal("tok-lite", lite.Value("databricks_token"));        // ignoring case, like Lite
            Assert.Null(lite.Value("OPENAI_API_KEY"));
            Assert.Equal("not-stowed", lite.StateOf("OPENAI_API_KEY"));
        }

        [Fact]
        public void AnyUserNameWorksButTwoItemsForOneNameAreNotGuessed()
        {
            var cm = new FakeCredentialManager();
            cm.Items["lazarette-lite/OPENAI_API_KEY/anthony"] = "sk-one";
            var lite = cm.Vault();
            Assert.Equal("sk-one", lite.Value("OPENAI_API_KEY"));

            cm.Items["lazarette-lite/OPENAI_API_KEY/work"] = "sk-two";
            Assert.Null(lite.Value("OPENAI_API_KEY"));
            Assert.Equal("ambiguous", lite.StateOf("OPENAI_API_KEY"));
        }

        [Fact]
        public void ANameDoesNotMatchAnotherServiceThatStartsTheSame()
        {
            var cm = new FakeCredentialManager();
            cm.Items["lazarette-lite/OPENAI_API_KEY_OLD/OPENAI_API_KEY_OLD"] = "old";
            Assert.Null(cm.Vault().Value("OPENAI_API_KEY"));
        }

        [Fact]
        public void AnEmptySecretIsReportedAsEmpty()
        {
            var cm = new FakeCredentialManager();
            cm.Items["lazarette-lite/AI_ENDPOINT_TOKEN/AI_ENDPOINT_TOKEN"] = "";
            var lite = cm.Vault();
            Assert.Null(lite.Value("AI_ENDPOINT_TOKEN"));
            Assert.Equal("empty", lite.StateOf("AI_ENDPOINT_TOKEN"));
        }

        [Fact]
        public void CredentialManagerFailingIsAStateNeverAnException()
        {
            var cm = new FakeCredentialManager { Broken = true };
            var lite = cm.Vault();
            Assert.Null(lite.Value("DATABRICKS_TOKEN"));
            Assert.Equal("unavailable", lite.StateOf("DATABRICKS_TOKEN"));
        }

        [Fact]
        public void OnlyVariableShapedNamesAreLookedUpSoNoWildcardReachesWindows()
        {
            var cm = new FakeCredentialManager();
            cm.Items["lazarette-lite/anything/anything"] = "x";
            var lite = cm.Vault();
            foreach (var bad in new[] { "*", "a/b", "", "1ABC", "A B", null })
                Assert.Null(lite.Value(bad));
            Assert.Equal(0, cm.Lists);
        }

        [Fact]
        public void AnAnswerIsReusedBrieflyAndRefreshAsksAgainAndReportsChange()
        {
            var cm = new FakeCredentialManager();
            var lite = cm.Vault(cacheSeconds: 60);
            Assert.Null(lite.Value("DATABRICKS_TOKEN"));
            cm.Items["lazarette-lite/DATABRICKS_TOKEN/DATABRICKS_TOKEN"] = "tok";
            Assert.Null(lite.Value("DATABRICKS_TOKEN"));                      // still the cached miss
            Assert.Equal(1, cm.Lists);

            Assert.True(lite.Refresh(new[] { "DATABRICKS_TOKEN" }));
            Assert.Equal("tok", lite.Value("DATABRICKS_TOKEN"));
            Assert.False(lite.Refresh(new[] { "DATABRICKS_TOKEN" }));         // nothing new

            cm.Items["lazarette-lite/DATABRICKS_TOKEN/DATABRICKS_TOKEN"] = "tok-2";
            Assert.True(lite.Refresh(new[] { "DATABRICKS_TOKEN" }));
        }

        [Fact]
        public void ADisabledSourceFindsNothing()
        {
            var lite = LiteVault.Disabled();
            Assert.Null(lite.Value("DATABRICKS_TOKEN"));
            Assert.Equal("unavailable", lite.StateOf("DATABRICKS_TOKEN"));
        }

        [Fact]
        public void TheDefaultSourceGoesThroughCredentialStoreSoItCanBeReplaced()
        {
            var oldLister = CredentialStore.Lister;
            var oldReader = CredentialStore.Reader;
            try
            {
                var cm = new FakeCredentialManager();
                cm.Items["lazarette-lite/OPENAI_API_KEY/OPENAI_API_KEY"] = "sk-x";
                CredentialStore.Lister = cm.List;
                CredentialStore.Reader = cm.Read;
                Assert.Equal("sk-x", new LiteVault().Value("OPENAI_API_KEY"));
            }
            finally
            {
                CredentialStore.Lister = oldLister;
                CredentialStore.Reader = oldReader;
            }
        }
    }

    public sealed class LiteVaultHostTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-lite-" + Guid.NewGuid().ToString("N"));
        private readonly GatewayFixture _fx = new GatewayFixture();
        private readonly HttpClient _http = new HttpClient();
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public LiteVaultHostTests() { Directory.CreateDirectory(_dir); }

        public void Dispose()
        {
            foreach (var d in _owned) d.Dispose();
            _http.Dispose();
            _fx.Unexport();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static string Wrap(string s) { return "t:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
        private static string Unwrap(string s) { return s != null && s.StartsWith("t:") ? Encoding.UTF8.GetString(Convert.FromBase64String(s.Substring(2))) : null; }

        private FairleadHost Start(LiteVault lite)
        {
            var store = new GatewayStore(Path.Combine(_dir, "fairlead"), Wrap, Unwrap);
            store.SaveFile("models", _fx.Yaml());
            store.SetValues(new Dictionary<string, string> { { _fx.BaseVar, GatewayFixture.BaseUrl } });
            var host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = Path.Combine(_dir, "fairlead"), LogDirectory = Path.Combine(_dir, "logs") },
                Config = RoutingTests.Config(),
                GatewayStore = store,
                LazaretteDataDirectory = Path.Combine(_dir, "no-lazarette"),
                LiteVault = lite,
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

        private async Task<JObject> Admin(FairleadHost host, HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, host.BaseUrl + path);
            req.Headers.Add("X-Fairlead-Session", host.SessionToken);
            var r = await _http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode, text);
            return JObject.Parse(text);
        }

        [Fact]
        public async Task TheGatewayTokenFromLiteNeedsNoPairingAndNeverReachesTheBrowser()
        {
            var cm = new FakeCredentialManager();
            var host = Start(cm.Vault());
            Assert.Null(host.Config.Snapshot().FindProvider("gw_openai"));    // no token anywhere yet
            Assert.False(host.Lazarette.Paired);

            // The user stows the token in Lite, then presses Refresh in Fairlead.
            cm.Items["lazarette-lite/" + _fx.TokenVar + "/" + _fx.TokenVar] = GatewayFixture.Token;
            var view = await Admin(host, HttpMethod.Post, "/api/lazarette/refresh");
            Assert.NotNull(host.Config.Snapshot().FindProvider("gw_openai"));
            Assert.Equal("not-paired", (string?)view["state"]);
            var lite = ((JArray)view.At("lite").At("names")!).First(n => (string?)n["name"] == _fx.TokenVar);
            Assert.Equal("ok", (string?)lite["state"]);

            var status = await Admin(host, HttpMethod.Get, "/api/status");
            var tokenVar = ((JArray)status.At("gatewayModels").At("variables")!).First(v => (string?)v["name"] == _fx.TokenVar);
            Assert.Equal("lazarette", (string?)tokenVar["source"]);

            var all = view.ToString() + status + await Admin(host, HttpMethod.Get, "/api/config");
            Assert.DoesNotContain(GatewayFixture.Token, all);
        }

        [Fact]
        public void LiteAnswersWhenLazaretteHoldsNothing()
        {
            var cm = new FakeCredentialManager();
            cm.Items["lazarette-lite/DATABRICKS_TOKEN/DATABRICKS_TOKEN"] = "from-lite";
            var host = Start(cm.Vault());
            Assert.Equal("from-lite", host.SecretValue("DATABRICKS_TOKEN"));
            Assert.Null(host.SecretValue("OPENAI_API_KEY"));
        }

        [Fact]
        public void AHostBuiltForTestsNeverReadsTheRealCredentialManager()
        {
            var host = Start(null);
            Assert.Equal("unavailable", host.Lite.StateOf("DATABRICKS_TOKEN"));
        }
    }
}
