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
using Fairlead.Core.Protocol;
using Fairlead.Core.Providers;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>
    /// The AI gateway model file in the gateway's own format (environment / endpoints / models), with made-up hosts,
    /// tokens and model ids. Every test uses its own environment variable names, so tests can run in parallel.
    /// </summary>
    internal sealed class GatewayFixture
    {
        public readonly string BaseVar = "FL_TEST_BASE_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
        public readonly string TokenVar = "FL_TEST_TOKEN_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
        public const string BaseUrl = "https://gateway.test/";
        public const string Token = "tok-made-up-123456";

        public Dictionary<string, string> Env()
        {
            return new Dictionary<string, string> { { BaseVar, BaseUrl }, { TokenVar, Token } };
        }

        public Func<string, string> Lookup(Dictionary<string, string> env)
        {
            return name => { string v; return env.TryGetValue(name, out v) ? v : null; };
        }

        public void Export()
        {
            Environment.SetEnvironmentVariable(BaseVar, BaseUrl);
            Environment.SetEnvironmentVariable(TokenVar, Token);
        }

        public void Unexport()
        {
            Environment.SetEnvironmentVariable(BaseVar, null);
            Environment.SetEnvironmentVariable(TokenVar, null);
        }

        private string Endpoint(string key, string path)
        {
            return
"  " + key + ":\n" +
"    method: \"POST\"\n" +
"    # Final URL shape: ${BASE}" + path + "\n" +
"    base_url_env: \"" + BaseVar + "\"\n" +
"    path: \"" + path + "\"\n" +
"    headers:\n" +
"      Content-Type: \"application/json\"\n" +
"      Authorization:\n" +
"        scheme: \"Bearer\"\n" +
"        token_env: \"" + TokenVar + "\"\n" +
"    request_format: \"openai_responses_v1\"\n" +
"    request_body:\n" +
"      required_fields:\n" +
"        - \"model\"\n" +
"        - \"input\"\n" +
"      optional_fields:\n" +
"        - \"max_output_tokens\"\n" +
"      template:\n" +
"        model: \"<configured provider model ID>\"\n" +
"        max_output_tokens: \"<integer>\"\n" +
"        input:\n" +
"          - role: \"user\"\n" +
"            content:\n" +
"              - type: \"input_text\"\n" +
"                text: \"<user prompt>\"\n";
        }

        public string Yaml(bool modelBEnabled = true)
        {
            return
"# The application must obtain this value from its runtime environment.\n" +
"environment:\n" +
"  required:\n" +
"    gateway_base_url:\n" +
"      env: \"" + BaseVar + "\"\n" +
"\n" +
"    bearer_token:\n" +
"      env: \"" + TokenVar + "\"\n" +
"\n" +
"endpoints:\n" +
Endpoint("gw_openai", "/ai-gateway/openai/v1/responses") +
"\n" +
Endpoint("gw_mlflow", "/ai-gateway/mlflow/v1/responses") +
"\n" +
"models:\n" +
"  model_a:\n" +
"    model_id: \"fake.model-a\"\n" +
"    endpoint: \"gw_openai\"\n" +
"    enabled: true\n" +
"\n" +
"  model_b:\n" +
"    model_id: \"fake.model-b\"\n" +
"    endpoint: \"gw_mlflow\"\n" +
"    enabled: " + (modelBEnabled ? "true" : "false") + "\n" +
"\n" +
"  model_c:\n" +
"    model_id: \"fake.model-c\"\n" +
"    endpoint: \"gw_mlflow\"\n" +
"    enabled: false\n";
        }

        public static JObject ResponsesAnswer(string text, string status = "completed")
        {
            return new JObject
            {
                ["id"] = "resp_1",
                ["object"] = "response",
                ["created_at"] = 1761500000,
                ["status"] = status,
                ["output"] = new JArray
                {
                    new JObject { ["type"] = "reasoning", ["summary"] = new JArray() },
                    new JObject
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new JArray { new JObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JArray() } },
                    },
                },
                ["usage"] = new JObject { ["input_tokens"] = 12, ["output_tokens"] = 5, ["total_tokens"] = 17 },
            };
        }
    }

    public class YamlReaderTests
    {
        [Fact]
        public void ReadsTheGatewayFileShape()
        {
            var fx = new GatewayFixture();
            var doc = (JObject)YamlReader.Parse(fx.Yaml());
            Assert.Equal(fx.BaseVar, (string)doc["environment"]!["required"]!["gateway_base_url"]!["env"]);
            var ep = doc["endpoints"]!["gw_openai"]!;
            Assert.Equal("/ai-gateway/openai/v1/responses", (string)ep["path"]);
            Assert.Equal("Bearer", (string)ep["headers"]!["Authorization"]!["scheme"]);
            Assert.Equal(new[] { "model", "input" }, ((JArray)ep["request_body"]!["required_fields"]!).Select(x => (string)x!).ToArray());
            var input = (JArray)ep["request_body"]!["template"]!["input"]!;
            Assert.Equal("user", (string)input[0]["role"]);
            Assert.Equal("input_text", (string)input[0]["content"]![0]["type"]);
            Assert.Equal(JTokenType.Boolean, doc["models"]!["model_a"]!["enabled"]!.Type);
            Assert.False((bool)doc["models"]!["model_c"]!["enabled"]!);
        }

        [Fact]
        public void HandlesScalarsCommentsAndFlowCollections()
        {
            var doc = (JObject)YamlReader.Parse(
                "a: plain text # a comment\n" +
                "b: \"has # inside\"\n" +
                "c: 'it''s'\n" +
                "d: 42\n" +
                "e: 1.5\n" +
                "f: ~\n" +
                "g: [x, \"y\", 3]\n" +
                "h: {k: v}\n" +
                "i:\n" +
                "- one\n" +
                "- two\n" +
                "j: \"tab\\tline\\u0041\"\n");
            Assert.Equal("plain text", (string)doc["a"]);
            Assert.Equal("has # inside", (string)doc["b"]);
            Assert.Equal("it's", (string)doc["c"]);
            Assert.Equal(JTokenType.Integer, doc["d"]!.Type);
            Assert.Equal(1.5, (double)doc["e"]!);
            Assert.Equal(JTokenType.Null, doc["f"]!.Type);
            Assert.Equal(3, ((JArray)doc["g"]!).Count);
            Assert.Equal("v", (string)doc["h"]!["k"]);
            Assert.Equal(new[] { "one", "two" }, ((JArray)doc["i"]!).Select(x => (string)x!).ToArray());
            Assert.Equal("tab\tlineA", (string)doc["j"]);
        }

        [Fact]
        public void ReadsLiteralAndFoldedBlockScalars()
        {
            var doc = (JObject)YamlReader.Parse(
                "lit: |\n" +
                "  line one\n" +
                "    indented # not a comment\n" +
                "\n" +
                "  line three\n" +
                "fold: >\n" +
                "  joined\n" +
                "  words\n" +
                "\n" +
                "  new paragraph\n" +
                "strip: |-\n" +
                "  no newline\n" +
                "keep: |+\n" +
                "  kept\n" +
                "\n" +
                "explicit: |2\n" +
                "    four spaces\n" +
                "empty: |\n" +
                "list:\n" +
                "- >-\n" +
                "  folded item\n" +
                "  here\n" +
                "- plain\n" +
                "nested:\n" +
                "  description: >\n" +
                "    Routes to the\n" +
                "    responses API.\n" +
                "  path: \"/x\"\n" +
                "after: 1\n");
            Assert.Equal("line one\n  indented # not a comment\n\nline three\n", (string)doc["lit"]);
            Assert.Equal("joined words\nnew paragraph\n", (string)doc["fold"]);
            Assert.Equal("no newline", (string)doc["strip"]);
            Assert.Equal("kept\n\n", (string)doc["keep"]);
            Assert.Equal("  four spaces\n", (string)doc["explicit"]);
            Assert.Equal("", (string)doc["empty"]);
            Assert.Equal(new[] { "folded item here", "plain" }, ((JArray)doc["list"]!).Select(x => (string)x!).ToArray());
            Assert.Equal("Routes to the responses API.\n", (string)doc["nested"]!["description"]);
            Assert.Equal("/x", (string)doc["nested"]!["path"]);
            Assert.Equal(1L, (long)doc["after"]!);
        }

        [Fact]
        public void AGatewayFileWithBlockScalarDescriptionsLoads()
        {
            var fx = new GatewayFixture();
            var yaml = fx.Yaml().Replace("endpoints:\n", "endpoints:\n  # notes\n") + "notes: |\n  Free text: with a colon\n  - and a dash\n";
            yaml = yaml.Replace("    method: \"POST\"\n", "    description: >\n      Calls the gateway\n      over HTTPS.\n    method: \"POST\"\n");
            var f = GatewayModelFile.Parse(yaml, "x", fx.Lookup(fx.Env()));
            Assert.True(f.Ok, string.Join("; ", f.Errors));
            Assert.Equal(2, f.Providers.Count);
        }

        [Theory]
        [InlineData("a:\n\tb: 1\n", "tabs")]
        [InlineData("a: &x 1\nb: *x\n", "anchors")]
        [InlineData("a: |x\n  text\n", "block scalar indicator")]
        [InlineData("a: |\n  ok\n\tb: 1\n", "tabs")]
        [InlineData("a: 1\na: 2\n", "appears twice")]
        [InlineData("a: 1\n---\nb: 2\n", "one document")]
        [InlineData("a: \"open\n", "unterminated")]
        [InlineData("a:\n  b: 1\n    c: 2\n", "indentation")]
        public void RefusesWhatItDoesNotSupportWithALineNumber(string yaml, string expected)
        {
            var ex = Assert.Throws<JsonException>(() => YamlReader.Parse(yaml));
            Assert.Contains("line", ex.Message);
            Assert.Contains(expected, ex.Message);
        }
    }

    public class GatewayModelFileTests
    {
        [Fact]
        public void BuildsOneManagedResponsesProviderPerEndpointWithEnabledModels()
        {
            var fx = new GatewayFixture();
            var f = GatewayModelFile.Parse(fx.Yaml(), "test.yaml", fx.Lookup(fx.Env()));
            Assert.True(f.Ok, string.Join("\n", f.Errors));
            Assert.Equal(new[] { "gw_openai", "gw_mlflow" }, f.Providers.Select(p => p.Id).ToArray());
            var a = f.Providers[0];
            Assert.Equal(ProviderKind.Responses, a.Kind);
            Assert.Equal("https://gateway.test", a.BaseUrl);
            Assert.Equal("/ai-gateway/openai/v1/responses", a.Path);
            Assert.Equal(fx.TokenVar, a.ApiKeyEnv);
            Assert.True(a.Managed);
            Assert.False(a.Local);
            Assert.Equal(GatewayModelFile.DefaultRetries, a.Retries);
            Assert.Equal(new[] { "max_output_tokens" }, a.RequestFields!.ToArray());
            Assert.Equal(new[] { "fake.model-a" }, a.Models.Select(m => m.Id).ToArray());
            Assert.All(f.Providers.SelectMany(p => p.Models), m => { Assert.False(m.Tools); Assert.False(m.Vision); });
            Assert.Equal(new[] { "fake.model-b" }, f.Providers[1].Models.Select(m => m.Id).ToArray());
            Assert.Equal(2, f.EnabledModels);
            Assert.Contains(fx.BaseVar, f.RequiredVariables);
            Assert.Contains(fx.TokenVar, f.RequiredVariables);
        }

        [Fact]
        public void AnEndpointWithNoEnabledModelIsLeftOut()
        {
            var fx = new GatewayFixture();
            var f = GatewayModelFile.Parse(fx.Yaml(modelBEnabled: false), "test.yaml", fx.Lookup(fx.Env()));
            Assert.True(f.Ok);
            Assert.Equal(new[] { "gw_openai" }, f.Providers.Select(p => p.Id).ToArray());
            Assert.Contains(f.Warnings, w => w.Contains("gw_mlflow"));
        }

        [Fact]
        public void MissingEnvironmentVariablesAreNamedAndNothingIsAdded()
        {
            var fx = new GatewayFixture();
            var f = GatewayModelFile.Parse(fx.Yaml(), "test.yaml", n => null);
            Assert.False(f.Ok);
            Assert.Empty(f.Providers);
            Assert.Contains(f.Errors, e => e.Contains(fx.BaseVar) && e.Contains("not set"));
            Assert.Contains(f.Errors, e => e.Contains(fx.TokenVar) && e.Contains("not set"));
        }

        [Fact]
        public void ErrorsNeverQuoteTheBaseUrlOrToken()
        {
            var fx = new GatewayFixture();
            var env = fx.Env();
            env[fx.BaseVar] = "http://secret-host.example.invalid/?q=1";
            var f = GatewayModelFile.Parse(fx.Yaml().Replace("fake.model-a", "fake.model-b").Replace("enabled: false", "enabled: maybe"), "test.yaml", fx.Lookup(env));
            Assert.False(f.Ok);
            var all = string.Join("\n", f.Errors.Concat(f.Warnings).Concat(f.Summary()));
            Assert.Contains("must hold an absolute https URL", all);
            Assert.DoesNotContain("secret-host", all);
            Assert.DoesNotContain(GatewayFixture.Token, all);
            Assert.DoesNotContain("fake.model", all);
        }

        [Theory]
        [InlineData("endpoint: \"gw_openai\"", "endpoint: \"nowhere\"", "refers to 'nowhere'")]
        [InlineData("request_format: \"openai_responses_v1\"", "request_format: \"chat_v9\"", "is not supported")]
        [InlineData("method: \"POST\"", "method: \"GET\"", "must be POST")]
        [InlineData("path: \"/ai-gateway/openai/v1/responses\"", "path: \"https://elsewhere/x\"", "must be a path")]
        [InlineData("        - \"input\"", "        - \"input\"\n        - \"user\"", "cannot fill 'user'")]
        [InlineData("    enabled: true\n\n  model_b", "    enabled: \"yes\"\n\n  model_b", "enabled must be true or false")]
        public void ValidatesReferencesAndContract(string from, string to, string expected)
        {
            var fx = new GatewayFixture();
            var yaml = fx.Yaml();
            var at = yaml.IndexOf(from, StringComparison.Ordinal);
            Assert.True(at >= 0, "fixture text not found: " + from);
            yaml = yaml.Substring(0, at) + to + yaml.Substring(at + from.Length);
            var f = GatewayModelFile.Parse(yaml, "test.yaml", fx.Lookup(fx.Env()));
            Assert.False(f.Ok);
            Assert.Contains(f.Errors, e => e.Contains(expected));
            Assert.Empty(f.Providers);
        }

        [Fact]
        public void RefusesTokensWrittenIntoTheFile()
        {
            var fx = new GatewayFixture();
            var yaml = fx.Yaml().Replace("      Authorization:\n        scheme: \"Bearer\"\n        token_env: \"" + fx.TokenVar + "\"\n", "      Authorization: \"Bearer abc\"\n      X-Api-Key: \"abc\"\n");
            var f = GatewayModelFile.Parse(yaml, "test.yaml", fx.Lookup(fx.Env()));
            Assert.False(f.Ok);
            Assert.Contains(f.Errors, e => e.Contains("Authorization must be a mapping"));
            Assert.Contains(f.Errors, e => e.Contains("X-Api-Key looks like a credential"));
        }

        [Fact]
        public void UnknownSectionsAndOptionalFieldsAreNotedNotSent()
        {
            var fx = new GatewayFixture();
            var yaml = fx.Yaml().Replace("        - \"max_output_tokens\"\n", "        - \"max_output_tokens\"\n        - \"reasoning\"\n") + "routes:\n  balanced: model_a\n";
            var f = GatewayModelFile.Parse(yaml, "test.yaml", fx.Lookup(fx.Env()));
            Assert.True(f.Ok, string.Join("\n", f.Errors));
            Assert.Contains(f.Warnings, w => w.Contains("'routes'"));
            Assert.Contains(f.Warnings, w => w.Contains("'reasoning'"));
            Assert.Equal(new[] { "max_output_tokens" }, f.Providers[0].RequestFields!.ToArray());
        }

        [Fact]
        public void TimeoutComesFromTheEnvironmentWhenSet()
        {
            var fx = new GatewayFixture();
            var env = fx.Env();
            env[GatewayModelFile.TimeoutVariable] = "45";
            Assert.All(GatewayModelFile.Parse(fx.Yaml(), "t", fx.Lookup(env)).Providers, p => Assert.Equal(45, p.TimeoutSeconds));
            env[GatewayModelFile.TimeoutVariable] = "forever";
            Assert.False(GatewayModelFile.Parse(fx.Yaml(), "t", fx.Lookup(env)).Ok);
        }

        [Fact]
        public void TheCommittedExampleIsValid()
        {
            var path = Path.Combine(CoreSourceTests.RepoRoot(), "config", "ai_gateway_models.example.yaml");
            var env = new Dictionary<string, string> { { "AI_GATEWAY_BASE_URL", "https://approved-gateway.example" }, { "DATABRICKS_TOKEN", "x" } };
            var f = GatewayModelFile.Load(path, n => { string v; return env.TryGetValue(n, out v) ? v : null; });
            Assert.True(f.Ok, string.Join("\n", f.Errors));
            Assert.Equal(new[] { "openai_responses", "mlflow_responses" }, f.Providers.Select(p => p.Id).ToArray());
            Assert.Equal(2, f.EnabledModels);
        }

        [Fact]
        public void PathComesFromTheVariableOrTheDefaultFile()
        {
            var root = Path.Combine(Path.GetTempPath(), "fl-gw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            try
            {
                Assert.Null(GatewayModelFile.ResolvePath(root, n => null));
                File.WriteAllText(Path.Combine(root, "config", "ai_gateway_models.yaml"), "models: {}");
                Assert.Equal(Path.Combine(root, "config", "ai_gateway_models.yaml"), GatewayModelFile.ResolvePath(root, n => null));
                Assert.Equal(Path.Combine(root, "elsewhere.yaml"), GatewayModelFile.ResolvePath(root, n => n == GatewayModelFile.PathVariable ? "elsewhere.yaml" : null));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    public class ResponsesApiTests
    {
        [Fact]
        public void BuildsTheEndpointUrlFromBaseAndPath()
        {
            var p = new ProviderConfig { Id = "g", Kind = ProviderKind.Responses, BaseUrl = "https://gateway.test/", Path = "/ai-gateway/mlflow/v1/responses" };
            Assert.Equal("https://gateway.test/ai-gateway/mlflow/v1/responses", UpstreamClient.EndpointUri(p, "chat").ToString());
            Assert.Throws<NotSupportedException>(() => UpstreamClient.EndpointUri(p, "embeddings"));
        }

        [Fact]
        public void BuildsTheResponsesRequestFromAChatRequest()
        {
            var chat = JObject.Parse("{\"model\":\"route\",\"stream\":true,\"max_tokens\":256,\"temperature\":0.2,\"messages\":[{\"role\":\"system\",\"content\":\"Be brief.\"},{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Hello!\"}]},{\"role\":\"assistant\",\"content\":\"Hi.\"},{\"role\":\"user\",\"content\":\"Again\"}]}");
            var body = ResponsesApi.FromChatRequest(chat, "fake.model-a", new List<string> { "max_output_tokens" });
            Assert.Equal("{\"model\":\"fake.model-a\",\"input\":[" +
                "{\"role\":\"system\",\"content\":[{\"type\":\"input_text\",\"text\":\"Be brief.\"}]}," +
                "{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Hello!\"}]}," +
                "{\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hi.\"}]}," +
                "{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Again\"}]}]," +
                "\"max_output_tokens\":256}", body.ToString(Formatting.None));
            // No stream, temperature or other field the endpoint did not list.
            Assert.Null(ResponsesApi.FromChatRequest(chat, "m", null)["max_output_tokens"]);
            Assert.Equal(0.2, (double)ResponsesApi.FromChatRequest(chat, "m", new List<string> { "temperature" })["temperature"]!);
        }

        [Theory]
        [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"f\"}}]}", "tool calls")]
        [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:x\"}}]}]}", "image input")]
        [InlineData("{\"messages\":[{\"role\":\"tool\",\"tool_call_id\":\"1\",\"content\":\"x\"}]}", "tool calls")]
        [InlineData("{\"messages\":[]}", "no text")]
        public void RefusesWhatIsNotVerified(string json, string expected)
        {
            var ex = Assert.Throws<ResponsesRequestException>(() => ResponsesApi.FromChatRequest(JObject.Parse(json), "m", null));
            Assert.Contains(expected, ex.Message);
        }

        [Fact]
        public void TranslatesTheAnswerToChatCompletions()
        {
            var c = ResponsesApi.ToChatCompletion(GatewayFixture.ResponsesAnswer("Hello back"), "route-name")!;
            Assert.Equal("Hello back", (string)c["choices"]![0]["message"]!["content"]);
            Assert.Equal("stop", (string)c["choices"]![0]["finish_reason"]);
            Assert.Equal("route-name", (string)c["model"]);
            Assert.Equal(12, (int)c["usage"]!["prompt_tokens"]!);
            Assert.Equal(5, (int)c["usage"]!["completion_tokens"]!);
            Assert.Equal(17, (int)c["usage"]!["total_tokens"]!);

            var cut = GatewayFixture.ResponsesAnswer("partial", "incomplete");
            cut["incomplete_details"] = new JObject { ["reason"] = "max_output_tokens" };
            Assert.Equal("length", (string)ResponsesApi.ToChatCompletion(cut, "m")!["choices"]![0]["finish_reason"]);

            Assert.Equal("top level", (string)ResponsesApi.ToChatCompletion(new JObject { ["output_text"] = "top level" }, "m")!["choices"]![0]["message"]!["content"]);
            Assert.Null(ResponsesApi.ToChatCompletion(new JObject { ["status"] = "failed", ["output"] = new JArray() }, "m"));
        }

        [Fact]
        public void TurnsAWholeAnswerIntoAChatStream()
        {
            var c = ResponsesApi.ToChatCompletion(GatewayFixture.ResponsesAnswer("Hi there"), "m")!;
            var sse = ResponsesApi.ToChatStream(c, true);
            var events = sse.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Substring("data: ".Length)).ToList();
            Assert.Equal(4, events.Count);
            Assert.Equal("Hi there", (string)JObject.Parse(events[0])["choices"]![0]["delta"]!["content"]);
            Assert.Equal("stop", (string)JObject.Parse(events[1])["choices"]![0]["finish_reason"]);
            Assert.Equal(5, (int)JObject.Parse(events[2])["usage"]!["completion_tokens"]!);
            Assert.Equal("[DONE]", events[3]);
            Assert.Equal(3, ResponsesApi.ToChatStream(c, false).Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries).Length);
        }
    }

    public class RetryPolicyTests
    {
        [Fact]
        public void BackoffDoublesWithJitterUpToTheCeiling()
        {
            Assert.Equal(250, GatewayEngine.BackoffMs(0, 500, 8000, 0.0));
            Assert.Equal(499, GatewayEngine.BackoffMs(0, 500, 8000, 0.999));
            Assert.Equal(500, GatewayEngine.BackoffMs(1, 500, 8000, 0.0));
            Assert.Equal(1000, GatewayEngine.BackoffMs(2, 500, 8000, 0.0));
            Assert.Equal(4000, GatewayEngine.BackoffMs(10, 500, 8000, 0.0));
            Assert.True(GatewayEngine.BackoffMs(10, 500, 8000, 0.999) < 8000);
        }

        [Fact]
        public void OnlyTransientFailuresAreRetried()
        {
            Assert.True(GatewayEngine.IsTransient(FailureKind.Server));
            Assert.True(GatewayEngine.IsTransient(FailureKind.RateLimited));
            Assert.True(GatewayEngine.IsTransient(FailureKind.Timeout));
            Assert.True(GatewayEngine.IsTransient(FailureKind.Network));
            Assert.False(GatewayEngine.IsTransient(FailureKind.Auth));
            Assert.False(GatewayEngine.IsTransient(FailureKind.BadRequest));
            Assert.False(GatewayEngine.IsTransient(FailureKind.ModelNotFound));
        }
    }

    /// <summary>The gateway file loaded by a real host, called over real loopback sockets with a fake gateway behind it.</summary>
    public sealed class GatewayModelHostTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-gw-" + Guid.NewGuid().ToString("N"));
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly HttpClient _http = new HttpClient();
        private readonly GatewayFixture _fx = new GatewayFixture();

        public GatewayModelHostTests()
        {
            Directory.CreateDirectory(_dir);
            _fx.Export();
        }

        public void Dispose()
        {
            foreach (var h in _hosts) h.Dispose();
            _http.Dispose();
            _fx.Unexport();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private FairleadHost Start(FakeUpstream fake, out string key, string? yaml = null, string? configPath = null)
        {
            var file = Path.Combine(_dir, "ai_gateway_models.yaml");
            File.WriteAllText(file, yaml ?? _fx.Yaml());
            var cfg = RoutingTests.Config();
            cfg.Routes.Add(new RouteConfig { Id = "balanced", Strategy = RouteStrategy.Failover, Targets = new List<string> { "gw_openai/fake.model-a", "gw_mlflow/fake.model-b" }, LocalFallback = false });
            key = GatewayEngine.NewClientKey();
            cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = key.Substring(0, 7), Hash = GatewayEngine.HashKey(key) });
            var host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") },
                Config = configPath == null ? cfg : null,
                GatewayModelPath = file,
                UpstreamHandler = fake,
                Monitor = false,
                PreferredPort = 0,
            });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private Task<HttpResponseMessage> Post(FairleadHost host, string path, JObject body, string key)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + path) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            return _http.SendAsync(req);
        }

        private static JObject Chat(string model, string prompt, bool stream = false)
        {
            return new JObject { ["model"] = model, ["stream"] = stream, ["max_tokens"] = 64, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } } };
        }

        private static HttpResponseMessage Status(HttpStatusCode code, string message = "nope")
        {
            return FakeUpstream.Json(code, new JObject { ["error"] = new JObject { ["message"] = message } });
        }

        [Fact]
        public async Task CallsTheConfiguredEndpointWithTheBearerTokenAndTheModelId()
        {
            HttpRequestMessage? seen = null;
            var fake = new FakeUpstream().On("gateway.test", (r, b) => { seen = r; return FakeUpstream.Json(HttpStatusCode.OK, GatewayFixture.ResponsesAnswer("Hello!")); });
            var host = Start(fake, out var key);
            Assert.True(host.GatewayModels!.Ok, string.Join("\n", host.GatewayModels.Errors));
            var r = await Post(host, "/v1/chat/completions", Chat("gw_mlflow/fake.model-b", "Hi"), key);
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("Hello!", (string)JObject.Parse(text)["choices"]![0]["message"]!["content"]);
            Assert.Equal("gw_mlflow/fake.model-b", r.Headers.GetValues("X-Fairlead-Target").Single());
            Assert.Equal("https://gateway.test/ai-gateway/mlflow/v1/responses", seen!.RequestUri!.ToString());
            Assert.Equal("Bearer " + GatewayFixture.Token, seen.Headers.Authorization!.ToString());
            var sent = fake.Seen.Single().Value;
            Assert.Equal("fake.model-b", (string)sent["model"]);
            Assert.Equal(64, (int)sent["max_output_tokens"]!);
            Assert.Null(sent["stream"]);
            Assert.Null(sent["messages"]);
        }

        /// <summary>config/ai_gateway_models.template.yaml with its placeholders filled in, as GenAI.mil will.</summary>
        private string FilledTemplate()
        {
            var text = File.ReadAllText(Path.Combine(CoreSourceTests.RepoRoot(), "config", "ai_gateway_models.template.yaml"));
            return text.Replace("<OPENAI_RESPONSES_URL>", "https://gateway.test/ai-gateway/openai/v1/responses")
                       .Replace("<MLFLOW_RESPONSES_URL>", "https://gateway.test/ai-gateway/mlflow/v1/responses")
                       .Replace("DATABRICKS_TOKEN", _fx.TokenVar);
        }

        [Fact]
        public void TheTemplateIsRefusedUntilItsPlaceholdersAreFilled()
        {
            var text = File.ReadAllText(Path.Combine(CoreSourceTests.RepoRoot(), "config", "ai_gateway_models.template.yaml"));
            var f = GatewayModelFile.Parse(text, "t", n => "x");
            Assert.False(f.Ok);
            Assert.Contains(f.Errors, e => e.Contains("endpoints.openai_responses.url still holds a placeholder"));
            Assert.Contains(f.Errors, e => e.Contains("endpoints.mlflow_responses.url still holds a placeholder"));
        }

        [Fact]
        public async Task TheFilledTemplateSendsWhatTheCurlsSend()
        {
            var fake = new FakeUpstream().On("gateway.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, GatewayFixture.ResponsesAnswer("Databricks is a data platform.")));
            var host = Start(fake, out var key, yaml: FilledTemplate());
            var f = host.GatewayModels!;
            Assert.True(f.Ok, string.Join("\n", f.Errors));
            Assert.Empty(f.BaseUrlVariables);   // the address is in the file: nothing to enter
            Assert.Equal(new[] { "openai_responses", "mlflow_responses" }, f.Providers.Select(p => p.Id).ToArray());
            Assert.Equal(9, f.EnabledModels);

            // The conversation from model-curls.md, sent through Fairlead's OpenAI API.
            var body = new JObject
            {
                ["model"] = "openai_responses/system.ai.grok-4-6",
                ["max_tokens"] = 256,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "user", ["content"] = "Hello!" },
                    new JObject { ["role"] = "assistant", ["content"] = "Hello! How can I assist you today?" },
                    new JObject { ["role"] = "user", ["content"] = "What is Databricks?" },
                },
            };
            var r = await Post(host, "/v1/chat/completions", body, key);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var seen = fake.Seen.Single();
            Assert.Equal("gateway.test/ai-gateway/openai/v1/responses", seen.Key);
            var expected = JObject.Parse(@"{
              ""model"": ""system.ai.grok-4-6"",
              ""input"": [
                { ""role"": ""user"", ""content"": [{ ""type"": ""input_text"", ""text"": ""Hello!"" }] },
                { ""role"": ""assistant"", ""content"": [{ ""type"": ""output_text"", ""text"": ""Hello! How can I assist you today?"" }] },
                { ""role"": ""user"", ""content"": [{ ""type"": ""input_text"", ""text"": ""What is Databricks?"" }] }
              ],
              ""max_output_tokens"": 256
            }");
            Assert.Equal(expected.ToString(Formatting.None), seen.Value.ToString(Formatting.None));

            var r2 = await Post(host, "/v1/chat/completions", Chat("mlflow_responses/system.ai.gpt-5-4", "Hi"), key);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
            Assert.Equal("gateway.test/ai-gateway/mlflow/v1/responses", fake.Seen.Last().Key);
        }

        [Fact]
        public async Task StreamsAWholeAnswerToStreamingClients()
        {
            var fake = new FakeUpstream().On("gateway.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, GatewayFixture.ResponsesAnswer("Streamed")));
            var host = Start(fake, out var key);
            var r = await Post(host, "/v1/chat/completions", Chat("balanced", "Hi", stream: true), key);
            var text = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("Streamed", text);
            Assert.Contains("data: [DONE]", text);
        }

        [Fact]
        public async Task AnthropicClientsReachTheGatewayToo()
        {
            var fake = new FakeUpstream().On("gateway.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, GatewayFixture.ResponsesAnswer("From the gateway")));
            var host = Start(fake, out var key);
            var body = new JObject { ["model"] = "balanced", ["max_tokens"] = 50, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "Hi" } } };
            var r = await Post(host, "/v1/messages", body, key);
            var answer = JObject.Parse(await r.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("From the gateway", (string)answer["content"]![0]["text"]);
        }

        [Fact]
        public async Task RetriesTransientFailuresOnTheSameTargetThenSucceeds()
        {
            var calls = 0;
            var fake = new FakeUpstream().On("gateway.test", (r, b) => ++calls < 3 ? Status(HttpStatusCode.ServiceUnavailable, "busy") : FakeUpstream.Json(HttpStatusCode.OK, GatewayFixture.ResponsesAnswer("third time")));
            var host = Start(fake, out var key);
            var r = await Post(host, "/v1/chat/completions", Chat("gw_openai/fake.model-a", "Hi"), key);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(3, calls);
            Assert.Equal("1", r.Headers.GetValues("X-Fairlead-Attempts").Single());
            var rec = host.Metrics.Find(r.Headers.GetValues("X-Fairlead-Request-Id").Single())!;
            Assert.Equal(2, rec.Attempts.Single().Retries);
        }

        [Fact]
        public async Task NeverRetriesAnAuthFailureAndFallsBackVisibly()
        {
            var fake = new FakeUpstream().On("gateway.test", (r, b) =>
                (string)b["model"] == "fake.model-a" ? Status(HttpStatusCode.Unauthorized, "bad token") : FakeUpstream.Json(HttpStatusCode.OK, GatewayFixture.ResponsesAnswer("fallback model")));
            var host = Start(fake, out var key);
            var r = await Post(host, "/v1/chat/completions", Chat("balanced", "Hi"), key);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(1, fake.Seen.Count(s => (string)s.Value["model"] == "fake.model-a"));
            Assert.Equal("gw_mlflow/fake.model-b", r.Headers.GetValues("X-Fairlead-Target").Single());
            Assert.Equal("2", r.Headers.GetValues("X-Fairlead-Attempts").Single());
            var rec = host.Metrics.Find(r.Headers.GetValues("X-Fairlead-Request-Id").Single())!;
            Assert.True(rec.Failover);
            Assert.Equal(new[] { "auth", "ok" }, rec.Attempts.Select(a => a.Outcome).ToArray());
            Assert.Equal(0, rec.Attempts[0].Retries);
        }

        [Fact]
        public async Task ABadRequestIsNotRetriedOrFailedOver()
        {
            var fake = new FakeUpstream().On("gateway.test", (r, b) => Status(HttpStatusCode.BadRequest, "bad input"));
            var host = Start(fake, out var key);
            var r = await Post(host, "/v1/chat/completions", Chat("balanced", "Hi"), key);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Single(fake.Seen);
        }

        [Fact]
        public async Task RequestsWithToolsSkipGatewayModels()
        {
            var fake = new FakeUpstream().On("gateway.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, GatewayFixture.ResponsesAnswer("x")));
            var host = Start(fake, out var key);
            var body = Chat("balanced", "Hi");
            body["tools"] = new JArray { new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = "f", ["parameters"] = new JObject() } } };
            var r = await Post(host, "/v1/chat/completions", body, key);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.Empty(fake.Seen);
        }

        [Fact]
        public async Task GatewayProvidersAreShownButNeverSavedOrEditable()
        {
            var path = Path.Combine(_dir, "fairlead.json");
            var fake = new FakeUpstream();
            var host = Start(fake, out var key, configPath: path);
            var view = host.Config.Snapshot();
            Assert.True(view.FindProvider("gw_openai")!.Managed);
            host.Config.Update(c => c.LogContent = true);
            var saved = File.ReadAllText(Path.Combine(_dir, "fairlead.json"));
            Assert.DoesNotContain("gw_openai", saved);
            Assert.DoesNotContain("gateway.test", saved);
            Assert.DoesNotContain("managed", saved);
            Assert.NotNull(host.Config.Snapshot().FindProvider("gw_openai"));

            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/api/providers") { Content = new StringContent("{\"provider\":{\"id\":\"gw_openai\",\"name\":\"x\"}}", Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Fairlead-Session", host.SessionToken);
            var r = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("AI gateway model file", await r.Content.ReadAsStringAsync());
        }

        [Fact]
        public void AnInvalidFileAddsNothingAndTheHostStillStarts()
        {
            var host = Start(new FakeUpstream(), out _, yaml: _fx.Yaml().Replace("endpoint: \"gw_openai\"", "endpoint: \"gone\""));
            Assert.False(host.GatewayModels!.Ok);
            Assert.Null(host.Config.Snapshot().FindProvider("gw_openai"));
            Assert.NotEqual(0, host.Port);
        }
    }

    /// <summary>The model file imported in the browser: kept by Fairlead, address stored with it, token never.</summary>
    public sealed class GatewayImportTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-imp-" + Guid.NewGuid().ToString("N"));
        private readonly GatewayFixture _fx = new GatewayFixture();
        private readonly HttpClient _http = new HttpClient();
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();

        public GatewayImportTests() { Directory.CreateDirectory(_dir); }

        public void Dispose()
        {
            foreach (var h in _hosts) h.Dispose();
            _http.Dispose();
            _fx.Unexport();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        // Reversible stand-in for DPAPI so the tests run on any OS; the real store uses SecretProtector.
        private static string Wrap(string s) { return "t:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
        private static string Unwrap(string s) { return s != null && s.StartsWith("t:") ? Encoding.UTF8.GetString(Convert.FromBase64String(s.Substring(2))) : null; }

        private FairleadHost Start(GatewayStore store, FakeUpstream? fake = null, string? clientKey = null)
        {
            var cfg = RoutingTests.Config();
            if (clientKey != null) cfg.Clients.Add(new ClientKey { Id = "k1", Name = "tests", Prefix = clientKey.Substring(0, 7), Hash = GatewayEngine.HashKey(clientKey) });
            var host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") },
                Config = cfg,
                GatewayStore = store,
                UpstreamHandler = fake ?? new FakeUpstream(),
                Monitor = false,
                PreferredPort = 0,
            });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private async Task<(HttpStatusCode code, JObject body)> Admin(FairleadHost host, HttpMethod method, string path, JObject? body = null)
        {
            var req = new HttpRequestMessage(method, host.BaseUrl + path);
            if (body != null) req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            req.Headers.Add("X-Fairlead-Session", host.SessionToken);
            var r = await _http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            return (r.StatusCode, string.IsNullOrEmpty(text) ? new JObject() : JObject.Parse(text));
        }

        [Fact]
        public void InspectChecksStructureWithoutAnyValues()
        {
            var f = GatewayModelFile.Inspect(_fx.Yaml(), "x");
            Assert.True(f.Ok);
            Assert.Equal(new[] { _fx.BaseVar }, f.BaseUrlVariables.ToArray());
            Assert.Equal(new[] { _fx.TokenVar }, f.TokenVariables.ToArray());
            Assert.False(GatewayModelFile.Inspect(_fx.Yaml().Replace("endpoint: \"gw_openai\"", "endpoint: \"gone\""), "x").Ok);
        }

        [Fact]
        public async Task ImportStoresTheFileEncryptedAndAsksForTheAddressAndToken()
        {
            var store = new GatewayStore(_dir, Wrap, Unwrap);
            var host = Start(store);
            var (code, body) = await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = _fx.Yaml() });
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.True((bool)body["saved"]!);
            var gw = (JObject)body["gateway"]!;
            Assert.Equal("imported", (string?)gw["source"]);
            Assert.False((bool)gw["ok"]!);
            var vars = ((JArray)gw["variables"]!).ToDictionary(v => (string)v["name"]!, v => (string)v["kind"]! + "/" + (string)v["source"]!);
            Assert.Equal("baseUrl/missing", vars[_fx.BaseVar]);
            Assert.Equal("token/missing", vars[_fx.TokenVar]);

            var onDisk = File.ReadAllText(Path.Combine(_dir, GatewayStore.FilesFolder, "models.dpapi"));
            Assert.DoesNotContain("fake.model", onDisk);
            Assert.DoesNotContain("gw_openai", onDisk);
        }

        [Fact]
        public async Task TheAddressIsStoredTheTokenIsRefusedAndProvidersAppearWithoutARestart()
        {
            var store = new GatewayStore(_dir, Wrap, Unwrap);
            var host = Start(store);
            await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = _fx.Yaml() });

            var (refused, msg) = await Admin(host, HttpMethod.Post, "/api/gateway/values", new JObject { ["values"] = new JObject { [_fx.TokenVar] = GatewayFixture.Token } });
            Assert.Equal(HttpStatusCode.BadRequest, refused);
            Assert.Contains("Lazarette", msg.ToString());

            var (bad, _) = await Admin(host, HttpMethod.Post, "/api/gateway/values", new JObject { ["values"] = new JObject { [_fx.BaseVar] = "http://gateway.test" } });
            Assert.Equal(HttpStatusCode.BadRequest, bad);

            var (ok, view) = await Admin(host, HttpMethod.Post, "/api/gateway/values", new JObject { ["values"] = new JObject { [_fx.BaseVar] = GatewayFixture.BaseUrl } });
            Assert.Equal(HttpStatusCode.OK, ok);
            Assert.DoesNotContain("gateway.test", view.ToString());
            Assert.Null(host.Config.Snapshot().FindProvider("gw_openai"));   // token still missing

            Environment.SetEnvironmentVariable(_fx.TokenVar, GatewayFixture.Token);
            host.LoadGatewayModels();
            Assert.True(host.GatewayModels!.Ok);
            Assert.Equal("https://gateway.test", host.Config.Snapshot().FindProvider("gw_openai")!.BaseUrl);

            // A second start reads the same encrypted files.
            var again = Start(new GatewayStore(_dir, Wrap, Unwrap));
            Assert.NotNull(again.Config.Snapshot().FindProvider("gw_mlflow"));

            var (removed, _) = await Admin(host, HttpMethod.Delete, "/api/gateway");
            Assert.Equal(HttpStatusCode.OK, removed);
            Assert.Null(host.Config.Snapshot().FindProvider("gw_openai"));
            Assert.False(File.Exists(Path.Combine(_dir, GatewayStore.FilesFolder, "models.dpapi")));
        }

        [Fact]
        public async Task ABrokenFileIsRefusedAndNothingIsSaved()
        {
            var host = Start(new GatewayStore(_dir, Wrap, Unwrap));
            var (code, body) = await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = _fx.Yaml().Replace("request_format: \"openai_responses_v1\"", "request_format: \"x\"") });
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.False((bool)body["saved"]!);
            Assert.NotEmpty((JArray)body["errors"]!);
            Assert.False(File.Exists(Path.Combine(_dir, GatewayStore.FilesFolder, "models.dpapi")));
        }
    
        /// <summary>config/ai_gateway_chat_models.template.yaml filled in, with the test token variable.</summary>
        private string FilledChatTemplate(string url)
        {
            var text = File.ReadAllText(Path.Combine(CoreSourceTests.RepoRoot(), "config", "ai_gateway_chat_models.template.yaml"));
            return text.Replace("<CHAT_COMPLETIONS_URL>", url).Replace("AI_ENDPOINT_TOKEN", _fx.TokenVar);
        }

        [Fact]
        public async Task TwoModelFilesLoadSideBySideAndTheChatEndpointGetsTheRequestAsIs()
        {
            _fx.Export();
            var fake = new FakeUpstream().On("chat.test", (r, b) => FakeUpstream.Json(HttpStatusCode.OK, new JObject
            {
                ["id"] = "x", ["object"] = "chat.completion", ["model"] = (string)b["model"]!,
                ["choices"] = new JArray { new JObject { ["index"] = 0, ["message"] = new JObject { ["role"] = "assistant", ["content"] = "Fine, thanks." }, ["finish_reason"] = "stop" } },
            }));
            var key = GatewayEngine.NewClientKey();
            var host = Start(new GatewayStore(_dir, Wrap, Unwrap), fake, key);

            var (c1, b1) = await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = _fx.Yaml(), ["fileName"] = "responses models.yaml" });
            Assert.True((bool)b1["saved"]!, b1.ToString());
            var (c2, b2) = await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = FilledChatTemplate("https://chat.test/v1/chat/completions"), ["fileName"] = "gemini-chat.yaml" });
            Assert.True((bool)b2["saved"]!, b2.ToString());

            var view = (JObject)b2["gateway"]!;
            Assert.True((bool)view["ok"]!, view.ToString());
            Assert.Equal(new[] { "gemini-chat", "responses-models" }, ((JArray)view["files"]!).Select(f => (string)f["name"]!).ToArray());
            var chat = host.Config.Snapshot().FindProvider("gemini_chat")!;
            Assert.Equal(ProviderKind.OpenAi, chat.Kind);
            Assert.Equal("https://chat.test/v1", chat.BaseUrl);   // .../chat/completions is accepted and trimmed
            Assert.NotNull(host.Config.Snapshot().FindProvider("gw_openai"));

            // Tools pass through to the chat endpoint (unlike the Responses endpoints).
            var body = new JObject
            {
                ["model"] = "gemini_chat/gemini-3.5-flash-lite-withtools",
                ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = "Hello, how are you?" } },
                ["tools"] = new JArray { new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = "f", ["parameters"] = new JObject() } } },
            };
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + "/v1/chat/completions") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            var res = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var seen = fake.Seen.Single();
            Assert.Equal("chat.test/v1/chat/completions", seen.Key);
            Assert.Equal("gemini-3.5-flash-lite-withtools", (string)seen.Value["model"]!);
            Assert.NotNull(seen.Value["tools"]);

            // Removing one file leaves the other.
            var (c3, b3) = await Admin(host, HttpMethod.Delete, "/api/gateway/gemini-chat");
            Assert.Equal(HttpStatusCode.OK, c3);
            Assert.Null(host.Config.Snapshot().FindProvider("gemini_chat"));
            Assert.NotNull(host.Config.Snapshot().FindProvider("gw_openai"));
            Assert.Single((JArray)b3["files"]!);

            // A restart reads both encrypted files again.
            await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = FilledChatTemplate("https://chat.test/v1"), ["fileName"] = "gemini-chat.yaml" });
            var again = Start(new GatewayStore(_dir, Wrap, Unwrap));
            Assert.Equal("https://chat.test/v1", again.Config.Snapshot().FindProvider("gemini_chat")!.BaseUrl);
            Assert.NotNull(again.Config.Snapshot().FindProvider("gw_mlflow"));
        }

        [Fact]
        public async Task AnEndpointNameUsedInAnotherFileIsRefused()
        {
            var host = Start(new GatewayStore(_dir, Wrap, Unwrap));
            await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = _fx.Yaml(), ["fileName"] = "first.yaml" });
            var (code, body) = await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = _fx.Yaml(), ["fileName"] = "second.yaml" });
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.False((bool)body["saved"]!);
            Assert.Contains("already in the imported file first", body.ToString());
            // The same file name replaces instead.
            var (_, again) = await Admin(host, HttpMethod.Post, "/api/gateway/import", new JObject { ["yaml"] = _fx.Yaml(), ["fileName"] = "first.yaml" });
            Assert.True((bool)again["saved"]!);
        }

        [Fact]
        public void AnOlderSingleImportIsReadAsTheFileNamedModels()
        {
            File.WriteAllText(Path.Combine(_dir, GatewayStore.FileName), Wrap(_fx.Yaml()));
            var store = new GatewayStore(_dir, Wrap, Unwrap);
            Assert.Equal(new[] { "models" }, store.Names.ToArray());
            Assert.Contains("gw_openai", store.ReadFile("models"));
            Assert.True(store.RemoveFile("models"));
            Assert.False(File.Exists(Path.Combine(_dir, GatewayStore.FileName)));
        }

        [Fact]
        public void TheChatTemplateIsRefusedUntilFilledAndAChatFileNeedsMessages()
        {
            var text = File.ReadAllText(Path.Combine(CoreSourceTests.RepoRoot(), "config", "ai_gateway_chat_models.template.yaml"));
            var f = GatewayModelFile.Parse(text, "t", n => "x");
            Assert.Contains(f.Errors, e => e.Contains("endpoints.gemini_chat.url still holds a placeholder"));
            var wrong = GatewayModelFile.Inspect(FilledChatTemplate("https://chat.test/v1").Replace("- \"messages\"", "- \"input\""), "t");
            Assert.Contains(wrong.Errors, e => e.Contains("must list model and messages"));
        }
    }
}

namespace Fairlead.Core.Tests
{
    public class CurlImportTests
    {
        private const string ResponsesCurl = "curl https://gateway.test/ai-gateway/openai/v1/responses \\\n  -H \"Content-Type: application/json\" \\\n  -H \"Authorization: Bearer $DATABRICKS_TOKEN\" \\\n  -d '{\n    \"model\": \"system.ai.grok-4-6\",\n    \"max_output_tokens\": 256,\n    \"input\": [{\"role\": \"user\", \"content\": [{\"type\": \"input_text\", \"text\": \"Hello!\"}]}]\n  }'";

        [Fact]
        public void ReadsAResponsesCurlIntoAModelFile()
        {
            var c = CurlImport.Parse(ResponsesCurl, null);
            Assert.True(c.Ok, string.Join("; ", c.Errors));
            Assert.Equal("responses", c.Format);
            Assert.Equal("system.ai.grok-4-6", c.Model);
            Assert.Equal("DATABRICKS_TOKEN", c.TokenVariable);
            Assert.Equal("curl_system_ai_grok_4_6", c.EndpointKey);
            var f = GatewayModelFile.Parse(c.Yaml, "x", n => n == "DATABRICKS_TOKEN" ? "t" : null);
            Assert.True(f.Ok, string.Join("; ", f.Errors));
            var p = f.Providers.Single();
            Assert.Equal(ProviderKind.Responses, p.Kind);
            Assert.Equal("https://gateway.test", p.BaseUrl);
            Assert.Equal("/ai-gateway/openai/v1/responses", p.Path);
        }

        [Fact]
        public void ReadsAChatCurlWithDefaultsAndWindowsLineEndings()
        {
            var curl = "curl -X POST https://chat.test/v1/chat/completions ^\r\n  -H \"Authorization: Bearer ${AI_ENDPOINT_TOKEN:-YOUR_API_KEY}\" ^\r\n  -H \"Content-Type: application/json\" ^\r\n  -d \"{\\\"model\\\": \\\"gemini-3.5-flash-lite-withtools\\\", \\\"messages\\\": [{\\\"role\\\": \\\"user\\\", \\\"content\\\": \\\"Hi\\\"}]}\"";
            var c = CurlImport.Parse(curl, null);
            Assert.True(c.Ok, string.Join("; ", c.Errors));
            Assert.Equal("chat", c.Format);
            Assert.Equal("AI_ENDPOINT_TOKEN", c.TokenVariable);
            var f = GatewayModelFile.Parse(c.Yaml, "x", n => n == GatewayModelFile.TimeoutVariable ? null : "t");
            Assert.True(f.Ok, string.Join("; ", f.Errors));
            Assert.Equal("https://chat.test/v1", f.Providers.Single().BaseUrl);
        }

        [Fact]
        public void NeverKeepsALiteralKeyAndRefusesPlaceholders()
        {
            var withKey = ResponsesCurl.Replace("$DATABRICKS_TOKEN", "dapi-made-up-secret-123");
            var c = CurlImport.Parse(withKey, "MY_TOKEN");
            Assert.True(c.Ok);
            Assert.Equal("MY_TOKEN", c.TokenVariable);
            Assert.DoesNotContain("dapi-made-up", c.Yaml);
            Assert.Contains(c.Warnings, w => w.Contains("ignored"));
            Assert.DoesNotContain(c.Warnings, w => w.Contains("dapi-made-up"));

            var placeholder = CurlImport.Parse(ResponsesCurl.Replace("https://gateway.test/ai-gateway/openai/v1/responses", "<OPENAI_RESPONSES_URL>"), null);
            Assert.False(placeholder.Ok);
            Assert.Contains(placeholder.Errors, e => e.Contains("placeholder"));
            Assert.False(CurlImport.Parse("curl https://gateway.test/x", null).Ok);   // no body
            Assert.False(CurlImport.Parse("curl https://gateway.test/x -d 'not json'", null).Ok);
            Assert.False(CurlImport.Parse("curl https://gateway.test/x -d '{\"model\": \"m\", \"input\": []", null).Ok);
        }
    }

    public sealed class GatewayCurlHostTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fairlead-curl-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http = new HttpClient();
        private readonly List<FairleadHost> _hosts = new List<FairleadHost>();
        private readonly string _tokenVar = "FL_CURL_TOKEN_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();

        public GatewayCurlHostTests() { Directory.CreateDirectory(_dir); }

        public void Dispose()
        {
            foreach (var h in _hosts) h.Dispose();
            _http.Dispose();
            Environment.SetEnvironmentVariable(_tokenVar, null);
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static string Wrap(string s) { return "t:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
        private static string Unwrap(string s) { return s != null && s.StartsWith("t:") ? Encoding.UTF8.GetString(Convert.FromBase64String(s.Substring(2))) : null; }

        private FairleadHost Start(FakeUpstream fake)
        {
            var host = new FairleadHost(new HostOptions
            {
                Settings = new FairleadSettings { RootDirectory = _dir, DataDirectory = _dir, LogDirectory = Path.Combine(_dir, "logs") },
                Config = RoutingTests.Config(),
                GatewayStore = new GatewayStore(_dir, Wrap, Unwrap),
                UpstreamHandler = fake,
                Monitor = false,
                PreferredPort = 0,
            });
            host.Start();
            _hosts.Add(host);
            return host;
        }

        private async Task<JObject> Admin(FairleadHost host, string path, JObject body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, host.BaseUrl + path) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Fairlead-Session", host.SessionToken);
            var r = await _http.SendAsync(req);
            var text = await r.Content.ReadAsStringAsync();
            Assert.True(r.IsSuccessStatusCode, text);
            return JObject.Parse(text);
        }

        private string Curl()
        {
            return "curl https://gateway.test/ai-gateway/mlflow/v1/responses -H \"Authorization: Bearer $" + _tokenVar + "\" -d '{\"model\": \"system.ai.gpt-5-4\", \"input\": [{\"role\": \"user\", \"content\": [{\"type\": \"input_text\", \"text\": \"Hello!\"}]}]}'";
        }

        [Fact]
        public async Task ACurlBecomesAGatewayAndTheTestSaysWhichPartFails()
        {
            var status = HttpStatusCode.Unauthorized;
            var fake = new FakeUpstream().On("gateway.test", (r, b) => status == HttpStatusCode.OK
                ? FakeUpstream.Json(HttpStatusCode.OK, GatewayFixture.ResponsesAnswer("ok"))
                : FakeUpstream.Json(status, new JObject { ["error"] = new JObject { ["message"] = "nope" } }));
            var host = Start(fake);

            var imported = await Admin(host, "/api/gateway/curl", new JObject { ["curl"] = Curl() });
            Assert.True((bool)imported["saved"]!, imported.ToString());
            Assert.Equal("curl_system_ai_gpt_5_4/system.ai.gpt-5-4", (string?)imported["target"]);
            var target = (string)imported["target"]!;

            // No key anywhere yet: nothing is sent.
            var noKey = await Admin(host, "/api/gateway/test", new JObject { ["target"] = target });
            Assert.Equal("no-key", (string?)noKey["verdict"]);
            Assert.Empty(fake.Seen);

            Environment.SetEnvironmentVariable(_tokenVar, "made-up-token-value");
            host.LoadGatewayModels();
            var rejected = await Admin(host, "/api/gateway/test", new JObject { ["target"] = target });
            Assert.Equal("key-rejected", (string?)rejected["verdict"]);
            Assert.DoesNotContain("made-up-token-value", rejected.ToString());

            status = HttpStatusCode.NotFound;
            host.Breakers.Find("curl_system_ai_gpt_5_4")?.Reset();
            var notFound = await Admin(host, "/api/gateway/test", new JObject { ["target"] = target });
            Assert.Equal("not-found", (string?)notFound["verdict"]);

            status = HttpStatusCode.OK;
            host.Breakers.Find("curl_system_ai_gpt_5_4")?.Reset();
            var ok = await Admin(host, "/api/gateway/test", new JObject { ["target"] = target });
            Assert.True((bool)ok["ok"]!, ok.ToString());
            Assert.Equal("ok", (string?)ok["text"]);
        }
    }
}
