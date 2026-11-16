using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Fairlead.Core.Config;
using Fairlead.Core.Resilience;
using Fairlead.Core.Telemetry;

namespace Fairlead.Core.Routing
{
    public sealed class Candidate
    {
        public ProviderConfig Provider { get; set; }
        public string Model { get; set; }
        public ModelInfo Info { get; set; }
        public string Ref { get { return Provider.Id + "/" + Model; } }
        public bool Local { get { return Provider.Local; } }
        /// <summary>Breaker open or probe failed: tried only after every healthy candidate.</summary>
        public bool Parked { get; set; }
        public string ParkedBecause { get; set; }
        /// <summary>Which lead it came from: the planned route's id, "local" after a local fallback, or "discovered".</summary>
        public string Via { get; set; }

        public Candidate()
        {
            Provider = new ProviderConfig();
            Model = string.Empty;
        }
    }

    public sealed class RoutePlan
    {
        public string Route { get; set; }
        /// <summary>The route the request named or defaulted to, before 'auto' rules chose <see cref="Route"/>.</summary>
        public string RequestedRoute { get; set; }
        public List<string> Decision { get; set; }
        public List<Candidate> Candidates { get; set; }
        public int HedgeAfterMs { get; set; }
        public bool LocalOnly { get; set; }
        public bool Kedge { get; set; }
        /// <summary>Set when nothing can serve the request; the gateway answers 503 with it.</summary>
        public string Error { get; set; }
        public int ErrorStatus { get; set; }
        /// <summary>Targets left out only because the prompt does not fit their context window.</summary>
        public int ContextRejected { get; set; }
        public int LargestWindow { get; set; }

        public RoutePlan()
        {
            Route = string.Empty;
            Decision = new List<string>();
            Candidates = new List<Candidate>();
            ErrorStatus = 503;
        }
    }

    /// <summary>
    /// Turns a request into an ordered list of targets, with a reason for every step. Pure apart from the
    /// round-robin counters: config, health, breakers and stats are read, never changed.
    /// </summary>
    public sealed class RoutePlanner
    {
        private readonly ConcurrentDictionary<string, int> _turns = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public RoutePlan Plan(FairleadConfig cfg, RequestProfile profile, HealthMonitor health, BreakerBoard breakers, Metrics metrics, ClientKey client, string operation = "chat")
        {
            var plan = new RoutePlan();
            var now = DateTimeOffset.UtcNow;
            var route = ResolveRoute(cfg, profile, plan, health);
            if (route == null) return plan;
            var askedFor = route.Id;

            if (route.Strategy == RouteStrategy.Auto)
            {
                var chosen = ApplyRules(cfg, profile, plan);
                route = cfg.FindRoute(chosen);
                if (route == null) { plan.Error = "The auto route chose '" + chosen + "', which does not exist."; return plan; }
            }
            plan.Route = route.Id;
            plan.RequestedRoute = askedFor;
            plan.HedgeAfterMs = route.HedgeAfterMs;

            if (client != null && client.Routes.Count > 0 && !client.Routes.Contains(route.Id, StringComparer.OrdinalIgnoreCase) && !client.Routes.Contains(askedFor, StringComparer.OrdinalIgnoreCase))
            {
                plan.Error = "Client key '" + client.Name + "' may not use route '" + route.Id + "'.";
                plan.ErrorStatus = 403;
                return plan;
            }

            if (profile.Sensitive && cfg.Privacy.ForceLocalWhenSensitive)
            {
                plan.LocalOnly = true;
                plan.Decision.Add("Private request (" + string.Join(", ", profile.SensitiveReasons) + "): local models only.");
            }
            if (health != null && health.KedgeActive)
            {
                plan.LocalOnly = true;
                plan.Kedge = true;
                var reason = health.Connectivity.KedgeReason == "forced" ? "forced on" : "internet unreachable";
                plan.Decision.Add("Kedge mode (" + reason + "): local models only.");
            }

            var candidates = Expand(cfg, route, profile, health, breakers, plan, operation, now);
            Order(route, candidates, metrics);
            foreach (var c in candidates) c.Via = route.Id;
            var healthy = candidates.Where(c => !c.Parked).ToList();

            if (healthy.Count == 0 && route.LocalFallback && !string.Equals(route.Id, "local", StringComparison.OrdinalIgnoreCase))
            {
                var local = cfg.FindRoute("local");
                if (local != null)
                {
                    plan.Decision.Add(candidates.Count == 0 ? "Route '" + route.Id + "' has no usable target; falling back to 'local'." : "Every target of '" + route.Id + "' is down; falling back to 'local'.");
                    var extra = Expand(cfg, local, profile, health, breakers, plan, operation, now);
                    Order(local, extra, metrics);
                    foreach (var c in extra) c.Via = local.Id;
                    candidates.AddRange(extra);
                    healthy = candidates.Where(c => !c.Parked).ToList();
                }
            }
            if (healthy.Count == 0 && (route.LocalFallback || string.Equals(route.Id, "local", StringComparison.OrdinalIgnoreCase)))
            {
                var auto = AutoLocal(cfg, profile, health, breakers, operation, now);
                if (auto != null)
                {
                    plan.Decision.Add("Using " + auto.Ref + ", a model found on a local server (no local route target is configured or reachable).");
                    auto.Via = "discovered";
                    candidates.Add(auto);
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = candidates.Where(c => !c.Parked).Concat(candidates.Where(c => c.Parked)).Where(c => seen.Add(c.Ref)).ToList();
            plan.Candidates = ordered;
            if (ordered.Count == 0)
            {
                plan.Error = plan.LocalOnly
                    ? "No local model can take this request" + (plan.Kedge ? " and the cloud is unreachable (Kedge mode)" : string.Empty) + ". Start Ollama and install a small model (for example: ollama pull llama3.2:1b), or add a target to the 'local' route."
                    : "Route '" + route.Id + "' has no target that can take this request. Add one in Fairlead's Routes page.";
                // A prompt longer than every window is the caller's to fix (400), not an outage (503).
                if (plan.ContextRejected > 0)
                {
                    plan.ErrorStatus = 400;
                    plan.Error = "The prompt (about " + (profile.EstimatedTokens + (profile.MaxOutputTokens ?? 0)) + " tokens with the reply) is longer than the context window of every model this route can use (largest: " + plan.LargestWindow + " tokens). Shorten the prompt or ask for a route with a larger window.";
                }
            }
            else
            {
                var first = ordered[0];
                plan.Decision.Add("First choice " + first.Ref + (first.Parked ? " (last resort: " + first.ParkedBecause + ")" : string.Empty) + (ordered.Count > 1 ? ", then " + string.Join(", ", ordered.Skip(1).Take(3).Select(c => c.Ref)) + (ordered.Count > 4 ? "\u2026" : string.Empty) : string.Empty) + ".");
            }
            return plan;
        }

        private static RouteConfig ResolveRoute(FairleadConfig cfg, RequestProfile profile, RoutePlan plan, HealthMonitor health)
        {
            if (!string.IsNullOrEmpty(profile.RouteOverride))
            {
                var o = cfg.FindRoute(profile.RouteOverride);
                if (o != null) { plan.Decision.Add("X-Fairlead-Route header asks for '" + o.Id + "'."); return o; }
                plan.Decision.Add("X-Fairlead-Route '" + profile.RouteOverride + "' is not a route; ignored.");
            }
            var model = profile.RequestedModel.Trim();
            if (model.StartsWith("fairlead/", StringComparison.OrdinalIgnoreCase)) model = model.Substring(9);
            if (model.Length == 0)
            {
                var d = cfg.FindRoute(cfg.DefaultRoute);
                plan.Decision.Add("No model named; default route '" + cfg.DefaultRoute + "'.");
                return d;
            }
            var direct = cfg.FindRoute(model);
            if (direct != null) { plan.Decision.Add("Model '" + model + "' is a Fairlead route."); return direct; }
            var slash = model.IndexOf('/');
            if (slash > 0)
            {
                var provider = cfg.FindProvider(model.Substring(0, slash));
                if (provider != null)
                {
                    plan.Decision.Add("Model '" + model + "' names a provider target directly.");
                    return new RouteConfig { Id = model, Strategy = RouteStrategy.Failover, Targets = new List<string> { model }, LocalFallback = true };
                }
            }
            var alias = Glob.Resolve(cfg.Aliases, model);
            if (alias != null)
            {
                var r = cfg.FindRoute(alias);
                if (r != null) { plan.Decision.Add("Model '" + model + "' matches an alias for route '" + r.Id + "'."); return r; }
            }
            // A bare model id that one provider serves ("phi4", "gpt-4.1") goes straight to it.
            foreach (var p in cfg.Providers.Where(x => x.Enabled))
            {
                if (p.FindModel(model) != null || HealthModels(health, p.Id).Any(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase)))
                {
                    plan.Decision.Add("Model '" + model + "' is served by " + p.Id + ".");
                    return new RouteConfig { Id = p.Id + "/" + model, Strategy = RouteStrategy.Failover, Targets = new List<string> { p.Id + "/" + model }, LocalFallback = true };
                }
            }
            var def = cfg.FindRoute(cfg.DefaultRoute);
            plan.Decision.Add("Model '" + model + "' is not known; default route '" + cfg.DefaultRoute + "'.");
            if (def == null) plan.Error = "The default route '" + cfg.DefaultRoute + "' does not exist.";
            return def;
        }

        public static string ApplyRules(FairleadConfig cfg, RequestProfile profile, RoutePlan plan)
        {
            foreach (var rule in cfg.Rules)
            {
                if (!rule.Enabled) continue;
                if (Matches(rule.When, profile))
                {
                    if (plan != null) plan.Decision.Add("Auto: rule '" + rule.Name + "' matched (" + Describe(rule.When, profile) + ") -> '" + rule.Route + "'.");
                    return rule.Route;
                }
            }
            if (plan != null) plan.Decision.Add("Auto: no rule matched (~" + profile.EstimatedTokens + " tokens) -> '" + cfg.AutoFallbackRoute + "'.");
            return cfg.AutoFallbackRoute;
        }

        public static bool Matches(RuleCondition w, RequestProfile p)
        {
            if (w.Sensitive.HasValue && w.Sensitive.Value != p.Sensitive) return false;
            if (w.MinTokens.HasValue && p.EstimatedTokens < w.MinTokens.Value) return false;
            if (w.MaxTokens.HasValue && p.EstimatedTokens > w.MaxTokens.Value) return false;
            if (w.HasTools.HasValue && w.HasTools.Value != p.HasTools) return false;
            if (w.HasImages.HasValue && w.HasImages.Value != p.HasImages) return false;
            if (w.Client != null && !string.Equals(w.Client, p.ClientName, StringComparison.OrdinalIgnoreCase)) return false;
            if (w.Keywords != null && w.Keywords.Count > 0)
            {
                var text = p.LastUserText;
                if (!w.Keywords.Any(k => !string.IsNullOrEmpty(k) && text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            }
            // A rule with no conditions at all matches everything.
            return true;
        }

        private static string Describe(RuleCondition w, RequestProfile p)
        {
            var parts = new List<string>();
            if (w.Sensitive.HasValue) parts.Add(w.Sensitive.Value ? "sensitive" : "not sensitive");
            if (w.MinTokens.HasValue) parts.Add("~" + p.EstimatedTokens + " >= " + w.MinTokens + " tokens");
            if (w.MaxTokens.HasValue) parts.Add("~" + p.EstimatedTokens + " <= " + w.MaxTokens + " tokens");
            if (w.HasTools.HasValue) parts.Add(w.HasTools.Value ? "uses tools" : "no tools");
            if (w.HasImages.HasValue) parts.Add(w.HasImages.Value ? "has images" : "no images");
            if (w.Keywords != null && w.Keywords.Count > 0)
            {
                var hit = w.Keywords.FirstOrDefault(k => !string.IsNullOrEmpty(k) && p.LastUserText.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                parts.Add("keyword '" + hit + "'");
            }
            if (w.Client != null) parts.Add("client " + w.Client);
            return parts.Count == 0 ? "always" : string.Join(", ", parts);
        }

        private static List<Candidate> Expand(FairleadConfig cfg, RouteConfig route, RequestProfile profile, HealthMonitor health, BreakerBoard breakers, RoutePlan plan, string operation, DateTimeOffset now)
        {
            var list = new List<Candidate>();
            foreach (var target in route.Targets)
            {
                var slash = target.IndexOf('/');
                var provider = cfg.FindProvider(target.Substring(0, slash));
                var model = target.Substring(slash + 1);
                if (provider == null) { plan.Decision.Add("Skip " + target + ": no such provider."); continue; }
                if (!provider.Enabled) { plan.Decision.Add("Skip " + target + ": provider disabled."); continue; }
                if (plan.LocalOnly && !provider.Local) continue;
                if (operation == "embeddings" && provider.Kind == ProviderKind.Anthropic) { plan.Decision.Add("Skip " + target + ": Anthropic has no embeddings endpoint."); continue; }
                if (operation == "embeddings" && provider.Kind == ProviderKind.Responses) { plan.Decision.Add("Skip " + target + ": a Responses gateway serves chat only."); continue; }
                if (IsAudio(operation) && provider.Kind != ProviderKind.OpenAi) { plan.Decision.Add("Skip " + target + ": only OpenAI-compatible providers serve audio."); continue; }
                var info = provider.FindModel(model) ?? HealthModels(health, provider.Id).FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
                var why = Unfit(info, profile, operation);
                if (why != null)
                {
                    plan.Decision.Add("Skip " + target + ": " + why + ".");
                    if (why.StartsWith("needs ~", StringComparison.Ordinal)) { plan.ContextRejected++; plan.LargestWindow = Math.Max(plan.LargestWindow, info.ContextWindow ?? 0); }
                    continue;
                }
                list.Add(Park(new Candidate { Provider = provider, Model = model, Info = info }, health, breakers, now));
            }
            return list;
        }

        /// <summary>Why a model cannot take the request, from what is known about it; unknown capabilities never exclude.</summary>
        /// <summary>Speech out and speech in: operations only OpenAI-compatible providers serve.</summary>
        public static bool IsAudio(string operation)
        {
            return operation == "speech" || operation == "transcriptions";
        }

        public static string Unfit(ModelInfo info, RequestProfile profile, string operation)
        {
            if (info == null) return null;
            if (operation == "embeddings" || IsAudio(operation)) return null;
            if (info.Embedding) return "embedding model";
            if (profile.HasTools && info.Tools == false) return "no tool support";
            if (profile.HasImages && info.Vision == false) return "no image input";
            if (info.ContextWindow.HasValue && info.ContextWindow.Value > 0)
            {
                var need = profile.EstimatedTokens + (profile.MaxOutputTokens ?? 0);
                if (need > info.ContextWindow.Value) return "needs ~" + need + " tokens, window is " + info.ContextWindow.Value;
            }
            return null;
        }

        private static Candidate Park(Candidate c, HealthMonitor health, BreakerBoard breakers, DateTimeOffset now)
        {
            var pb = breakers.Find(c.Provider.Id);
            var mb = breakers.Find(c.Ref);
            if (pb != null && pb.State(now) == BreakerState.Open) { c.Parked = true; c.ParkedBecause = "circuit open: " + (pb.LastError ?? "recent failures"); }
            else if (mb != null && mb.State(now) == BreakerState.Open) { c.Parked = true; c.ParkedBecause = "model unavailable: " + (mb.LastError ?? "recent failures"); }
            else
            {
                var h = health == null ? null : health.Provider(c.Provider.Id);
                if (h != null && h.Reachable == false) { c.Parked = true; c.ParkedBecause = "probe failed: " + (h.LastError ?? "unreachable"); }
                else if (h != null && h.AuthOk == false) { c.Parked = true; c.ParkedBecause = "key rejected"; }
            }
            return c;
        }

        /// <summary>The models the last probe found on <paramref name="providerId"/>; empty when there is no probe yet.</summary>
        private static List<ModelInfo> HealthModels(HealthMonitor health, string providerId)
        {
            var h = health == null ? null : health.Provider(providerId);
            return h == null || h.Models == null ? new List<ModelInfo>() : h.Models;
        }

        private static double? FirstByteMs(Metrics metrics, string target)
        {
            var s = metrics.Stats(target);
            return s == null ? null : s.EwmaFirstByteMs;
        }

        private void Order(RouteConfig route, List<Candidate> list, Metrics metrics)
        {
            if (list.Count < 2) return;
            if (route.Strategy == RouteStrategy.Fastest && metrics != null)
            {
                var known = list.Where(c => FirstByteMs(metrics, c.Ref) != null).OrderBy(c => FirstByteMs(metrics, c.Ref).Value).ToList();
                var unknown = list.Where(c => FirstByteMs(metrics, c.Ref) == null).ToList();
                list.Clear();
                list.AddRange(known);
                list.AddRange(unknown);
            }
            else if (route.Strategy == RouteStrategy.RoundRobin)
            {
                var turn = _turns.AddOrUpdate(route.Id, 0, (k, v) => unchecked(v + 1));
                var shift = ((turn % list.Count) + list.Count) % list.Count;
                var rotated = list.Skip(shift).Concat(list.Take(shift)).ToList();
                list.Clear();
                list.AddRange(rotated);
            }
        }

        private static Candidate AutoLocal(FairleadConfig cfg, RequestProfile profile, HealthMonitor health, BreakerBoard breakers, string operation, DateTimeOffset now)
        {
            if (health == null || IsAudio(operation)) return null;
            foreach (var p in cfg.Providers.Where(x => x.Enabled && x.Local))
            {
                var h = health.Provider(p.Id);
                if (h == null || h.Reachable != true) continue;
                var models = p.Models.Concat(h.Models).Where(m => Unfit(m, profile, operation) == null).ToList();
                if (operation == "embeddings") models = models.Where(m => m.Embedding).ToList();
                // Prefer a model that can use tools when the request has them, then one already loaded in memory
                // (no load delay), then the order the operator listed models in, then discovery order.
                var loaded = h.Loaded;
                var pick = models
                    .Select((m, i) => new { m, i })
                    .OrderByDescending(x => profile.HasTools && x.m.Tools == true)
                    .ThenByDescending(x => loaded.Contains(x.m.Id, StringComparer.OrdinalIgnoreCase))
                    .ThenBy(x => x.i)
                    .Select(x => x.m)
                    .FirstOrDefault();
                if (pick == null) continue;
                var c = Park(new Candidate { Provider = p, Model = pick.Id, Info = pick }, health, breakers, now);
                if (!c.Parked) return c;
            }
            return null;
        }
    }
}
