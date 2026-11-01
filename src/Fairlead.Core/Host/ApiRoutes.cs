using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Gateway;
using Fairlead.Core.Protocol;
using Fairlead.Core.Providers;
using Fairlead.Core.Resilience;
using Fairlead.Core.Serialization;
using Fairlead.Core.Telemetry;

namespace Fairlead.Core.Host
{
    public sealed partial class FairleadHost
    {
        // ---------- gateway (/v1, client keys) ----------

        /// <summary>The durable request log (who, what, which model), filtered by the query string. Never holds prompt or response text.</summary>
        private List<RequestLogEntry> RequestLogEntries(HttpRequest r)
        {
            var q = r.QueryValues;
            Func<string, string> get = k => { string v; return q.TryGetValue(k, out v) ? v : null; };
            var query = new RequestLogQuery { Client = get("client"), Label = get("label"), Target = get("model"), Route = get("route"), Status = get("status") };
            int limit;
            if (int.TryParse(get("limit"), out limit)) query.Limit = limit;
            var now = DateTimeOffset.UtcNow;
            query.Since = RequestLog.ParseSince(get("since"), now);
            query.Until = RequestLog.ParseSince(get("until"), now);
            if (!string.IsNullOrWhiteSpace(get("since")) && !query.Since.HasValue) throw new BadRequestException("'since' is a span such as 90m, 24h or 7d, or a date and time.");
            return RequestLog.Read(Metrics.LogDirectory, query);
        }

        private void RegisterGatewayRoutes()
        {
            Gateway.Post("/v1/chat/completions", r => Engine.HandleChatAsync(r, Dialect.OpenAi));
            Gateway.Post("/v1/messages", r => Engine.HandleChatAsync(r, Dialect.Anthropic));
            Gateway.Post("/v1/embeddings", r => Engine.HandleEmbeddingsAsync(r));
            Gateway.Post("/v1/completions", r => LegacyCompletions.HandleAsync(Engine, r));
            Gateway.Post("/v1/audio/speech", r => Engine.HandleAudioAsync(r, "speech"));
            Gateway.Post("/v1/audio/transcriptions", r => Engine.HandleAudioAsync(r, "transcriptions"));
            Gateway.Post("/v1/messages/count_tokens", CountTokens);
            Gateway.Get("/v1/models", ListModels);
            Gateway.Get("/v1/models/{id}", GetModel);
            Gateway.Get("/v1/models/{provider}/{model}", GetModel); // a provider/model id has a slash in it
            Gateway.Get("/v1/health", r => Task.FromResult(Json.Ok(new { ok = true, kedge = Health.KedgeActive, internet = Health.Connectivity.Internet.ToString().ToLowerInvariant() })));
        }

        /// <summary>Anthropic's token counter, which Claude Code calls before long prompts. Estimated: no provider is asked.</summary>
        private Task<HttpResponse> CountTokens(HttpRequest r)
        {
            ClientKey client;
            string error;
            if (!Engine.Authenticate(r, out client, out error)) return Task.FromResult(HttpResponse.Json(401, Translate.Error(Dialect.Anthropic, 401, error).ToString(Formatting.None)));
            var body = Json.Body(r);
            var profile = Routing.RequestProfile.From(body, Dialect.Anthropic, false);
            return Task.FromResult(Json.Ok(new Dictionary<string, object> { { "input_tokens", profile.EstimatedTokens } }));
        }

        private Task<HttpResponse> ListModels(HttpRequest r)
        {
            ClientKey client;
            string error;
            if (!Engine.Authenticate(r, out client, out error)) return Task.FromResult(HttpResponse.Json(401, Translate.Error(Dialect.OpenAi, 401, error).ToString(Formatting.None)));
            var created = StartedAt.ToUnixTimeSeconds();
            var data = new JArray();
            string capability;
            r.QueryValues.TryGetValue("capability", out capability);
            var audioOnly = string.Equals(capability, "audio", StringComparison.OrdinalIgnoreCase);
            foreach (var m in ModelIds(client))
            {
                var audio = AudioKinds(m.Key);
                if (audioOnly && audio.Count == 0) continue;
                var entry = new JObject { { "id", m.Key }, { "object", "model" }, { "created", created }, { "owned_by", m.Value }, { "type", "model" }, { "display_name", m.Key } };
                if (audio.Count > 0)
                {
                    // Additive: which of /v1/audio/speech and /v1/audio/transcriptions this id can serve.
                    var kinds = new JArray();
                    foreach (var k in audio) kinds.Add(new JValue(k));
                    entry["audio"] = kinds;
                }
                var window = ContextWindowOf(m.Key);
                if (window.HasValue) entry["context_window"] = window.Value; // only when a provider reported it or the operator entered it
                data.Add(entry);
            }
            // Both shapes in one: OpenAI clients read data[].id; Anthropic clients read data[].id plus has_more.
            return Task.FromResult(Json.Ok(new JObject { { "object", "list" }, { "data", data }, { "has_more", false } }));
        }

        private Task<HttpResponse> GetModel(HttpRequest r)
        {
            ClientKey client;
            string error;
            if (!Engine.Authenticate(r, out client, out error)) return Task.FromResult(HttpResponse.Json(401, Translate.Error(Dialect.OpenAi, 401, error).ToString(Formatting.None)));
            string id;
            if (!r.RouteValues.TryGetValue("id", out id)) id = r.RouteValues["provider"] + "/" + r.RouteValues["model"];
            id = Uri.UnescapeDataString(id);
            var hit = ModelIds(client).FirstOrDefault(m => string.Equals(m.Key, id, StringComparison.OrdinalIgnoreCase));
            if (hit.Key == null) return Task.FromResult(HttpResponse.Json(404, Translate.Error(Dialect.OpenAi, 404, "Unknown model '" + id + "'.").ToString(Formatting.None)));
            var one = new JObject { { "id", hit.Key }, { "object", "model" }, { "created", StartedAt.ToUnixTimeSeconds() }, { "owned_by", hit.Value } };
            var audio = AudioKinds(hit.Key);
            if (audio.Count > 0)
            {
                var kinds = new JArray();
                foreach (var k in audio) kinds.Add(new JValue(k));
                one["audio"] = kinds;
            }
            var oneWindow = ContextWindowOf(hit.Key);
            if (oneWindow.HasValue) one["context_window"] = oneWindow.Value;
            return Task.FromResult(Json.Ok(one));
        }

        /// <summary>The context window of a provider/model id, when it is known; for a route, the smallest window among its targets (what a prompt must fit in the worst case).</summary>
        private int? ContextWindowOf(string id)
        {
            var cfg = Config.Current;
            var route = cfg.FindRoute(id);
            var targets = route != null ? route.Targets : new List<string> { id };
            int? smallest = null;
            foreach (var t in targets)
            {
                var slash = t.IndexOf('/');
                if (slash <= 0) continue;
                var provider = cfg.FindProvider(t.Substring(0, slash));
                if (provider == null || !provider.Enabled) continue;
                var name = t.Substring(slash + 1);
                var info = provider.FindModel(name) ?? DiscoveredModels(provider.Id).FirstOrDefault(m => string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase));
                if (info == null || !info.ContextWindow.HasValue || info.ContextWindow.Value <= 0) continue;
                if (!smallest.HasValue || info.ContextWindow.Value < smallest.Value) smallest = info.ContextWindow.Value;
            }
            return smallest;
        }

        /// <summary>
        /// The audio operations an id (a route or provider/model) can serve: only OpenAI-compatible providers do, and a model
        /// is an audio model by its name (see <see cref="AudioModels"/>). A route has them when one of its targets does.
        /// </summary>
        private List<string> AudioKinds(string id)
        {
            var cfg = Config.Current;
            var kinds = new List<string>();
            var route = cfg.FindRoute(id);
            var targets = route != null ? route.Targets : new List<string> { id };
            foreach (var t in targets)
            {
                var slash = t.IndexOf('/');
                if (slash <= 0) continue;
                var provider = cfg.FindProvider(t.Substring(0, slash));
                if (provider == null || !provider.Enabled || provider.Kind != ProviderKind.OpenAi) continue;
                foreach (var k in AudioModels.KindsOf(t.Substring(slash + 1))) if (!kinds.Contains(k)) kinds.Add(k);
            }
            return kinds;
        }

        /// <summary>Routes first (what tools should use), then every provider/model target.</summary>
        private List<KeyValuePair<string, string>> ModelIds(ClientKey client)
        {
            var cfg = Config.Current;
            var list = new List<KeyValuePair<string, string>>();
            foreach (var route in cfg.Routes)
            {
                if (client != null && client.Routes.Count > 0 && !client.Routes.Contains(route.Id, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(new KeyValuePair<string, string>(route.Id, "fairlead"));
            }
            if (client != null && client.Routes.Count > 0) return list;
            foreach (var p in cfg.Providers.Where(x => x.Enabled))
            {
                var models = p.Models.Select(m => m.Id).Concat(DiscoveredModels(p.Id).Select(m => m.Id)).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var m in models) list.Add(new KeyValuePair<string, string>(p.Id + "/" + m, p.Id));
            }
            return list;
        }

        // ---------- admin (/api, session token) ----------

        private void RegisterAdminRoutes()
        {
            Admin.Get("/api", r => Task.FromResult(Json.Ok(new { version = Version, docs = "docs/API.md", cli = "fairlead help", routes = Admin.Describe() })));
            Admin.Get("/api/commands", r =>
            {
                var script = CommandCatalog.ScriptPath(Settings.RootDirectory);
                if (script == null) throw new NotFoundException("The command line (scripts/fairlead.ps1) is not installed next to this engine, so there is no command list to give.");
                return Task.FromResult(Json.Ok(CommandCatalog.Document(CommandCatalog.FromScript(System.IO.File.ReadAllText(script)))));
            });
            Admin.Get("/api/health", r => Task.FromResult(Json.Ok(new { ok = true, version = Version })));
            Admin.Get("/api/status", r => Task.FromResult(Json.Ok(Status())));
            Admin.Post("/api/heartbeat", r =>
            {
                var body = Json.Body(r);
                RecordPresence(Json.Read<bool?>(body, "leaving") == true);
                return Task.FromResult(Json.Ok(new { ok = true }));
            });
            Admin.Post("/api/session/stop", r =>
            {
                if (!string.Equals(r.Header(IntentHeader), "end-session", StringComparison.Ordinal))
                    throw new BadRequestException("Stopping Fairlead needs the X-Fairlead-Intent: end-session header.");
                Log.Info("SESSION_END", "Stop requested from the browser.");
                Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); Stop(); });
                return Task.FromResult(Json.Ok(new { stopping = true }));
            });

            Admin.Get("/api/config", r => Task.FromResult(Json.Ok(ConfigView(Config.Snapshot()))));
            Admin.Get("/api/presets", r => Task.FromResult(Json.Ok(ProviderPresets.All)));
            Admin.Get("/api/detect", DetectLocal);

            Admin.Post("/api/providers", SaveProvider);
            Admin.Delete("/api/providers/{id}", DeleteProvider);
            Admin.Post("/api/providers/{id}/test", TestProvider);
            Admin.Post("/api/providers/{id}/discover", DiscoverProvider);

            Admin.Post("/api/gateway/import", ImportGatewayFile);
            Admin.Post("/api/gateway/values", SaveGatewayValues);
            Admin.Post("/api/gateway/curl", ImportGatewayCurl);
            Admin.Post("/api/gateway/test", TestGatewayModel);
            Admin.Delete("/api/gateway", r =>
            {
                GatewayValues.Clear();
                Log.Info("GATEWAY_IMPORT_REMOVED", "Every imported AI gateway model file and the stored values were deleted.");
                LoadGatewayModels();
                return Task.FromResult(Json.Ok(GatewayView()));
            });
            Admin.Delete("/api/gateway/{name}", r =>
            {
                var name = r.RouteValues["name"];
                if (!GatewayValues.RemoveFile(name)) throw new NotFoundException("No imported model file called " + name + ".");
                Log.Info("GATEWAY_IMPORT_REMOVED", "Imported AI gateway model file " + name + " was deleted.");
                LoadGatewayModels();
                return Task.FromResult(Json.Ok(GatewayView()));
            });

            Admin.Get("/api/lazarette", r => Task.FromResult(Json.Ok(LazaretteView())));
            Admin.Post("/api/lazarette/pair", async r =>
            {
                var code = Json.Read<string>(Json.Body(r), "code");
                if (string.IsNullOrWhiteSpace(code)) throw new BadRequestException("Paste the pairing code from Lazarette (Settings, Fairlead, Pair).");
                await Lazarette.PairAsync(code).ConfigureAwait(false);
                Log.Info("LAZARETTE_PAIRED", "Paired with Lazarette (" + Lazarette.PairingHint + "); state " + Lazarette.State + ".");
                return Json.Ok(LazaretteView());
            });
            Admin.Delete("/api/lazarette/pair", r =>
            {
                Lazarette.Unpair();
                Log.Info("LAZARETTE_UNPAIRED", "Lazarette pairing removed; keys from Lazarette forgotten.");
                return Task.FromResult(Json.Ok(LazaretteView()));
            });
            Admin.Post("/api/lazarette/refresh", async r =>
            {
                await Lazarette.PollAsync().ConfigureAwait(false);
                return Json.Ok(LazaretteView());
            });

            Admin.Post("/api/routes", SaveRoute);
            Admin.Delete("/api/routes/{id}", DeleteRoute);
            Admin.Post("/api/routing", SaveRouting);
            Admin.Post("/api/routing/reset", r =>
            {
                Config.Update(c => { c.Rules = FairleadConfig.DefaultRules(); c.Aliases = FairleadConfig.DefaultAliases(); });
                return Task.FromResult(Json.Ok(ConfigView(Config.Snapshot())));
            });
            Admin.Post("/api/settings", SaveSettings);
            Admin.Post("/api/kedge", r =>
            {
                var mode = ParseKedge(Json.Read<string>(Json.Body(r), "mode"));
                Config.Update(c => c.Kedge = mode);
                return Task.FromResult(Json.Ok(new { mode = mode.ToString().ToLowerInvariant(), connectivity = Health.Connectivity }));
            });

            Admin.Post("/api/clients", CreateClient);
            Admin.Get("/api/clients/usage", r =>
            {
                var now = DateTime.UtcNow;
                return Task.FromResult(Json.Ok(Config.Current.Clients.Select(k => new
                {
                    k.Id,
                    k.Name,
                    usedToday = Metrics.Usage.Used(k.Id, now),
                    dailyTokenLimit = k.DailyTokenLimit,
                    dailyTokenSoftLimit = k.DailyTokenSoftLimit,
                    resetsInSeconds = ClientUsage.SecondsToReset(now),
                }).ToList()));
            });
            Admin.Post("/api/clients/{id}/budget", r =>
            {
                var id = r.RouteValues["id"];
                var body = Json.Body(r);
                var daily = Json.Read<long?>(body, "dailyTokens");
                var soft = Json.Read<long?>(body, "softDailyTokens");
                if ((daily.HasValue && daily.Value < 0) || (soft.HasValue && soft.Value < 0)) throw new BadRequestException("The daily token limits are whole numbers of tokens (0 or empty for no limit).");
                if (daily.HasValue && soft.HasValue && daily.Value > 0 && soft.Value > daily.Value) throw new BadRequestException("The soft limit must be below the daily limit.");
                var found = false;
                Config.Update(c =>
                {
                    var k = c.Clients.FirstOrDefault(x => x.Id == id);
                    if (k == null) return;
                    found = true;
                    if (body["dailyTokens"] != null) k.DailyTokenLimit = daily.HasValue && daily.Value > 0 ? daily : null;
                    if (body["softDailyTokens"] != null) k.DailyTokenSoftLimit = soft.HasValue && soft.Value > 0 ? soft : null;
                });
                if (!found) throw new NotFoundException("No client key " + id + ".");
                var now = Config.Current.Clients.First(x => x.Id == id);
                return Task.FromResult(Json.Ok(new { id = id, dailyTokenLimit = now.DailyTokenLimit, dailyTokenSoftLimit = now.DailyTokenSoftLimit }));
            });
            Admin.Post("/api/clients/{id}/rate-limit", r =>
            {
                var id = r.RouteValues["id"];
                var rate = Json.Read<int?>(Json.Body(r), "requestsPerMinute");
                if (rate.HasValue && (rate.Value < 0 || rate.Value > 1000000)) throw new BadRequestException("requestsPerMinute is a whole number of requests a minute (0 or empty for no limit).");
                var found = false;
                Config.Update(c =>
                {
                    var k = c.Clients.FirstOrDefault(x => x.Id == id);
                    if (k == null) return;
                    found = true;
                    k.RateLimitPerMinute = rate.HasValue && rate.Value > 0 ? rate : null;
                });
                if (!found) throw new NotFoundException("No client key " + id + ".");
                Engine.ForgetClient(id);
                return Task.FromResult(Json.Ok(new { id = id, rateLimitPerMinute = rate.HasValue && rate.Value > 0 ? rate : null }));
            });
            Admin.Delete("/api/clients/{id}", r =>
            {
                var id = r.RouteValues["id"];
                var removed = false;
                Config.Update(c => removed = c.Clients.RemoveAll(k => k.Id == id) > 0);
                Engine.ForgetClient(id);
                if (!removed) throw new NotFoundException("No client key " + id + ".");
                return Task.FromResult(Json.Ok(new { removed = id }));
            });

            Admin.Get("/api/health/providers", r => Task.FromResult(Json.Ok(new
            {
                connectivity = Health.Connectivity,
                providers = Health.Providers(),
                breakers = Breakers.All.Select(b => b.Describe(DateTimeOffset.UtcNow)),
            })));
            Admin.Post("/api/probe", async r =>
            {
                await Health.ProbeOnceAsync(CancellationToken.None).ConfigureAwait(false);
                return Json.Ok(new { connectivity = Health.Connectivity, providers = Health.Providers() });
            });
            Admin.Get("/api/cache", r => Task.FromResult(Json.Ok(Engine.Cache == null ? (object)new { enabled = false } : Engine.Cache.Status())));
            Admin.Post("/api/cache/clear", r =>
            {
                var removed = Engine.Cache == null ? 0 : Engine.Cache.Clear();
                return Task.FromResult(Json.Ok(new { cleared = removed }));
            });
            Admin.Post("/api/breakers/reset", r =>
            {
                var key = Json.Read<string>(Json.Body(r), "provider");
                if (string.IsNullOrEmpty(key)) foreach (var b in Breakers.All) b.Reset();
                else Breakers.ResetProvider(key);
                return Task.FromResult(Json.Ok(new { reset = key ?? "all" }));
            });

            Admin.Get("/api/requests", r =>
            {
                int limit;
                if (!int.TryParse(r.QueryValues.ContainsKey("limit") ? r.QueryValues["limit"] : "200", out limit)) limit = 200;
                return Task.FromResult(Json.Ok(Metrics.Recent(Math.Max(1, Math.Min(1000, limit)))));
            });
            Admin.Get("/api/requests/{id}", r =>
            {
                var rec = Metrics.Find(r.RouteValues["id"]);
                if (rec == null) throw new NotFoundException("That request is no longer in the activity log.");
                return Task.FromResult(Json.Ok(rec));
            });
            Admin.Get("/api/bearing", r => Task.FromResult(Json.Ok(Engine.CitedAnswers == null ? (object)new { enabled = false } : Engine.CitedAnswers.Status())));
            Admin.Get("/api/bearing/search", r =>
            {
                string q;
                if (!r.QueryValues.TryGetValue("q", out q) || string.IsNullOrWhiteSpace(q)) throw new BadRequestException("Give the question as ?q=...");
                int limit;
                string l;
                if (!r.QueryValues.TryGetValue("limit", out l) || !int.TryParse(l, out limit)) limit = Fairlead.Core.Bearing.BearingService.DefaultTop;
                if (Engine.CitedAnswers == null || !Engine.CitedAnswers.Enabled) throw new BadRequestException("Cited answers are not set up: set FAIRLEAD_BEARING_DIR to a folder of documents and restart Fairlead.");
                return Task.FromResult(Json.Ok(Engine.CitedAnswers.Search(q, Math.Max(1, limit))));
            });
            Admin.Get("/api/requestlog", r => Task.FromResult(Json.Ok(RequestLogEntries(r))));
            Admin.Get("/api/requestlog/summary", r => Task.FromResult(Json.Ok(RequestLog.Summarize(RequestLogEntries(r)))));
            Admin.Get("/api/metrics", r => Task.FromResult(Json.Ok(Metrics.Summary())));
            Admin.Get("/api/live", r => Task.FromResult(LiveStream()));
            Admin.Get("/api/logs", r =>
            {
                string level;
                r.QueryValues.TryGetValue("level", out level);
                return Task.FromResult(Json.Ok(Log.Recent(300, string.IsNullOrEmpty(level) ? null : level)));
            });

            Admin.Post("/api/explain", r =>
            {
                var body = Json.Body(r);
                var request = PlaygroundBody(body, false);
                return Task.FromResult(Json.Ok(Engine.Explain(request, Dialect.OpenAi, Json.Read<string>(body, "route"), Json.Read<bool?>(body, "private") == true, Json.Read<string>(body, "client"))));
            });
            Admin.Post("/api/playground", Playground);
        }

        /// <summary>
        /// Server-sent events for the live routing view: start, attempt, firstByte and done for every gateway request,
        /// as they happen. Same session token as the rest of /api (the UI reads it with fetch, not EventSource, so the
        /// token stays in a header). A comment line every 15 s keeps the connection open and notices a closed tab.
        /// </summary>
        private HttpResponse LiveStream()
        {
            return HttpResponse.EventStream(async (output, token) =>
            {
                using (var sub = Metrics.Live.Subscribe())
                {
                    try
                    {
                        await output.WriteAsync("retry: 3000\nevent: hello\ndata: " + Json.Serialize(new { version = Version, kedge = Health.KedgeActive }) + "\n\n", token).ConfigureAwait(false);
                        while (!token.IsCancellationRequested)
                        {
                            var ev = await sub.NextAsync(15000, token).ConfigureAwait(false);
                            var dropped = sub.TakeDropped();
                            if (dropped > 0) await output.WriteAsync("event: dropped\ndata: {\"count\":" + dropped + "}\n\n", token).ConfigureAwait(false);
                            if (ev == null) { await output.WriteAsync(": keepalive\n\n", token).ConfigureAwait(false); continue; }
                            await output.WriteAsync("event: " + ev.Type + "\ndata: " + ev.Json + "\n\n", token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (System.IO.IOException) { }
                    catch (ObjectDisposedException) { }
                }
            });
        }

        public object Status()
        {
            var cfg = Config.Current;
            return new
            {
                version = Version,
                port = Port,
                registryPort = FairleadSettings.RegistryPort,
                preferredPort = Options.PreferredPort > 0 ? Options.PreferredPort : Settings.PreferredPort,
                gatewayUrl = BaseUrl + "/v1",
                startedAt = StartedAt,
                autoStop = Options.AutoStop,
                setupComplete = cfg.SetupComplete,
                requireClientKey = cfg.RequireClientKey,
                clients = cfg.Clients.Count,
                configFile = Config.FilePath,
                logFile = Log.FilePath,
                connectivity = Health.Connectivity,
                providers = cfg.Providers.Select(p =>
                {
                    var h = Health.Provider(p.Id);
                    var b = Breakers.Find(p.Id);
                    return new
                    {
                        id = p.Id,
                        name = p.Name,
                        local = p.Local,
                        enabled = p.Enabled,
                        reachable = h == null ? null : h.Reachable,
                        authOk = h == null ? null : h.AuthOk,
                        probeMs = h == null ? null : h.ProbeMs,
                        lastError = h == null ? null : h.LastError,
                        models = p.Models.Select(m => m.Id).Concat(DiscoveredModels(p.Id).Select(m => m.Id)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        breaker = b == null ? "closed" : b.State(DateTimeOffset.UtcNow).ToString().ToLowerInvariant(),
                        keySource = SecretProtector.KeySource(p, Lazarette.Value),
                    };
                }),
                routes = cfg.Routes.Select(r => new { id = r.Id, strategy = r.Strategy, targets = r.Targets.Count }),
                gatewayModels = GatewayView(),
                launcher = Options.LauncherState,
                metrics = Metrics.Summary(),
            };
        }

        /// <summary>The config as the browser sees it: keys replaced by where they come from and their last four characters.</summary>
        private object ConfigView(FairleadConfig c)
        {
            return new
            {
                c.SetupComplete,
                providers = c.Providers.Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Kind,
                    p.Preset,
                    p.BaseUrl,
                    p.Local,
                    p.Enabled,
                    p.ApiKeyEnv,
                    p.TimeoutSeconds,
                    p.Headers,
                    p.StreamUsage,
                    models = p.Models,
                    discovered = DiscoveredModels(p.Id),
                    keySource = SecretProtector.KeySource(p, Lazarette.Value),
                    keyHint = SecretProtector.KeyHint(p),
                    managed = p.Managed == true,
                    p.Path,
                    p.Retries,
                }),
                c.Routes,
                c.Rules,
                c.Aliases,
                c.DefaultRoute,
                c.AutoFallbackRoute,
                kedge = c.Kedge,
                c.RequireClientKey,
                clients = c.Clients.Select(k => new { k.Id, k.Name, k.Prefix, k.Created, k.Routes, k.RateLimitPerMinute, k.DailyTokenLimit, k.DailyTokenSoftLimit }),
                c.Network,
                c.Privacy,
                c.CorsOrigins,
                c.LogContent,
                c.LazaretteHoldHours,
            };
        }

        private async Task<HttpResponse> DetectLocal(HttpRequest r)
        {
            var cfg = Config.Current;
            var found = await Task.WhenAll(ProviderPresets.LocalByAddress().Select(async group =>
            {
                var preset = group[0];
                var name = string.Join(" or ", group.Select(g => g.Name));
                var alsoPreset = group.Skip(1).Select(g => g.Id).ToList();
                var probe = ProviderPresets.Create(preset.Id);
                var existing = cfg.Providers.FirstOrDefault(p => string.Equals(p.BaseUrl.TrimEnd('/'), probe.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                try
                {
                    var models = await Upstream.ListModelsAsync(existing ?? probe, CancellationToken.None, 2500).ConfigureAwait(false);
                    return new { preset = preset.Id, name = name, alsoPreset = alsoPreset, baseUrl = probe.BaseUrl, running = true, configured = existing == null ? null : existing.Id, models = models, error = (string)null };
                }
                catch (UpstreamException ex)
                {
                    return new { preset = preset.Id, name = name, alsoPreset = alsoPreset, baseUrl = probe.BaseUrl, running = ex.Kind != FailureKind.Network && ex.Kind != FailureKind.Timeout, configured = existing == null ? null : existing.Id, models = new List<ModelInfo>(), error = (string)ex.Message };
                }
            })).ConfigureAwait(false);
            return Json.Ok(found);
        }

        /// <summary>The models the last probe found on a provider; empty before the first probe.</summary>
        private List<ModelInfo> DiscoveredModels(string providerId)
        {
            var h = Health.Provider(providerId);
            return h == null || h.Models == null ? new List<ModelInfo>() : h.Models;
        }

        private Task<HttpResponse> SaveProvider(HttpRequest r)
        {
            var body = Json.Body(r);
            var incoming = body["provider"] as JObject;
            if (incoming == null) throw new BadRequestException("Send { provider: {...}, apiKey?: string, clearKey?: bool }.");
            var id = (string)incoming["id"];
            if (!string.IsNullOrWhiteSpace(id) && Config.IsManaged(id)) throw new BadRequestException(ManagedMessage(id));
            var preset = (string)incoming["preset"];
            var apiKey = Json.Read<string>(body, "apiKey");
            var clearKey = Json.Read<bool?>(body, "clearKey") == true;
            ProviderConfig saved = null;
            Config.Update(c =>
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    if (string.IsNullOrWhiteSpace(preset)) throw new BadRequestException("A new provider needs a preset or an id.");
                    id = UniqueId(c, preset);
                }
                var p = c.FindProvider(id);
                if (p == null)
                {
                    p = ProviderPresets.Find(preset) != null ? ProviderPresets.Create(preset, id) : new ProviderConfig { Id = id };
                    c.Providers.Add(p);
                }
                var oldBase = p.BaseUrl;
                if (incoming["name"] != null) p.Name = (string)incoming["name"] ?? p.Name;
                if (incoming["kind"] != null) p.Kind = ParseKind((string)incoming["kind"]);
                if (incoming["baseUrl"] != null) p.BaseUrl = ((string)incoming["baseUrl"] ?? p.BaseUrl).Trim();
                if (incoming["local"] != null) p.Local = (bool)incoming["local"];
                if (incoming["enabled"] != null) p.Enabled = (bool)incoming["enabled"];
                if (incoming["apiKeyEnv"] != null) p.ApiKeyEnv = string.IsNullOrWhiteSpace((string)incoming["apiKeyEnv"]) ? null : ((string)incoming["apiKeyEnv"]).Trim();
                if (incoming["timeoutSeconds"] != null) p.TimeoutSeconds = (int)incoming["timeoutSeconds"];
                if (incoming["streamUsage"] != null) p.StreamUsage = (bool)incoming["streamUsage"];
                var headers = incoming["headers"] as JObject;
                if (headers != null) p.Headers = headers.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>();
                var models = incoming["models"] as JArray;
                if (models != null) p.Models = models.ToObject<List<ModelInfo>>() ?? new List<ModelInfo>();
                // A key typed in the browser may only go where the provider already pointed, or to a URL saved in this same call.
                if (clearKey) p.ApiKeyProtected = null;
                if (!string.IsNullOrWhiteSpace(apiKey)) p.ApiKeyProtected = SecretProtector.Protect(apiKey.Trim());
                if (!string.Equals(oldBase, p.BaseUrl, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(apiKey) && p.ApiKeyProtected != null && !string.IsNullOrEmpty(oldBase))
                    p.ApiKeyProtected = null; // never send a stored key to a new address without it being re-entered
                saved = p;
            });
            Breakers.ResetProvider(id);
            Health.Nudge();
            return Task.FromResult(Json.Ok(ConfigView(Config.Snapshot())));
        }

        private static string UniqueId(FairleadConfig c, string baseId)
        {
            var id = baseId;
            var n = 2;
            while (c.FindProvider(id) != null) id = baseId + "-" + n++;
            return id;
        }

        private string ManagedMessage(string id)
        {
            return "Provider '" + id + "' comes from the AI gateway model file" + (GatewayModels == null ? string.Empty : " (" + GatewayModels.Path + ")") + ". Import a changed file under Providers, AI gateway; it takes effect at once.";
        }

        private Task<HttpResponse> DeleteProvider(HttpRequest r)
        {
            var id = r.RouteValues["id"];
            if (Config.IsManaged(id)) throw new BadRequestException(ManagedMessage(id));
            Config.Update(c =>
            {
                if (c.Providers.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) == 0) throw new NotFoundException("No provider " + id + ".");
                foreach (var route in c.Routes) route.Targets.RemoveAll(t => t.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase));
            });
            return Task.FromResult(Json.Ok(ConfigView(Config.Snapshot())));
        }

        private async Task<HttpResponse> TestProvider(HttpRequest r)
        {
            var p = Config.Current.FindProvider(r.RouteValues["id"]);
            if (p == null) throw new NotFoundException("No provider " + r.RouteValues["id"] + ".");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var models = await Upstream.ListModelsAsync(p, CancellationToken.None, 10000).ConfigureAwait(false);
                Breakers.ResetProvider(p.Id);
                Health.Nudge();
                return Json.Ok(new { ok = true, ms = sw.ElapsedMilliseconds, models = models, keySource = SecretProtector.KeySource(p, Lazarette.Value) });
            }
            catch (UpstreamException ex)
            {
                return Json.Ok(new { ok = false, ms = sw.ElapsedMilliseconds, error = ex.Message, kind = ex.Kind.ToString().ToLowerInvariant(), keySource = SecretProtector.KeySource(p, Lazarette.Value) });
            }
        }

        /// <summary>Saves the provider's current model list into the config, keeping prices and capabilities typed in before.</summary>
        private async Task<HttpResponse> DiscoverProvider(HttpRequest r)
        {
            var id = r.RouteValues["id"];
            if (Config.IsManaged(id)) throw new BadRequestException(ManagedMessage(id));
            var p = Config.Current.FindProvider(id);
            if (p == null) throw new NotFoundException("No provider " + id + ".");
            var models = await Upstream.ListModelsAsync(p, CancellationToken.None, 10000).ConfigureAwait(false);
            Config.Update(c =>
            {
                var target = c.FindProvider(id);
                foreach (var m in models)
                {
                    var existing = target.FindModel(m.Id);
                    if (existing == null) { target.Models.Add(m); continue; }
                    if (existing.ContextWindow == null) existing.ContextWindow = m.ContextWindow;
                    if (existing.Tools == null) existing.Tools = m.Tools;
                    if (existing.Vision == null) existing.Vision = m.Vision;
                    existing.Embedding = existing.Embedding || m.Embedding;
                }
            });
            return Json.Ok(ConfigView(Config.Snapshot()));
        }

        private Task<HttpResponse> SaveRoute(HttpRequest r)
        {
            var body = Json.Body(r);
            var incoming = body["route"] as JObject;
            var route = incoming == null ? null : incoming.ToObject<RouteConfig>(Json.Settings);
            if (route == null) throw new BadRequestException("Send { route: {...}, previousId?: string }.");
            var previous = Json.Read<string>(body, "previousId");
            Config.Update(c =>
            {
                var oldId = string.IsNullOrEmpty(previous) ? route.Id : previous;
                var index = c.Routes.FindIndex(x => string.Equals(x.Id, oldId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) c.Routes[index] = route; else c.Routes.Add(route);
                if (!string.Equals(oldId, route.Id, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var rule in c.Rules) if (string.Equals(rule.Route, oldId, StringComparison.OrdinalIgnoreCase)) rule.Route = route.Id;
                    foreach (var k in c.Aliases.Keys.ToList()) if (string.Equals(c.Aliases[k], oldId, StringComparison.OrdinalIgnoreCase)) c.Aliases[k] = route.Id;
                    if (string.Equals(c.DefaultRoute, oldId, StringComparison.OrdinalIgnoreCase)) c.DefaultRoute = route.Id;
                    if (string.Equals(c.AutoFallbackRoute, oldId, StringComparison.OrdinalIgnoreCase)) c.AutoFallbackRoute = route.Id;
                }
            });
            return Task.FromResult(Json.Ok(ConfigView(Config.Snapshot())));
        }

        private Task<HttpResponse> DeleteRoute(HttpRequest r)
        {
            var id = r.RouteValues["id"];
            if (string.Equals(id, "local", StringComparison.OrdinalIgnoreCase)) throw new BadRequestException("The local route is where Kedge mode and private requests land; it cannot be deleted.");
            Config.Update(c =>
            {
                if (c.Routes.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) == 0) throw new NotFoundException("No route " + id + ".");
            });
            return Task.FromResult(Json.Ok(ConfigView(Config.Snapshot())));
        }

        private Task<HttpResponse> SaveRouting(HttpRequest r)
        {
            var body = Json.Body(r);
            Config.Update(c =>
            {
                var rules = body["rules"] as JArray;
                if (rules != null) c.Rules = rules.ToObject<List<RoutingRule>>(Json.Settings) ?? new List<RoutingRule>();
                var aliases = body["aliases"] as JObject;
                if (aliases != null) c.Aliases = aliases.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>();
                var d = Json.Read<string>(body, "defaultRoute");
                if (!string.IsNullOrEmpty(d)) c.DefaultRoute = d;
                var f = Json.Read<string>(body, "autoFallbackRoute");
                if (!string.IsNullOrEmpty(f)) c.AutoFallbackRoute = f;
            });
            return Task.FromResult(Json.Ok(ConfigView(Config.Snapshot())));
        }

        private Task<HttpResponse> SaveSettings(HttpRequest r)
        {
            var body = Json.Body(r);
            Config.Update(c =>
            {
                if (body["kedge"] != null) c.Kedge = ParseKedge((string)body["kedge"]);
                if (body["requireClientKey"] != null) c.RequireClientKey = (bool)body["requireClientKey"];
                if (body["setupComplete"] != null) c.SetupComplete = (bool)body["setupComplete"];
                if (body["lazaretteHoldHours"] != null) c.LazaretteHoldHours = (double)body["lazaretteHoldHours"];
                if (body["logContent"] != null) c.LogContent = (bool)body["logContent"];
                var cors = body["corsOrigins"] as JArray;
                if (cors != null) c.CorsOrigins = cors.Select(x => ((string)x ?? string.Empty).Trim().TrimEnd('/')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var privacy = body["privacy"] as JObject;
                if (privacy != null) c.Privacy = privacy.ToObject<PrivacyConfig>(Json.Settings) ?? c.Privacy;
                var network = body["network"] as JObject;
                if (network != null) c.Network = network.ToObject<NetworkConfig>(Json.Settings) ?? c.Network;
            });
            return Task.FromResult(Json.Ok(ConfigView(Config.Snapshot())));
        }

        private Task<HttpResponse> CreateClient(HttpRequest r)
        {
            var body = Json.Body(r);
            var name = (Json.Read<string>(body, "name") ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 60) throw new BadRequestException("Give the key a name of 1 to 60 characters (the tool that will use it).");
            var routes = Json.Read<List<string>>(body, "routes") ?? new List<string>();
            var rate = Json.Read<int?>(body, "rateLimitPerMinute");
            if (rate.HasValue && (rate.Value < 0 || rate.Value > 1000000)) throw new BadRequestException("rateLimitPerMinute is a whole number of requests a minute (0 or empty for no limit).");
            var daily = Json.Read<long?>(body, "dailyTokenLimit");
            var soft = Json.Read<long?>(body, "dailyTokenSoftLimit");
            if ((daily.HasValue && daily.Value < 0) || (soft.HasValue && soft.Value < 0)) throw new BadRequestException("The daily token limits are whole numbers of tokens (0 or empty for no limit).");
            if (daily.HasValue && soft.HasValue && daily.Value > 0 && soft.Value > daily.Value) throw new BadRequestException("The soft limit must be below the daily limit.");
            var key = GatewayEngine.NewClientKey();
            var client = new ClientKey
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Name = name,
                Prefix = key.Substring(0, 7),
                Hash = GatewayEngine.HashKey(key),
                Routes = routes.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                RateLimitPerMinute = rate.HasValue && rate.Value > 0 ? rate : null,
                DailyTokenLimit = daily.HasValue && daily.Value > 0 ? daily : null,
                DailyTokenSoftLimit = soft.HasValue && soft.Value > 0 ? soft : null,
            };
            Config.Update(c =>
            {
                if (c.Clients.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))) throw new BadRequestException("A key called '" + name + "' already exists.");
                foreach (var route in client.Routes) if (c.FindRoute(route) == null) throw new BadRequestException("Unknown route '" + route + "'.");
                c.Clients.Add(client);
            });
            // The only time the key leaves Fairlead.
            return Task.FromResult(Json.Ok(new { client = new { client.Id, client.Name, client.Prefix, client.Created, client.Routes, client.RateLimitPerMinute, client.DailyTokenLimit, client.DailyTokenSoftLimit }, key = key }));
        }

        private async Task<HttpResponse> Playground(HttpRequest r)
        {
            var body = Json.Body(r);
            var request = PlaygroundBody(body, true);
            var synthetic = new HttpRequest
            {
                Method = "POST",
                Path = "/v1/chat/completions",
                Body = System.Text.Encoding.UTF8.GetBytes(request.ToString(Formatting.None)),
                RequestId = r.RequestId,
                Internal = true,
                Aborted = r.Aborted,
            };
            var route = Json.Read<string>(body, "route");
            if (!string.IsNullOrEmpty(route)) synthetic.Headers[GatewayEngine.RouteHeader] = route;
            if (Json.Read<bool?>(body, "private") == true) synthetic.Headers[GatewayEngine.PrivacyHeader] = "local";
            var response = await Engine.HandleChatAsync(synthetic, Dialect.OpenAi).ConfigureAwait(false);
            var answer = JObject.Parse(System.Text.Encoding.UTF8.GetString(response.Body));
            var rec = Metrics.Find(r.RequestId);
            return Json.Ok(new
            {
                status = response.Status,
                text = response.Status < 300 ? Translate.ContentText(answer["choices"].At(0).At("message").At("content")) : null,
                error = response.Status >= 300 ? (string)answer["error"].At("message") : null,
                route = response.Headers.ContainsKey("X-Fairlead-Route") ? response.Headers["X-Fairlead-Route"] : null,
                target = response.Headers.ContainsKey("X-Fairlead-Target") ? response.Headers["X-Fairlead-Target"] : null,
                record = rec,
            });
        }

        private static JObject PlaygroundBody(JObject body, bool forSend)
        {
            var raw = body["body"] as JObject;
            if (raw != null) { var copy = (JObject)raw.DeepClone(); if (forSend) copy["stream"] = false; return copy; }
            var messages = new JArray();
            var system = Json.Read<string>(body, "system");
            if (!string.IsNullOrWhiteSpace(system)) messages.Add(new JObject { { "role", "system" }, { "content", system } });
            messages.Add(new JObject { { "role", "user" }, { "content", Json.Read<string>(body, "prompt") ?? string.Empty } });
            var req = new JObject { { "model", Json.Read<string>(body, "model") ?? "auto" }, { "messages", messages }, { "stream", false } };
            var max = Json.Read<int?>(body, "maxTokens");
            if (max.HasValue) req["max_tokens"] = max.Value;
            if (Json.Read<bool?>(body, "withTools") == true)
            {
                req["tools"] = new JArray { new JObject { { "type", "function" }, { "function", new JObject { { "name", "get_time" }, { "description", "Current time" }, { "parameters", new JObject { { "type", "object" }, { "properties", new JObject() } } } } } } };
            }
            return req;
        }

        /// <summary>The Lazarette link for the UI: states and names, never a value or the pairing secret.</summary>
        private object LazaretteView()
        {
            return new
            {
                paired = Lazarette.Paired,
                pairing = Lazarette.PairingHint,
                state = Lazarette.State,
                message = Lazarette.Message,
                lastChecked = Lazarette.LastChecked,
                expiresAt = Lazarette.ExpiresAt,
                holdHours = Lazarette.HoldHours,
                port = Port,
                registryPort = FairleadSettings.RegistryPort,
                names = Lazarette.Names().Select(n => new { name = n.Name, state = n.State }),
            };
        }

        /// <summary>The AI gateway model file for the status page: keys, counts, problems and where each variable comes from; never values.</summary>
        private object GatewayView()
        {
            var f = GatewayModels;
            var envPath = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GatewayModelFile.PathVariable));
            if (f == null) return new { present = false, canImport = !envPath, imported = GatewayValues.HasFile };
            return new
            {
                present = true,
                source = GatewaySource,
                path = f.Path,
                ok = f.Ok,
                canImport = !envPath,
                imported = GatewayValues.HasFile,
                endpoints = f.EndpointKeys,
                models = f.ModelKeys.Count,
                enabledModels = f.EnabledModels,
                providers = f.Providers.Select(p => p.Id),
                variables = f.RequiredVariables.Select(v => new
                {
                    name = v,
                    kind = f.TokenVariables.Contains(v) ? "token" : f.BaseUrlVariables.Contains(v) ? "baseUrl" : "other",
                    source = GatewayValues.HasValue(v) ? "stored" : Lazarette.Value(v) != null ? "lazarette" : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)) ? "env" : "missing",
                }),
                errors = f.Errors,
                warnings = f.Warnings,
                files = (GatewayFiles ?? new List<GatewayModelFile>()).Select(x => new
                {
                    name = x.Name,
                    ok = x.Ok,
                    endpoints = x.EndpointKeys,
                    models = x.ModelKeys.Count,
                    enabledModels = x.EnabledModels,
                    providers = x.Providers.Select(p => p.Id),
                    errors = x.Errors,
                    warnings = x.Warnings,
                }),
            };
        }

        /// <summary>
        /// Takes the model file from the browser, refuses it when its structure is wrong (nothing is saved then), else
        /// keeps it encrypted in the data folder and reloads the gateway providers. Values it names can come in the same call.
        /// </summary>
        private Task<HttpResponse> ImportGatewayFile(HttpRequest r)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GatewayModelFile.PathVariable)))
                throw new BadRequestException(GatewayModelFile.PathVariable + " is set, so Fairlead reads the model file from that path and an import would be ignored. Remove the variable first.");
            var body = Json.Body(r);
            var yaml = Json.Read<string>(body, "yaml");
            if (string.IsNullOrWhiteSpace(yaml)) throw new BadRequestException("Choose the model file (YAML) to import.");
            if (yaml.Length > GatewayStore.MaxFileChars) throw new BadRequestException("The model file is too large; a gateway model file is a few KB.");
            var name = GatewayStore.NameFrom(Json.Read<string>(body, "fileName"));
            if (name == null) throw new BadRequestException("Name the model file with letters or digits (for example gemini-chat.yaml).");
            return Task.FromResult(ImportYaml(name, yaml, body, null));
        }

        /// <summary>
        /// Builds a one-endpoint model file from a pasted curl command (URL, format and model from the command; the key only
        /// by the name of its variable) and imports it like a chosen file, under curl-&lt;model&gt;.
        /// </summary>
        private Task<HttpResponse> ImportGatewayCurl(HttpRequest r)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GatewayModelFile.PathVariable)))
                throw new BadRequestException(GatewayModelFile.PathVariable + " is set, so Fairlead reads the model file from that path and an import would be ignored. Remove the variable first.");
            var body = Json.Body(r);
            var curl = Json.Read<string>(body, "curl");
            if (string.IsNullOrWhiteSpace(curl)) throw new BadRequestException("Paste the curl command.");
            if (curl.Length > 64 * 1024) throw new BadRequestException("The command is too long.");
            var c = CurlImport.Parse(curl, Json.Read<string>(body, "tokenVariable"));
            if (!c.Ok) return Task.FromResult(Json.Ok(new { saved = false, errors = c.Errors, warnings = c.Warnings }));
            return Task.FromResult(ImportYaml(c.FileName, c.Yaml, body, c));
        }

        private HttpResponse ImportYaml(string name, string yaml, JObject body, CurlImport fromCurl)
        {
            var check = GatewayModelFile.Inspect(yaml, "the chosen file");
            // Each endpoint key becomes a provider id, so it must not repeat one from another imported file.
            foreach (var other in (GatewayFiles ?? new List<GatewayModelFile>()).Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                foreach (var ep in check.EndpointKeys.Where(k => other.EndpointKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
                    check.Errors.Add("Endpoint '" + ep + "' is already in the imported file " + other.Name + "; rename it in this file, or import this one under the same file name to replace that file.");
            if (!check.Ok) return Json.Ok(new { saved = false, errors = check.Errors, warnings = check.Warnings });
            var values = ReadGatewayValues(body, check);
            var replaced = GatewayValues.Names.Contains(name, StringComparer.OrdinalIgnoreCase);
            GatewayValues.SaveFile(name, yaml);
            if (values.Count > 0) GatewayValues.SetValues(values);
            Log.Info("GATEWAY_IMPORTED", "AI gateway model file " + name + (replaced ? " replaced" : " imported") + " (" + check.EndpointKeys.Count + " endpoints, " + check.ModelKeys.Count + " models); kept encrypted at " + GatewayValues.Location(name) + ".");
            LoadGatewayModels();
            if (fromCurl == null) return Json.Ok(new { saved = true, gateway = GatewayView() });
            return Json.Ok(new
            {
                saved = true,
                gateway = GatewayView(),
                warnings = fromCurl.Warnings,
                fileName = name,
                target = fromCurl.EndpointKey + "/" + fromCurl.Model,
                format = fromCurl.Format,
                tokenVariable = fromCurl.TokenVariable,
            });
        }

        /// <summary>
        /// Sends one short prompt straight to a gateway model (no route, no fallback) and says in plain words what went
        /// wrong: no key, key rejected, wrong address, model or request refused, or unreachable. Never shows the key.
        /// </summary>
        private async Task<HttpResponse> TestGatewayModel(HttpRequest r)
        {
            var body = Json.Body(r);
            var target = Json.Read<string>(body, "target");
            var slash = target == null ? -1 : target.IndexOf('/');
            if (slash <= 0) throw new BadRequestException("Name the model to test as provider/model.");
            var providerId = target.Substring(0, slash);
            var provider = Config.Current.FindProvider(providerId);
            if (provider == null)
            {
                // An endpoint from a model file that is not in use yet: say why instead of "not found".
                var file = (GatewayFiles ?? new List<GatewayModelFile>()).FirstOrDefault(x => x.EndpointKeys.Contains(providerId, StringComparer.OrdinalIgnoreCase));
                if (file == null) throw new NotFoundException("No provider called " + providerId + ".");
                var missingKey = file.TokenVariables.FirstOrDefault(v => file.Errors.Any(e => e.StartsWith(v + " ", StringComparison.Ordinal) && e.EndsWith("is not set.", StringComparison.Ordinal)));
                if (missingKey != null)
                    return Json.Ok(new { ok = false, verdict = "no-key", message = "No key is available for " + providerId + ": share " + missingKey + " from Lazarette, or set that environment variable and restart Fairlead. Nothing was sent." });
                return Json.Ok(new { ok = false, verdict = "file-problem", message = "The model file " + file.Name + " has problems, so " + providerId + " is not in use: " + string.Join(" ", file.Errors) });
            }
            if (string.IsNullOrEmpty(SecretProtector.ApiKeyFor(provider, Lazarette.Value)))
                return Json.Ok(new { ok = false, verdict = "no-key", message = "No key is available for " + provider.Id + ": share " + (provider.ApiKeyEnv ?? "its key") + " from Lazarette, or set that environment variable and restart Fairlead. Nothing was sent." });
            var request = new JObject
            {
                { "model", target },
                { "messages", new JArray { new JObject { { "role", "user" }, { "content", "Reply with the single word: ok" } } } },
                { "max_tokens", 16 },
                { "stream", false },
            };
            var synthetic = new HttpRequest
            {
                Method = "POST",
                Path = "/v1/chat/completions",
                Body = System.Text.Encoding.UTF8.GetBytes(request.ToString(Formatting.None)),
                RequestId = r.RequestId,
                Internal = true,
                Aborted = r.Aborted,
            };
            var response = await Engine.HandleChatAsync(synthetic, Dialect.OpenAi).ConfigureAwait(false);
            var rec = Metrics.Find(r.RequestId);
            var last = rec == null ? null : rec.Attempts.LastOrDefault(a => a.Outcome != "skipped");
            if (response.Status < 300)
            {
                var answer = JObject.Parse(System.Text.Encoding.UTF8.GetString(response.Body));
                return Json.Ok(new { ok = true, verdict = "ok", message = "The gateway answered: the address, key and model all work.", status = 200, ms = rec == null ? (int?)null : rec.TotalMs, text = Translate.ContentText(answer["choices"].At(0).At("message").At("content")) });
            }
            var outcome = last == null ? "error" : last.Outcome;
            var status = last == null ? (int?)null : last.Status;
            string verdict, message;
            switch (outcome)
            {
                case "auth": verdict = "key-rejected"; message = "The gateway rejected the key (HTTP " + (status ?? 401) + "): it is wrong, expired or not allowed this model. The address and the request were fine."; break;
                case "modelnotfound": verdict = status == 404 ? "not-found" : "model"; message = status == 404 ? "HTTP 404: the URL or path is wrong, or the gateway does not serve this model there." : "The gateway does not know this model id."; break;
                case "badrequest": verdict = "request-rejected"; message = "The gateway refused the request (HTTP " + (status ?? 400) + "): the model id or the request format does not match what it expects."; break;
                case "network": verdict = "unreachable"; message = "The gateway could not be reached: check the address and that this PC is on the network that can see it."; break;
                case "timeout": verdict = "timeout"; message = "The gateway did not answer in time."; break;
                case "server": verdict = "server"; message = "The gateway had an error of its own (HTTP " + (status ?? 500) + ")."; break;
                case "ratelimited": verdict = "rate-limited"; message = "The gateway is rate-limiting this key (HTTP 429); the key itself works."; break;
                default: verdict = "error"; message = "The request failed."; break;
            }
            return Json.Ok(new { ok = false, verdict = verdict, message = message, status = status, detail = last == null ? (rec == null ? null : rec.Error) : last.Error });
        }

        /// <summary>Stores the gateway address (or another non-secret variable the current file names), encrypted, then reloads.</summary>
        private Task<HttpResponse> SaveGatewayValues(HttpRequest r)
        {
            var f = GatewayModels;
            if (f == null) throw new BadRequestException("Import the AI gateway model file first.");
            var values = ReadGatewayValues(Json.Body(r), f);
            if (values.Count == 0) throw new BadRequestException("Nothing to save.");
            GatewayValues.SetValues(values);
            Log.Info("GATEWAY_VALUES_SAVED", "Stored " + string.Join(", ", values.Keys) + " for the AI gateway (encrypted).");
            LoadGatewayModels();
            return Task.FromResult(Json.Ok(GatewayView()));
        }

        /// <summary>
        /// The "values" object: only variables the file names, never a token (tokens live in Lazarette or the
        /// environment); addresses must be https (http only for this machine).
        /// </summary>
        private static Dictionary<string, string> ReadGatewayValues(JToken body, GatewayModelFile f)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var values = body == null ? null : body["values"] as JObject;
            if (values == null) return result;
            foreach (var p in values.Properties())
            {
                if (!f.RequiredVariables.Contains(p.Name)) throw new BadRequestException("The model file does not use " + p.Name + ".");
                if (f.TokenVariables.Contains(p.Name)) throw new BadRequestException(p.Name + " is a token: Fairlead does not store tokens. Keep it in Lazarette (or set the environment variable).");
                if (p.Value.Type != JTokenType.String && p.Value.Type != JTokenType.Null) throw new BadRequestException(p.Name + " must be text.");
                var v = p.Value.Type == JTokenType.Null ? string.Empty : ((string)p.Value).Trim();
                if (v.Length > 8192) throw new BadRequestException(p.Name + " is too long.");
                if (v.Length > 0 && f.BaseUrlVariables.Contains(p.Name))
                {
                    Uri u;
                    var ok = Uri.TryCreate(v, UriKind.Absolute, out u) && (u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback))
                        && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment) && string.IsNullOrEmpty(u.UserInfo);
                    if (!ok) throw new BadRequestException(p.Name + " must be the gateway's https address, for example https://gateway.example, with no query or credentials.");
                }
                result[p.Name] = v;
            }
            return result;
        }

        private static KedgeMode ParseKedge(string mode)
        {
            switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "auto": return KedgeMode.Auto;
                case "on": return KedgeMode.On;
                case "off": return KedgeMode.Off;
                default: throw new BadRequestException("Kedge mode must be auto, on or off.");
            }
        }

        private static ProviderKind ParseKind(string kind)
        {
            switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "openai": return ProviderKind.OpenAi;
                case "anthropic": return ProviderKind.Anthropic;
                case "ollama": return ProviderKind.Ollama;
                case "responses": throw new BadRequestException("Responses providers come from the AI gateway model file: import it under Providers, AI gateway.");
                default: throw new BadRequestException("Provider kind must be openai, anthropic or ollama.");
            }
        }
    }
}
