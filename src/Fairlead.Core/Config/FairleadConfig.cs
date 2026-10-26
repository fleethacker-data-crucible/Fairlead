using System;
using System.Collections.Generic;
using System.Linq;

namespace Fairlead.Core.Config
{
    /// <summary>The wire dialect a provider speaks. Every OpenAI-compatible server (Ollama, LM Studio, vLLM, Groq, Gemini...) is OpenAi.</summary>
    public enum ProviderKind { OpenAi, Anthropic, Ollama }

    /// <summary>A model a provider serves. Unknown capabilities stay null and never exclude a model.</summary>
    public sealed class ModelInfo
    {
        private string _id = string.Empty;
        public string Id { get { return _id; } set { _id = value; } }
        public int? ContextWindow { get; set; }
        public bool? Tools { get; set; }
        public bool? Vision { get; set; }
        public bool Embedding { get; set; }
        /// <summary>USD per million input tokens. Empty until the operator enters it; Fairlead never invents prices.</summary>
        public decimal? InputPricePerMTok { get; set; }
        public decimal? OutputPricePerMTok { get; set; }
        /// <summary>True when found by discovery (Ollama tags, /models) rather than typed in.</summary>
        public bool Discovered { get; set; }
    }

    public sealed class ProviderConfig
    {
        private string _id = string.Empty;
        public string Id { get { return _id; } set { _id = value; } }
        private string _name = string.Empty;
        public string Name { get { return _name; } set { _name = value; } }
        private ProviderKind _kind = ProviderKind.OpenAi;
        public ProviderKind Kind { get { return _kind; } set { _kind = value; } }
        /// <summary>Preset this provider was created from (openai, anthropic, gemini, groq, ollama, lmstudio...), for defaults only.</summary>
        public string Preset { get; set; }
        /// <summary>OpenAI-compatible: the /v1 base (https://api.openai.com/v1). Anthropic and Ollama: the server root.</summary>
        private string _baseUrl = string.Empty;
        public string BaseUrl { get { return _baseUrl; } set { _baseUrl = value; } }
        /// <summary>Runs on this machine or network: used in Kedge mode and for private requests.</summary>
        public bool Local { get; set; }
        private bool _enabled = true;
        public bool Enabled { get { return _enabled; } set { _enabled = value; } }
        /// <summary>The API key, protected with Windows DPAPI for the current user ("dpapi:..."). Never sent to the browser.</summary>
        public string ApiKeyProtected { get; set; }
        /// <summary>Environment variable to read the key from when none is stored (ANTHROPIC_API_KEY...).</summary>
        public string ApiKeyEnv { get; set; }
        private int _timeoutSeconds = 120;
        public int TimeoutSeconds { get { return _timeoutSeconds; } set { _timeoutSeconds = value; } }
        private Dictionary<string, string> _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get { return _headers; } set { _headers = value; } }
        private List<ModelInfo> _models = new List<ModelInfo>();
        public List<ModelInfo> Models { get { return _models; } set { _models = value; } }
        /// <summary>Ask OpenAI-compatible servers for a final usage chunk when streaming (stream_options.include_usage).</summary>
        private bool _streamUsage = true;
        public bool StreamUsage { get { return _streamUsage; } set { _streamUsage = value; } }

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
        private string _id = string.Empty;
        public string Id { get { return _id; } set { _id = value; } }
        private string _description = string.Empty;
        public string Description { get { return _description; } set { _description = value; } }
        private RouteStrategy _strategy = RouteStrategy.Failover;
        public RouteStrategy Strategy { get { return _strategy; } set { _strategy = value; } }
        /// <summary>Ordered "providerId/modelId" references.</summary>
        private List<string> _targets = new List<string>();
        public List<string> Targets { get { return _targets; } set { _targets = value; } }
        /// <summary>When the first target has not answered (headers) after this many ms, race the next one. 0 = off.</summary>
        public int HedgeAfterMs { get; set; }
        /// <summary>When every target is down or filtered out, fall back to the local route.</summary>
        private bool _localFallback = true;
        public bool LocalFallback { get { return _localFallback; } set { _localFallback = value; } }
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
        private string _name = string.Empty;
        public string Name { get { return _name; } set { _name = value; } }
        private string _route = string.Empty;
        public string Route { get { return _route; } set { _route = value; } }
        private bool _enabled = true;
        public bool Enabled { get { return _enabled; } set { _enabled = value; } }
        private RuleCondition _when = new RuleCondition();
        public RuleCondition When { get { return _when; } set { _when = value; } }
    }

    /// <summary>A key a tool uses to call the gateway. Only the SHA-256 hash is kept; the key is shown once.</summary>
    public sealed class ClientKey
    {
        private string _id = string.Empty;
        public string Id { get { return _id; } set { _id = value; } }
        private string _name = string.Empty;
        public string Name { get { return _name; } set { _name = value; } }
        /// <summary>First characters of the key, to recognise it in a tool's settings.</summary>
        private string _prefix = string.Empty;
        public string Prefix { get { return _prefix; } set { _prefix = value; } }
        private string _hash = string.Empty;
        public string Hash { get { return _hash; } set { _hash = value; } }
        private DateTimeOffset _created = DateTimeOffset.UtcNow;
        public DateTimeOffset Created { get { return _created; } set { _created = value; } }
        /// <summary>Routes this key may use; empty = all.</summary>
        private List<string> _routes = new List<string>();
        public List<string> Routes { get { return _routes; } set { _routes = value; } }
    }

    public sealed class NetworkConfig
    {
        /// <summary>host:port pairs probed with a plain TCP connect to decide whether the internet is reachable.</summary>
        private List<string> _probeHosts = new List<string> { "1.1.1.1:443", "8.8.8.8:443", "9.9.9.9:443" };
        public List<string> ProbeHosts { get { return _probeHosts; } set { _probeHosts = value; } }
        private int _probeIntervalSeconds = 15;
        public int ProbeIntervalSeconds { get { return _probeIntervalSeconds; } set { _probeIntervalSeconds = value; } }
        private int _probeTimeoutMs = 2500;
        public int ProbeTimeoutMs { get { return _probeTimeoutMs; } set { _probeTimeoutMs = value; } }
        /// <summary>Consecutive failed probes before declaring the internet down (hysteresis against flapping).</summary>
        private int _offlineAfterFailures = 2;
        public int OfflineAfterFailures { get { return _offlineAfterFailures; } set { _offlineAfterFailures = value; } }
        private int _onlineAfterSuccesses = 2;
        public int OnlineAfterSuccesses { get { return _onlineAfterSuccesses; } set { _onlineAfterSuccesses = value; } }
        /// <summary>How often cloud keys are verified with a real API call (the cheap model list).</summary>
        private int _deepCheckMinutes = 10;
        public int DeepCheckMinutes { get { return _deepCheckMinutes; } set { _deepCheckMinutes = value; } }
    }

    public sealed class PrivacyConfig
    {
        /// <summary>Scan requests for secrets (keys, private keys, passwords) and mark them sensitive.</summary>
        private bool _detectSecrets = true;
        public bool DetectSecrets { get { return _detectSecrets; } set { _detectSecrets = value; } }
        /// <summary>Sensitive requests never leave the machine, whatever route was asked for.</summary>
        private bool _forceLocalWhenSensitive = true;
        public bool ForceLocalWhenSensitive { get { return _forceLocalWhenSensitive; } set { _forceLocalWhenSensitive = value; } }
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
        private int _version = 1;
        public int Version { get { return _version; } set { _version = value; } }
        public bool SetupComplete { get; set; }
        private List<ProviderConfig> _providers = new List<ProviderConfig>();
        public List<ProviderConfig> Providers { get { return _providers; } set { _providers = value; } }
        private List<RouteConfig> _routes = new List<RouteConfig>();
        public List<RouteConfig> Routes { get { return _routes; } set { _routes = value; } }
        private List<RoutingRule> _rules = new List<RoutingRule>();
        public List<RoutingRule> Rules { get { return _rules; } set { _rules = value; } }
        /// <summary>Glob (model name a tool sends) to route id, e.g. "claude-*haiku*" -> fast.</summary>
        private Dictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Aliases { get { return _aliases; } set { _aliases = value; } }
        /// <summary>Route for model names nothing else matches.</summary>
        private string _defaultRoute = "auto";
        public string DefaultRoute { get { return _defaultRoute; } set { _defaultRoute = value; } }
        /// <summary>Route the auto route uses when no rule matches.</summary>
        private string _autoFallbackRoute = "smart";
        public string AutoFallbackRoute { get { return _autoFallbackRoute; } set { _autoFallbackRoute = value; } }
        private KedgeMode _kedge = KedgeMode.Auto;
        public KedgeMode Kedge { get { return _kedge; } set { _kedge = value; } }
        private bool _requireClientKey = true;
        public bool RequireClientKey { get { return _requireClientKey; } set { _requireClientKey = value; } }
        private List<ClientKey> _clients = new List<ClientKey>();
        public List<ClientKey> Clients { get { return _clients; } set { _clients = value; } }
        private NetworkConfig _network = new NetworkConfig();
        public NetworkConfig Network { get { return _network; } set { _network = value; } }
        private PrivacyConfig _privacy = new PrivacyConfig();
        public PrivacyConfig Privacy { get { return _privacy; } set { _privacy = value; } }
        /// <summary>Browser origins allowed to call /v1 directly (web apps built at the hackathon). Empty = none.</summary>
        private List<string> _corsOrigins = new List<string>();
        public List<string> CorsOrigins { get { return _corsOrigins; } set { _corsOrigins = value; } }
        /// <summary>Keep the latest prompt and answer text in the in-memory activity log (never on disk). Off by default.</summary>
        public bool LogContent { get; set; }

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
