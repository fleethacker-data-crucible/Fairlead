using System;
using System.Collections.Generic;
using Fairlead.Core.Config;
using Fairlead.Core.Providers;
using Fairlead.Core.Resilience;
using Fairlead.Core.Serialization;
using Fairlead.Core.Telemetry;
using Xunit;
using N = Newtonsoft.Json;
using NConverters = Newtonsoft.Json.Converters;
using NLinq = Newtonsoft.Json.Linq;
using NSerialization = Newtonsoft.Json.Serialization;

namespace Fairlead.Core.Tests
{
    /// <summary>
    /// Fairlead 0.1 used Newtonsoft.Json; the engine now has its own JSON (no third-party DLL). These tests pin that
    /// fairlead.json, API responses and relayed request bodies come out exactly as 0.1 wrote and read them.
    /// </summary>
    public class NewtonsoftParityTests
    {
        // The two configurations 0.1 used: ConfigStore.FileJson and Host.Json.Settings.
        private static readonly N.JsonSerializerSettings NFile = new N.JsonSerializerSettings
        {
            ContractResolver = new NSerialization.DefaultContractResolver { NamingStrategy = new NSerialization.CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
            Converters = { new NConverters.StringEnumConverter { NamingStrategy = new NSerialization.CamelCaseNamingStrategy() } },
            NullValueHandling = N.NullValueHandling.Ignore,
            Formatting = N.Formatting.Indented,
            ObjectCreationHandling = N.ObjectCreationHandling.Replace,
        };

        private static readonly N.JsonSerializerSettings NApi = new N.JsonSerializerSettings
        {
            ContractResolver = new NSerialization.DefaultContractResolver { NamingStrategy = new NSerialization.CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = true } },
            NullValueHandling = N.NullValueHandling.Include,
            Converters = { new NConverters.StringEnumConverter { NamingStrategy = new NSerialization.CamelCaseNamingStrategy() } },
            DateFormatHandling = N.DateFormatHandling.IsoDateFormat,
            Formatting = N.Formatting.None,
        };

        private static FairleadConfig RichConfig()
        {
            var c = FairleadConfig.CreateDefault();
            c.SetupComplete = true;
            c.Kedge = KedgeMode.On;
            c.Providers.Add(new ProviderConfig
            {
                Id = "cloud",
                Name = "Cloud \"quoted\" \u00e9\u4e2d",
                Kind = ProviderKind.Anthropic,
                BaseUrl = "https://example.invalid/v1",
                ApiKeyProtected = "dpapi:AAAA==",
                Headers = new Dictionary<string, string> { { "X-Custom", "a\tb" }, { "Mixed-Case", "c" } },
                Models = new List<ModelInfo>
                {
                    new ModelInfo { Id = "big", ContextWindow = 200000, Tools = true, InputPricePerMTok = 3m, OutputPricePerMTok = 15.25m },
                    new ModelInfo { Id = "embed", Embedding = true, Discovered = true },
                },
            });
            c.Routes.Add(new RouteConfig { Id = "fast", Strategy = RouteStrategy.RoundRobin, Targets = new List<string> { "cloud/big", "local/llama" }, HedgeAfterMs = 750 });
            c.Rules.Add(new RoutingRule { Name = "secrets", Route = "local", When = new RuleCondition { Sensitive = true, Keywords = new List<string> { "password" }, MinTokens = 10 } });
            c.Aliases["gpt-4*"] = "smart";
            c.Clients.Add(new ClientKey { Id = "k1", Name = "halyard", Prefix = "fl-abcd", Hash = "00ff", Created = new DateTimeOffset(2026, 10, 26, 13, 2, 51, 123, TimeSpan.FromHours(-4)), Routes = new List<string> { "smart" } });
            c.CorsOrigins.Add("http://localhost:3000");
            return c;
        }

        [Fact]
        public void ConfigFileIsWrittenAsNewtonsoftWroteIt()
        {
            var c = RichConfig();
            Assert.Equal(N.JsonConvert.SerializeObject(c, NFile), ConfigStore.Serialize(c));
        }

        [Fact]
        public void ConfigFileWrittenByNewtonsoftReadsBackTheSame()
        {
            var written = N.JsonConvert.SerializeObject(RichConfig(), NFile);
            var read = Serialization.JsonConvert.DeserializeObject<FairleadConfig>(written, JsonSettings.File);
            Assert.Equal(written, N.JsonConvert.SerializeObject(read, NFile));
        }

        [Fact]
        public void ConfigFileWithNullsAndMissingFieldsReadsAsNewtonsoftReadIt()
        {
            const string text = "{ \"version\": 1, \"kedge\": \"OFF\", \"defaultRoute\": null, \"providers\": [ { \"id\": \"p\", \"headers\": null, \"models\": [ { \"id\": \"m\", \"tools\": null } ], \"timeoutSeconds\": \"30\" } ], \"network\": { \"probeHosts\": [] }, \"unknown\": [1, 2] }";
            var mine = Serialization.JsonConvert.DeserializeObject<FairleadConfig>(text, JsonSettings.File);
            var theirs = N.JsonConvert.DeserializeObject<FairleadConfig>(text, NFile);
            Assert.Equal(N.JsonConvert.SerializeObject(theirs, NFile), N.JsonConvert.SerializeObject(mine, NFile));
        }

        [Fact]
        public void ApiResponsesAreWrittenAsNewtonsoftWroteThem()
        {
            var record = new RequestRecord
            {
                Id = "r1",
                Time = new DateTimeOffset(2026, 10, 26, 9, 38, 11, TimeSpan.Zero),
                Client = null,
                Dialect = "openai",
                Decision = new List<string> { "auto -> smart" },
                Attempts = new List<AttemptRecord> { new AttemptRecord { Target = "cloud/big", Outcome = "ok", Status = 200, Ms = 412 } },
                FirstByteMs = 99,
                Cost = 0.0015m,
                PromptPreview = "line1\nline2 \u2028 </script>",
            };
            var connectivity = new ConnectivitySnapshot { Internet = InternetState.Down, Kedge = true, KedgeReason = "forced", Mode = KedgeMode.On, Reached = new List<string> { "1.1.1.1:443" } };
            var value = new
            {
                record,
                connectivity,
                stats = new TargetStats { Target = "cloud/big", Requests = 3, EwmaFirstByteMs = 12.5, Cost = 1.10m },
                breaker = (FailureKind?)FailureKind.RateLimited,
                none = (string)null,
                launcher = new Dictionary<string, object> { { "PowerShell", "5.1.19041" }, { "tray", true }, { "count", 2 } },
                ratio = 0.1 + 0.2,
                big = long.MaxValue,
            };
            Assert.Equal(N.JsonConvert.SerializeObject(value, NApi), Host.Json.Serialize(value));
        }

        [Theory]
        [InlineData("{\"model\":\"gpt-4.1\",\"messages\":[{\"role\":\"user\",\"content\":\"h\\u00e9llo \\\"q\\\" \\\\ \\n \\t \\u0001 \\ud83d\\ude00\"}],\"temperature\":0.7,\"max_tokens\":100,\"stream\":true,\"stop\":null}")]
        [InlineData("{\"a\":1.0,\"b\":1e5,\"c\":-0.0,\"d\":12345678901234567890,\"e\":3.14159265358979,\"f\":[],\"g\":{},\"h\":[true,false,null]}")]
        [InlineData("{\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get\",\"parameters\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\",\"enum\":[\"a\",\"b\"]}}}}}]}")]
        [InlineData("[{\"nested\":[[1,[2,[3]]]]},\"text\",42]")]
        public void BodiesRoundTripAsNewtonsoftWroteThem(string json)
        {
            var mine = JToken.Parse(json);
            var theirs = NLinq.JToken.Parse(json);
            Assert.Equal(theirs.ToString(N.Formatting.None), mine.ToString(Formatting.None));
            Assert.Equal(theirs.ToString(N.Formatting.Indented), mine.ToString(Formatting.Indented));
        }

        [Fact]
        public void ATokenPutInASecondContainerIsCopiedAsInNewtonsoft()
        {
            var source = JObject.Parse("{\"stop\":[\"a\"],\"schema\":{\"type\":\"object\"}}");
            var target = new JObject();
            target["stop_sequences"] = source["stop"];
            target["input_schema"] = source["schema"];
            ((JArray)target["stop_sequences"]).Add("b");
            ((JObject)target["input_schema"])["properties"] = new JObject();
            Assert.Equal("{\"stop\":[\"a\"],\"schema\":{\"type\":\"object\"}}", source.ToString(Formatting.None));
            Assert.Equal("{\"stop_sequences\":[\"a\",\"b\"],\"input_schema\":{\"type\":\"object\",\"properties\":{}}}", target.ToString(Formatting.None));
            Assert.Same(target, target["stop_sequences"].Parent);
            Assert.Same(source, source["stop"].Parent);
        }

        [Fact]
        public void ValueOfTReadsLikeNewtonsoft()
        {
            const string json = "{\"stream\":true,\"n\":\"5\",\"f\":2.5,\"s\":7,\"nul\":null}";
            var mine = JObject.Parse(json);
            var theirs = NLinq.JObject.Parse(json);
            Assert.Equal(theirs.Value<bool?>("stream"), mine.Value<bool?>("stream"));
            Assert.Equal(theirs.Value<int?>("n"), mine.Value<int?>("n"));
            Assert.Equal(theirs.Value<double>("f"), mine.Value<double>("f"));
            Assert.Equal(theirs.Value<string>("s"), mine.Value<string>("s"));
            Assert.Equal(theirs.Value<bool?>("nul"), mine.Value<bool?>("nul"));
            Assert.Equal(theirs.Value<string>("missing"), mine.Value<string>("missing"));
        }
    }
}
