using System;
using System.Collections.Generic;
using System.Linq;
using Fairlead.Core.Config;
using Fairlead.Core.Protocol;
using Fairlead.Core.Providers;
using Fairlead.Core.Resilience;
using Fairlead.Core.Routing;
using Fairlead.Core.JsonLite;
using Xunit;

namespace Fairlead.Core.Tests
{
    public class RoutingTests
    {
        internal static FairleadConfig Config()
        {
            var c = FairleadConfig.CreateDefault();
            c.Providers[0].Models.Add(new ModelInfo { Id = "qwen", ContextWindow = 32768, Tools = true });
            c.Providers[0].Models.Add(new ModelInfo { Id = "tiny", ContextWindow = 2048, Tools = false });
            var cloud = ProviderPresets.Create("anthropic");
            cloud.Models.Add(new ModelInfo { Id = "claude-big", ContextWindow = 200000, Tools = true, Vision = true, InputPricePerMTok = 3m, OutputPricePerMTok = 15m });
            c.Providers.Add(cloud);
            var fast = ProviderPresets.Create("groq");
            fast.Models.Add(new ModelInfo { Id = "llama-fast" });
            c.Providers.Add(fast);
            c.FindRoute("smart")!.Targets = new List<string> { "anthropic/claude-big", "ollama/qwen" };
            c.FindRoute("fast")!.Targets = new List<string> { "groq/llama-fast", "ollama/tiny" };
            c.FindRoute("local")!.Targets = new List<string> { "ollama/qwen" };
            return c;
        }

        private static RequestProfile Profile(string model, string prompt, bool tools = false)
        {
            var body = new JObject { ["model"] = model, ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } } };
            if (tools) body["tools"] = new JArray { new JObject { ["type"] = "function", ["function"] = new JObject { ["name"] = "f" } } };
            return RequestProfile.From(body, Dialect.OpenAi, true);
        }

        private static RoutePlan Plan(FairleadConfig c, RequestProfile p, HealthMonitor? h = null, BreakerBoard? b = null)
        {
            return new RoutePlanner().Plan(c, p, h, b ?? new BreakerBoard(), null, null);
        }

        [Fact]
        public void AutoRouteSendsShortChatToFastAndToolsToSmart()
        {
            var c = Config();
            var quick = Plan(c, Profile("auto", "hi"));
            Assert.Equal("fast", quick.Route);
            Assert.Equal("groq/llama-fast", quick.Candidates[0].Ref);
            var agent = Plan(c, Profile("auto", "hi", tools: true));
            Assert.Equal("smart", agent.Route);
            Assert.Equal("anthropic/claude-big", agent.Candidates[0].Ref);
            Assert.Contains(agent.Decision, d => d.Contains("agentic-tools"));
        }

        [Fact]
        public void HardCodedModelNamesLandOnRoutesThroughAliases()
        {
            var c = Config();
            Assert.Equal("fast", Plan(c, Profile("claude-haiku-5-5", "hello")).Route);
            Assert.Equal("smart", Plan(c, Profile("claude-opus-5-5", "hello")).Route);
            Assert.Equal("smart", Plan(c, Profile("gpt-6", "hello")).Route);
            // Unknown names use the default route (auto), which then applies the rules.
            Assert.Equal("fast", Plan(c, Profile("mystery-model", "hello")).Route);
        }

        [Fact]
        public void ProviderSlashModelAndBareServedModelGoDirect()
        {
            var c = Config();
            var direct = Plan(c, Profile("groq/llama-fast", "x"));
            Assert.Equal("groq/llama-fast", direct.Candidates[0].Ref);
            var bare = Plan(c, Profile("qwen", "x"));
            Assert.Equal("ollama/qwen", bare.Candidates[0].Ref);
        }

        [Fact]
        public void SecretsInThePromptKeepTheRequestLocal()
        {
            var c = Config();
            var plan = Plan(c, Profile("smart", "deploy with AKIAABCDEFGHIJKLMNOP please"));
            Assert.True(plan.LocalOnly);
            Assert.All(plan.Candidates, x => Assert.True(x.Local));
            Assert.Contains(plan.Decision, d => d.Contains("aws-key"));
        }

        [Fact]
        public void KedgeModeDropsCloudTargetsAndFallsBackToLocal()
        {
            var c = Config();
            c.Kedge = KedgeMode.On;
            using (var health = new HealthMonitor(new ConfigStore(c), new UpstreamClient(new FakeUpstream()), new BreakerBoard(), new Host.Diagnostics(null)))
            {
                var plan = Plan(c, Profile("fast", "hello"), health);
                Assert.True(plan.Kedge);
                Assert.Equal("ollama/tiny", plan.Candidates[0].Ref);
                var smart = Plan(c, Profile("anthropic/claude-big", "hello"), health);
                Assert.Equal("ollama/qwen", smart.Candidates.Single().Ref);
            }
        }

        [Fact]
        public void CapabilitiesAndContextWindowFilterTargets()
        {
            var c = Config();
            var withTools = Plan(c, Profile("fast", "hi", tools: true));
            Assert.Equal("smart", Plan(c, Profile("auto", "hi", tools: true)).Route);
            Assert.DoesNotContain(withTools.Candidates, x => x.Ref == "ollama/tiny");
            var longPrompt = Plan(c, Profile("fast", new string('x', 20000)));
            Assert.DoesNotContain(longPrompt.Candidates, x => x.Ref == "ollama/tiny");
            Assert.Contains(longPrompt.Decision, d => d.Contains("window is 2048"));
        }

        [Fact]
        public void OpenBreakerParksATargetBehindHealthyOnes()
        {
            var c = Config();
            var breakers = new BreakerBoard();
            breakers.For("anthropic").Failure(FailureKind.Network, "offline");
            var plan = Plan(c, Profile("smart", "hello"), null, breakers);
            Assert.Equal("ollama/qwen", plan.Candidates[0].Ref);
            Assert.True(plan.Candidates.Last().Parked);
        }

        [Fact]
        public void EmptyRouteFallsBackToLocal()
        {
            var c = Config();
            c.FindRoute("smart")!.Targets.Clear();
            var plan = Plan(c, Profile("smart", "hello"));
            Assert.Equal("ollama/qwen", plan.Candidates.Single().Ref);
            Assert.Contains(plan.Decision, d => d.Contains("falling back to 'local'"));
        }

        [Fact]
        public void RouteHeaderOverridesTheModelName()
        {
            var c = Config();
            var p = Profile("claude-opus-5-5", "hello");
            p.RouteOverride = "local";
            Assert.Equal("local", Plan(c, p).Route);
        }

        [Fact]
        public void ClientKeysCanBeLimitedToRoutes()
        {
            var c = Config();
            var key = new ClientKey { Name = "bot", Routes = new List<string> { "local" } };
            var denied = new RoutePlanner().Plan(c, Profile("smart", "x"), null, new BreakerBoard(), null, key);
            Assert.Equal(403, denied.ErrorStatus);
            Assert.NotNull(denied.Error);
            Assert.Null(new RoutePlanner().Plan(c, Profile("local", "x"), null, new BreakerBoard(), null, key).Error);
        }

        [Fact]
        public void GlobsMatchInFileOrderAndClasses()
        {
            var a = FairleadConfig.DefaultAliases();
            Assert.Equal("fast", Glob.Resolve(a, "claude-3-5-haiku-latest"));
            Assert.Equal("smart", Glob.Resolve(a, "o3"));
            Assert.Null(Glob.Resolve(a, "llama3"));
        }

        [Fact]
        public void SecretScannerIsSpecific()
        {
            Assert.Empty(SecretScanner.Scan("my password is in the vault, timestamp 1700000000000123"));
            Assert.Contains("private-key", SecretScanner.Scan("-----BEGIN OPENSSH PRIVATE KEY-----"));
            Assert.Contains("card-number", SecretScanner.Scan("card 4111 1111 1111 1111 exp"));
            Assert.Contains("password-assignment", SecretScanner.Scan("db_password = \"hunter22!\""));
        }

        [Fact]
        public void ConfigValidationRejectsDanglingReferences()
        {
            var c = Config();
            c.Rules.Add(new RoutingRule { Name = "x", Route = "nope" });
            Assert.Throws<ArgumentException>(() => ConfigStore.Validate(c));
            var d = Config();
            d.Routes[1].Targets.Add("no-slash");
            Assert.Throws<ArgumentException>(() => ConfigStore.Validate(d));
        }
    }

    public class BreakerTests
    {
        [Fact]
        public void OpensAfterThresholdThenHalfOpensForOneTrial()
        {
            var b = new CircuitBreaker("p");
            var now = DateTimeOffset.UtcNow;
            b.Failure(FailureKind.Server, "500");
            b.Failure(FailureKind.Server, "500");
            Assert.Equal(BreakerState.Closed, b.State(now));
            b.Failure(FailureKind.Server, "500");
            Assert.Equal(BreakerState.Open, b.State(DateTimeOffset.UtcNow));
            var later = DateTimeOffset.UtcNow.AddSeconds(11);
            Assert.Equal(BreakerState.HalfOpen, b.State(later));
            Assert.True(b.TryAcquire(later));
            Assert.False(b.TryAcquire(later));
            b.Success();
            Assert.Equal(BreakerState.Closed, b.State(later));
        }

        [Fact]
        public void NetworkFailureOpensAtOnceAndBadRequestNever()
        {
            var b = new CircuitBreaker("p");
            b.Failure(FailureKind.BadRequest, "400");
            Assert.Equal(BreakerState.Closed, b.State(DateTimeOffset.UtcNow));
            b.Failure(FailureKind.Network, "refused");
            Assert.Equal(BreakerState.Open, b.State(DateTimeOffset.UtcNow));
        }

        [Fact]
        public void RateLimitHonoursRetryAfter()
        {
            var b = new CircuitBreaker("p");
            b.Failure(FailureKind.RateLimited, "429", TimeSpan.FromSeconds(42));
            Assert.InRange((b.OpenUntil!.Value - DateTimeOffset.UtcNow).TotalSeconds, 40, 43);
        }
    }
}
