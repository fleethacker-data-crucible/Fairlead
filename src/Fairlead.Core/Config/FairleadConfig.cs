using System;
using System.Collections.Generic;
using System.Linq;

namespace Fairlead.Core.Config
{
    /// <summary>The wire dialect a provider speaks. Every OpenAI-compatible server (Ollama, LM Studio, vLLM, Groq, Gemini...) is OpenAi.</summary>
    /// <summary>
    /// The wire format a provider speaks. Responses: the OpenAI Responses API (POST base URL + path) as served by an AI
    /// gateway; Fairlead calls it without streaming and translates to and from Chat Completions.
    /// </summary>
    public enum ProviderKind { OpenAi, Anthropic, Ollama, Responses }

    /// <summary>A model a provider serves. Unknown capabilities stay null and never exclude a model.</summary>
    public sealed class ModelInfo
    {
        public string Id { get; set; }
        public int? ContextWindow { get; set; }
        public bool? Tools { get; set; }
        public bool? Vision { get; set; }
        public bool Embedding { get; set; }
        /// <summary>USD per million input tokens. Empty until the operator enters it; Fairlead never invents prices.</summary>
        public decimal? InputPricePerMTok { get; set; }
        public decimal? OutputPricePerMTok { get; set; }
        /// <summary>True when found by discovery (Ollama tags, /models) rather than typed in.</summary>
        public bool Discovered { get; set; }

        public ModelInfo()
        {
            Id = string.Empty;
        }
    }

    public sealed class ProviderConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ProviderKind Kind { get; set; }
        /// <summary>Preset this provider was created from (openai, anthropic, gemini, groq, ollama, lmstudio...), for defaults only.</summary>
        public string Preset { get; set; }
        /// <summary>OpenAI-compatible: the /v1 base (https://api.openai.com/v1). Anthropic and Ollama: the server root.</summary>
        public string BaseUrl { get; set; }
        /// <summary>Runs on this machine or network: used in Kedge mode and for private requests.</summary>
        public bool Local { get; set; }
        public bool Enabled { get; set; }
        /// <summary>The API key, protected with Windows DPAPI for the current user ("dpapi:..."). Never sent to the browser.</summary>
        public string ApiKeyProtected { get; set; }
        /// <summary>Environment variable to read the key from when none is stored (ANTHROPIC_API_KEY...).</summary>
        public string ApiKeyEnv { get; set; }
        public int TimeoutSeconds { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public List<ModelInfo> Models { get; set; }
        /// <summary>Ask OpenAI-compatible servers for a final usage chunk when streaming (stream_options.include_usage).</summary>
        public bool StreamUsage { get; set; }
        /// <summary>Responses providers: the endpoint path appended to the base URL (for example /ai-gateway/openai/v1/responses).</summary>
        public string Path { get; set; }
        /// <summary>Responses providers: the optional request fields the endpoint accepts (max_output_tokens, temperature, top_p). Others are never sent.</summary>
        public List<string> RequestFields { get; set; }
        /// <summary>Extra tries on the same target for transient failures (5xx, 429, timeout, network), with exponential backoff and jitter. Never for 401/403/400/404. Null or 0: fail over at once.</summary>
        public int? Retries { get; set; }
        public int? RetryBaseMs { get; set; }
        public int? RetryMaxMs { get; set; }
        /// <summary>True for providers loaded from the AI gateway model file: rebuilt at every start, never saved to fairlead.json, not editable in the browser.</summary>
        public bool? Managed { get; set; }

        public ProviderConfig()
        {
            Id = string.Empty;
            Name = string.Empty;
            Kind = ProviderKind.OpenAi;
            BaseUrl = string.Empty;
            Enabled = true;
            TimeoutSeconds = 120;
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Models = new List<ModelInfo>();
            StreamUsage = true;
        }

        public ModelInfo FindModel(string id)
        {
            return Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public enum RouteStrategy
    {
        /// <summary>Targets in the order listed; the next one only when the previous fails.</summary>
        Failover,
        /// <summary>Lowest observed latency first (EWMA of time to first byte).</summary>
        Fastest,
        /// <summary>Spread load across targets in turn.</summary>
        RoundRobin,
        /// <summary>Pick another route per request with the routing rules (the "auto" route).</summary>
        Auto,
    }

    /// <summary>A virtual model tools ask for by name ("auto", "smart", "fast", "local").</summary>
    public sealed class RouteConfig
    {
        public string Id { get; set; }
        public string Description { get; set; }
        public RouteStrategy Strategy { get; set; }
        /// <summary>Ordered "providerId/modelId" references.</summary>
        public List<string> Targets { get; set; }
        /// <summary>When the first target has not answered (headers) after this many ms, race the next one. 0 = off.</summary>
        public int HedgeAfterMs { get; set; }
        /// <summary>When every target is down or filtered out, fall back to the local route.</summary>
        public bool LocalFallback { get; set; }

        public RouteConfig()
        {
            Id = string.Empty;
            Description = string.Empty;
            Strategy = RouteStrategy.Failover;
            Targets = new List<string>();
            LocalFallback = true;
        }
    }

    /// <summary>One condition set for the auto route. All set fields must match; the first matching rule wins.</summary>
    public sealed class RuleCondition
    {
        public bool? Sensitive { get; set; }
        public int? MinTokens { get; set; }
        public int? MaxTokens { get; set; }
        public bool? HasTools { get; set; }
        public bool? HasImages { get; set; }
        /// <summary>Any of these (case-insensitive) in the latest user message.</summary>
        public List<string> Keywords { get; set; }
        /// <summary>Client key name the rule applies to.</summary>
        public string Client { get; set; }
    }

    public sealed class RoutingRule
    {
        public string Name { get; set; }
        public string Route { get; set; }
        public bool Enabled { get; set; }
        public RuleCondition When { get; set; }

        public RoutingRule()
        {
            Name = string.Empty;
            Route = string.Empty;
            Enabled = true;
            When = new RuleCondition();
        }
    }

    /// <summary>A key a tool uses to call the gateway. Only the SHA-256 hash is kept; the key is shown once.</summary>
    public sealed class ClientKey
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>First characters of the key, to recognise it in a tool's settings.</summary>
        public string Prefix { get; set; }
        public string Hash { get; set; }
        public DateTimeOffset Created { get; set; }
        /// <summary>Routes this key may use; empty = all.</summary>
        public List<string> Routes { get; set; }
        /// <summary>Most requests this key may make in a minute (chat and embeddings); over it the answer is 429 with Retry-After. Null or 0 = no limit, the default.</summary>
        public int? RateLimitPerMinute { get; set; }

        public ClientKey()
        {
            Id = string.Empty;
            Name = string.Empty;
            Prefix = string.Empty;
            Hash = string.Empty;
            Created = DateTimeOffset.UtcNow;
            Routes = new List<string>();
        }
    }

    public sealed class NetworkConfig
    {
        /// <summary>host:port pairs probed with a plain TCP connect to decide whether the internet is reachable.</summary>
        public List<string> ProbeHosts { get; set; }
        public int ProbeIntervalSeconds { get; set; }
        public int ProbeTimeoutMs { get; set; }
        /// <summary>Consecutive failed probes before declaring the internet down (hysteresis against flapping).</summary>
        public int OfflineAfterFailures { get; set; }
        public int OnlineAfterSuccesses { get; set; }
        /// <summary>How often cloud keys are verified with a real API call (the cheap model list).</summary>
        public int DeepCheckMinutes { get; set; }

        public NetworkConfig()
        {
            ProbeHosts = new List<string> { "1.1.1.1:443", "8.8.8.8:443", "9.9.9.9:443" };
            ProbeIntervalSeconds = 15;
            ProbeTimeoutMs = 2500;
            OfflineAfterFailures = 2;
            OnlineAfterSuccesses = 2;
            DeepCheckMinutes = 10;
        }
    }

    public sealed class PrivacyConfig
    {
        /// <summary>Scan requests for secrets (keys, private keys, passwords) and mark them sensitive.</summary>
        public bool DetectSecrets { get; set; }
        /// <summary>Sensitive requests never leave the machine, whatever route was asked for.</summary>
        public bool ForceLocalWhenSensitive { get; set; }

        public PrivacyConfig()
        {
            DetectSecrets = true;
            ForceLocalWhenSensitive = true;
        }
    }

    public enum KedgeMode
    {
        /// <summary>Go local automatically when the internet is down.</summary>
        Auto,
        /// <summary>Local models only, whatever the network says.</summary>
        On,
        /// <summary>Never switch automatically; failed cloud calls still fall back per route.</summary>
        Off,
    }

    /// <summary>fairlead.json: everything the one-time browser setup writes.</summary>
    public sealed class FairleadConfig
    {
        public int Version { get; set; }
        public bool SetupComplete { get; set; }
        public List<ProviderConfig> Providers { get; set; }
        public List<RouteConfig> Routes { get; set; }
        public List<RoutingRule> Rules { get; set; }
        /// <summary>Glob (model name a tool sends) to route id, e.g. "claude-*haiku*" -> fast.</summary>
        public Dictionary<string, string> Aliases { get; set; }
        /// <summary>Route for model names nothing else matches.</summary>
        public string DefaultRoute { get; set; }
        /// <summary>Route the auto route uses when no rule matches.</summary>
        public string AutoFallbackRoute { get; set; }
        public KedgeMode Kedge { get; set; }
        public bool RequireClientKey { get; set; }
        public List<ClientKey> Clients { get; set; }
        public NetworkConfig Network { get; set; }
        public PrivacyConfig Privacy { get; set; }
        /// <summary>Browser origins allowed to call /v1 directly (web apps built at the hackathon). Empty = none.</summary>
        public List<string> CorsOrigins { get; set; }
        /// <summary>Keep the latest prompt and answer text in the in-memory activity log (never on disk). Off by default.</summary>
        public bool LogContent { get; set; }
        /// <summary>
        /// Hours Fairlead keeps keys from Lazarette in memory after Lazarette last confirmed them, so routing survives
        /// its tab closing. 0 forgets them as soon as Lazarette is locked or closed. Never written anywhere but memory.
        /// </summary>
        public double LazaretteHoldHours { get; set; }

        public FairleadConfig()
        {
            Version = 1;
            Providers = new List<ProviderConfig>();
            Routes = new List<RouteConfig>();
            Rules = new List<RoutingRule>();
            Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DefaultRoute = "auto";
            AutoFallbackRoute = "smart";
            Kedge = KedgeMode.Auto;
            RequireClientKey = true;
            Clients = new List<ClientKey>();
            Network = new NetworkConfig();
            Privacy = new PrivacyConfig();
            CorsOrigins = new List<string>();
            LazaretteHoldHours = 8;
        }

        public ProviderConfig FindProvider(string id)
        {
            return Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public RouteConfig FindRoute(string id)
        {
            return Routes.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A fresh config: local Ollama, the four standard routes, the default rules and aliases.</summary>
        public static FairleadConfig CreateDefault()
        {
            var c = new FairleadConfig();
            c.Providers.Add(ProviderPresets.Create("ollama"));
            c.Routes.Add(new RouteConfig { Id = "auto", Description = "Chooses smart, fast or local per request with the routing rules.", Strategy = RouteStrategy.Auto });
            c.Routes.Add(new RouteConfig { Id = "smart", Description = "Strongest available model: long context, tools, reasoning.", Strategy = RouteStrategy.Failover });
            c.Routes.Add(new RouteConfig { Id = "fast", Description = "Quick, cheap answers for short prompts.", Strategy = RouteStrategy.Fastest });
            c.Routes.Add(new RouteConfig { Id = "local", Description = "Models on this machine only. Kedge mode and private requests land here.", Strategy = RouteStrategy.Failover, LocalFallback = false });
            c.Rules.AddRange(DefaultRules());
            foreach (var kv in DefaultAliases()) c.Aliases[kv.Key] = kv.Value;
            return c;
        }

        public static List<RoutingRule> DefaultRules()
        {
            return new List<RoutingRule>
            {
                new RoutingRule { Name = "private-stays-local", Route = "local", When = new RuleCondition { Sensitive = true } },
                new RoutingRule { Name = "long-context", Route = "smart", When = new RuleCondition { MinTokens = 24000 } },
                new RoutingRule { Name = "agentic-tools", Route = "smart", When = new RuleCondition { HasTools = true } },
                new RoutingRule { Name = "images", Route = "smart", When = new RuleCondition { HasImages = true } },
                new RoutingRule { Name = "deep-reasoning", Route = "smart", When = new RuleCondition { Keywords = new List<string> { "step by step", "prove", "architect", "design a", "refactor", "debug", "root cause", "analyze", "analyse", "trade-off", "tradeoff" } } },
                new RoutingRule { Name = "quick-chat", Route = "fast", When = new RuleCondition { MaxTokens = 1500 } },
            };
        }

        public static Dictionary<string, string> DefaultAliases()
        {
            // Tools that hard-code model names (Claude Code, Cursor-style clients, SDK samples) still land on a route.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "*haiku*", "fast" },
                { "*-mini*", "fast" },
                { "*flash*", "fast" },
                { "claude-*", "smart" },
                { "gpt-*", "smart" },
                { "o[0-9]*", "smart" },
                { "gemini-*", "smart" },
            };
        }
    }
}
