using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Gateway;
using Fairlead.Core.Protocol;
using Fairlead.Core.Providers;
using Fairlead.Core.Resilience;
using Fairlead.Core.Secrets;
using Fairlead.Core.Telemetry;

namespace Fairlead.Core.Host
{
    public sealed class HostOptions
    {
        public FairleadSettings Settings { get; set; }
        /// <summary>Directory holding the built UI (index.html). When missing, the gateway still runs and / returns a notice.</summary>
        public string WebRoot { get; set; }
        public int PreferredPort { get; set; }
        /// <summary>Random per-session token the admin UI must send as X-Fairlead-Session. Generated when null.</summary>
        public string SessionToken { get; set; }
        public Dictionary<string, object> LauncherState { get; set; }
        /// <summary>Tests inject a fake upstream; null uses the real network.</summary>
        public System.Net.Http.HttpMessageHandler UpstreamHandler { get; set; }
        /// <summary>A config to use instead of fairlead.json (tests).</summary>
        public FairleadConfig Config { get; set; }
        /// <summary>The AI gateway model file; null looks up FAIRLEAD_GATEWAY_CONFIG, then config\ai_gateway_models.yaml under the root.</summary>
        public string GatewayModelPath { get; set; }
        /// <summary>The store for a model file imported in the browser; null uses the data folder (memory when Config is given).</summary>
        public GatewayStore GatewayStore { get; set; }
        /// <summary>Lazarette's data folder (its logs\arc-server-*.json); null uses %LAZARETTE_DATA% or %LOCALAPPDATA%\Lazarette.</summary>
        public string LazaretteDataDirectory { get; set; }
        /// <summary>Protection for the saved Lazarette pairing; null uses DPAPI (tests pass a stand-in).</summary>
        public Func<string, string> Protect { get; set; }
        public Func<string, string> Unprotect { get; set; }
        /// <summary>Start the health monitor (off in tests that drive health by hand).</summary>
        public bool Monitor { get; set; }
        /// <summary>Stop when the admin tab is gone (TechStack lifecycle). Off for Fairlead by default: it serves other tools.</summary>
        public bool AutoStop { get; set; }
        public TimeSpan HeartbeatTimeout { get; set; }
        public TimeSpan LeavingGrace { get; set; }

        public HostOptions()
        {
            Settings = new FairleadSettings();
            LauncherState = new Dictionary<string, object>();
            Monitor = true;
            HeartbeatTimeout = TimeSpan.FromSeconds(20);
            LeavingGrace = TimeSpan.FromSeconds(5);
        }
    }

    public sealed class HostInfo
    {
        public int Port { get; set; }
        public string Url { get; set; }
        /// <summary>Base URL tools use: http://127.0.0.1:port/v1</summary>
        public string GatewayUrl { get; set; }
        public string SessionToken { get; set; }
        /// <summary>URL with the session token, for opening the admin page once.</summary>
        public string LaunchUrl { get; set; }
        public bool PortIsRegistry { get; set; }
        public string LogFile { get; set; }
        public string RuntimeFile { get; set; }
        public string ConfigFile { get; set; }

        public HostInfo()
        {
            Url = string.Empty;
            GatewayUrl = string.Empty;
            SessionToken = string.Empty;
            LaunchUrl = string.Empty;
        }
    }

    /// <summary>
    /// Composition root: config, health monitor, gateway engine, admin API and the HTTP server. PowerShell creates one,
    /// calls <see cref="Start"/>, shows the tray icon and waits on <see cref="WaitForStop"/>.
    /// </summary>
    public sealed partial class FairleadHost : IDisposable
    {
        public const string SessionHeader = "X-Fairlead-Session";
        public const string IntentHeader = "X-Fairlead-Intent";
        public const string Version = "0.1.0";
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
        private readonly object _presenceLock = new object();
        private readonly object _gatewayLock = new object();
        private HttpServer _server;
        private string _runtimeFile;
        private DateTimeOffset? _lastHeartbeat;
        private DateTimeOffset? _leavingAt;
        private Timer _presenceTimer;

        public FairleadHost(HostOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            Options = options;
            Settings = options.Settings;
            Log = new Diagnostics(Settings.LogDirectory);
            Config = options.Config != null ? new ConfigStore(options.Config) : new ConfigStore(Settings.ConfigPath);
            GatewayValues = options.GatewayStore ?? new GatewayStore(options.Config != null ? null : Settings.DataDirectory);
            Upstream = new UpstreamClient(options.UpstreamHandler);
            Lazarette = new LazaretteLink(Settings.DataDirectory, Upstream, LazaretteNames, options.LazaretteDataDirectory == null ? null : (Func<string>)(() => options.LazaretteDataDirectory), options.Protect, options.Unprotect);
            Lazarette.HoldHours = Config.Current.LazaretteHoldHours;
            Upstream.VaultLookup = Lazarette.Value;
            LoadGatewayModels();
            Lazarette.Changed += () => { Log.Info("LAZARETTE", "Keys from Lazarette changed (" + Lazarette.State + "); reloading the AI gateway."); LoadGatewayModels(); };
            Breakers = new BreakerBoard();
            Health = new HealthMonitor(Config, Upstream, Breakers, Log);
            Metrics = new Metrics(Settings.LogDirectory);
            Engine = new GatewayEngine(Config, Upstream, Health, Breakers, Metrics, Log);
            SessionToken = string.IsNullOrEmpty(options.SessionToken) ? NewToken() : options.SessionToken;
            Admin = new Router();
            Gateway = new Router();
            StartedAt = DateTimeOffset.UtcNow;
            Config.Changed += c => Lazarette.HoldHours = c.LazaretteHoldHours;
            Config.Changed += c => Log.Info("CONFIG_SAVED", "fairlead.json saved (" + c.Providers.Count + " providers, " + c.Routes.Count + " routes).");
            RegisterGatewayRoutes();
            RegisterAdminRoutes();
        }

        public HostOptions Options { get; private set; }
        public FairleadSettings Settings { get; private set; }
        public Diagnostics Log { get; private set; }
        public ConfigStore Config { get; private set; }
        /// <summary>The AI gateway model file as last loaded; null when there is none.</summary>
        public GatewayModelFile GatewayModels { get; private set; }
        /// <summary>Where it came from: "imported" (the browser), "environment" (FAIRLEAD_GATEWAY_CONFIG) or "file" (config\ folder).</summary>
        public string GatewaySource { get; private set; }
        /// <summary>The model file and values imported in the browser, encrypted in the data folder.</summary>
        public GatewayStore GatewayValues { get; private set; }
        /// <summary>Keys held in Lazarette, the suite's vault, asked for by variable name and kept in memory only.</summary>
        public LazaretteLink Lazarette { get; private set; }

        /// <summary>
        /// What to ask Lazarette for: every variable the gateway model file names that is not saved in Fairlead (its
        /// tokens above all), and the key variable of every provider without a key saved in Fairlead.
        /// </summary>
        private IList<string> LazaretteNames()
        {
            var names = new List<string>();
            var f = GatewayModels;
            if (f != null)
                foreach (var v in f.RequiredVariables)
                    if (!GatewayValues.HasValue(v)) names.Add(v);
            foreach (var p in Config.Current.Providers)
                if (!string.IsNullOrEmpty(p.ApiKeyEnv) && string.IsNullOrEmpty(p.ApiKeyProtected) && !names.Contains(p.ApiKeyEnv)) names.Add(p.ApiKeyEnv);
            return names;
        }
        public BreakerBoard Breakers { get; private set; }
        public UpstreamClient Upstream { get; private set; }
        public HealthMonitor Health { get; private set; }
        public Metrics Metrics { get; private set; }
        public GatewayEngine Engine { get; private set; }
        public Router Admin { get; private set; }
        public Router Gateway { get; private set; }
        public string SessionToken { get; private set; }
        public DateTimeOffset StartedAt { get; private set; }
        public int Port { get { return _server == null ? 0 : _server.Port; } }
        public bool StopRequested { get; private set; }
        public string BaseUrl { get { return "http://127.0.0.1:" + Port; } }

        /// <summary>
        /// Reads the AI gateway model file and adds its endpoints as managed providers, replacing the previous set. The
        /// file is FAIRLEAD_GATEWAY_CONFIG when set, else the one imported in the browser (encrypted in the data folder),
        /// else config\ai_gateway_models.yaml. Variables come from the values stored with the import, then the
        /// environment. A file with errors adds nothing; Fairlead keeps running, and the console, log and Providers page
        /// say why. Called at start and after every import or value change.
        /// </summary>
        public void LoadGatewayModels()
        {
            lock (_gatewayLock)
            {
                // Saved in Fairlead (the address), then Lazarette (tokens), then the process environment.
                var env = GatewayValues.Environment(name => Lazarette.Value(name) ?? Environment.GetEnvironmentVariable(name));
                GatewayModelFile file = null;
                var source = "file";
                var path = Options.GatewayModelPath;
                if (path == null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GatewayModelFile.PathVariable)) && GatewayValues.HasFile)
                {
                    source = "imported";
                    var text = GatewayValues.ReadFile();
                    if (text == null)
                    {
                        file = new GatewayModelFile { Path = GatewayValues.Location };
                        file.Errors.Add("The imported model file cannot be decrypted by this Windows account. Import it again.");
                    }
                    else file = GatewayModelFile.Parse(text, GatewayValues.Location, env);
                }
                else
                {
                    if (path == null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GatewayModelFile.PathVariable))) source = "environment";
                    if (path == null) path = GatewayModelFile.ResolvePath(Settings.RootDirectory, null);
                    if (path != null) file = GatewayModelFile.Load(path, env);
                }

                var providers = new List<ProviderConfig>();
                if (file != null && file.Ok)
                {
                    // Tokens are not copied into the providers: UpstreamClient asks Lazarette or the environment per request.
                    providers.AddRange(file.Providers);
                }
                try { Config.SetManaged(providers); }
                catch (ArgumentException ex)
                {
                    file.Errors.Add(ex.Message);
                    Config.SetManaged(new ProviderConfig[0]);
                }
                GatewayModels = file;
                GatewaySource = file == null ? null : source;
                if (file == null) return;
                foreach (var line in file.Summary()) Log.Info(file.Ok ? "GATEWAY_MODELS" : "GATEWAY_MODELS_INVALID", line.Trim());
            }
        }

        public HostInfo Start()
        {
            if (_server != null) throw new InvalidOperationException("Host already started.");
            _server = new HttpServer(HandleAsync, Log);
            var preferred = Options.PreferredPort > 0 ? Options.PreferredPort : Settings.PreferredPort;
            var port = _server.Start(preferred);
            var info = new HostInfo
            {
                Port = port,
                Url = "http://127.0.0.1:" + port + "/",
                GatewayUrl = "http://127.0.0.1:" + port + "/v1",
                SessionToken = SessionToken,
                LaunchUrl = "http://127.0.0.1:" + port + "/?session=" + SessionToken,
                PortIsRegistry = port == preferred,
                LogFile = Log.FilePath,
                ConfigFile = Config.FilePath,
            };
            WriteRuntimeFile(info);
            info.RuntimeFile = _runtimeFile;
            if (Options.Monitor) Health.Start();
            if (Options.Monitor) Lazarette.Start();
            if (Options.AutoStop) _presenceTimer = new Timer(PresenceTick, null, 2000, 2000);
            Log.Info("HOST_READY", "Fairlead " + Version + " gateway " + info.GatewayUrl + ", config " + (Config.FilePath ?? "in memory"));
            return info;
        }

        public void Stop()
        {
            if (StopRequested) return;
            StopRequested = true;
            Log.Info("HOST_STOP", "Stop requested.");
            try { if (_presenceTimer != null) _presenceTimer.Dispose(); } catch (ObjectDisposedException) { }
            try { if (_server != null) _server.Stop(); } finally { _stopped.Set(); }
            if (_runtimeFile != null)
            {
                try { File.Delete(_runtimeFile); } catch (IOException) { }
            }
        }

        /// <summary>Blocks the calling (PowerShell) thread until End session, the tray's Exit, or Stop().</summary>
        public bool WaitForStop(int timeoutMilliseconds = -1)
        {
            if (timeoutMilliseconds < 0) { _stopped.Wait(); return true; }
            return _stopped.Wait(timeoutMilliseconds);
        }

        public void Dispose()
        {
            Stop();
            Health.Dispose();
            Lazarette.Dispose();
            if (_server != null) _server.Dispose();
            _stopped.Dispose();
        }

        // ---- presence (only when the tab owns the engine: FAIRLEAD_AUTO_STOP=1) ----

        public void RecordPresence(bool leaving)
        {
            lock (_presenceLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (leaving) _leavingAt = now;
                else { _lastHeartbeat = now; _leavingAt = null; }
            }
        }

        public bool BrowserGone(DateTimeOffset now)
        {
            if (!Options.AutoStop) return false;
            lock (_presenceLock)
            {
                if (_lastHeartbeat == null) return false;
                if (_leavingAt != null && now - _leavingAt.Value >= Options.LeavingGrace) return true;
                return now - _lastHeartbeat.Value >= Options.HeartbeatTimeout;
            }
        }

        private void PresenceTick(object state)
        {
            if (StopRequested || !BrowserGone(DateTimeOffset.UtcNow)) return;
            Log.Info("HOST_AUTO_STOP", "No browser heartbeat; the engine stops itself (FAIRLEAD_AUTO_STOP=1 is set).");
            Stop();
        }

        private void WriteRuntimeFile(HostInfo info)
        {
            try
            {
                Directory.CreateDirectory(Settings.LogDirectory);
                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                _runtimeFile = Path.Combine(Settings.LogDirectory, "fairlead-server-" + pid + ".json");
                // The session token is deliberately excluded: anything on the machine can read this file.
                File.WriteAllText(_runtimeFile, Json.Serialize(new { pid = pid, port = info.Port, url = info.Url, gateway = info.GatewayUrl, started = StartedAt, version = Version }));
            }
            catch (IOException) { _runtimeFile = null; }
            catch (UnauthorizedAccessException) { _runtimeFile = null; }
        }

        private static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        // ---- request pipeline ----

        private async Task<HttpResponse> HandleAsync(HttpRequest request)
        {
            if (request.Path == "/v1" || request.Path.StartsWith("/v1/", StringComparison.Ordinal)) return await HandleGatewayAsync(request).ConfigureAwait(false);

            // Everything else (admin API, UI) is for this machine's browser only.
            if (!request.IsLoopback) return Json.Error(403, "LOOPBACK_ONLY", "Fairlead's admin pages are only served to this machine.", request.RequestId);
            var host = request.Header("Host") ?? string.Empty;
            var hostName = host.StartsWith("[") ? host.Substring(0, host.IndexOf(']') + 1) : host.Split(':')[0];
            if (hostName != "127.0.0.1" && hostName != "localhost" && hostName != "[::1]")
                return Json.Error(403, "BAD_HOST", "Loopback host names only.", request.RequestId);

            // FleetPort id endpoint (edge-ops PORTS.md): launchers and Lazarette's requester check use it.
            if (request.Path == "/__fleethacker/id")
            {
                var idOrigin = request.Header("Origin");
                if (idOrigin != null && !IsOwnOrigin(idOrigin)) return Json.Error(403, "BAD_ORIGIN", "Cross-origin requests are not accepted.", request.RequestId);
                if (request.Method != "GET") return Json.Error(405, "METHOD_NOT_ALLOWED", "Method not allowed.", request.RequestId);
                return Json.Ok(new { tool = "fairlead", version = Version, pid = System.Diagnostics.Process.GetCurrentProcess().Id });
            }

            if (request.Path.StartsWith("/api/", StringComparison.Ordinal) || request.Path == "/api")
            {
                if (request.Path != "/api/health" && !TokenMatches(request) && !BeaconTokenMatches(request))
                    return Json.Error(401, "SESSION_REQUIRED", "Missing or invalid X-Fairlead-Session token. Open Fairlead from the launcher or the tray icon.", request.RequestId);
                var origin = request.Header("Origin");
                if (origin != null && !IsOwnOrigin(origin))
                    return Json.Error(403, "BAD_ORIGIN", "Cross-origin requests are not accepted.", request.RequestId);
                bool mismatch;
                var handler = Admin.Resolve(request, out mismatch);
                if (handler == null) return Json.Error(mismatch ? 405 : 404, mismatch ? "METHOD_NOT_ALLOWED" : "NOT_FOUND", mismatch ? "Method not allowed." : "No such API route.", request.RequestId);
                try
                {
                    return await handler(request).ConfigureAwait(false);
                }
                catch (BadRequestException ex) { return Json.Error(400, "BAD_REQUEST", ex.Message, request.RequestId); }
                catch (ArgumentException ex) { return Json.Error(400, "INVALID", ex.Message, request.RequestId); }
                catch (NotFoundException ex) { return Json.Error(404, "NOT_FOUND", ex.Message, request.RequestId); }
                catch (UpstreamException ex) { return Json.Error(502, "UPSTREAM_" + ex.Kind.ToString().ToUpperInvariant(), ex.Message, request.RequestId); }
                catch (InvalidOperationException ex) { return Json.Error(409, "CONFLICT", ex.Message, request.RequestId); }
            }

            var webRoot = Options.WebRoot ?? Settings.WebRootDirectory;
            if (!string.IsNullOrEmpty(webRoot))
            {
                var served = new StaticFiles(webRoot).Serve(request);
                if (served != null) return served;
            }
            if (request.Path == "/" || request.Path == "/index.html")
                return HttpResponse.Text(200, "Fairlead is running. The gateway is at " + BaseUrl + "/v1, but no built admin UI was found" + (string.IsNullOrEmpty(webRoot) ? "." : " at " + webRoot + ".") + " Build it with build/Build-Fairlead.ps1 and relaunch.", "text/plain; charset=utf-8");
            return HttpResponse.Text(404, "Not found");
        }

        private async Task<HttpResponse> HandleGatewayAsync(HttpRequest request)
        {
            var origin = request.Header("Origin");
            var cors = origin != null && Config.Current.CorsOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            // A web page must not spend the operator's credits: browsers are refused unless their origin is listed.
            if (origin != null && !cors && !IsOwnOrigin(origin))
                return Json.Error(403, "BAD_ORIGIN", "Browser origin " + origin + " is not allowed. Add it under Fairlead > Settings > Browser origins.", request.RequestId);
            if (request.Method == "OPTIONS")
            {
                var pre = new HttpResponse { Status = 204 };
                if (cors) AddCors(pre, origin);
                return pre;
            }
            bool mismatch;
            var handler = Gateway.Resolve(request, out mismatch);
            HttpResponse response;
            if (handler == null)
            {
                response = Json.Error(mismatch ? 405 : 404, mismatch ? "METHOD_NOT_ALLOWED" : "NOT_FOUND", mismatch ? "Method not allowed." : "Fairlead serves /v1/chat/completions, /v1/messages, /v1/embeddings and /v1/models.", request.RequestId);
            }
            else
            {
                response = await handler(request).ConfigureAwait(false);
            }
            if (cors) AddCors(response, origin);
            return response;
        }

        private static void AddCors(HttpResponse r, string origin)
        {
            r.Headers["Access-Control-Allow-Origin"] = origin;
            r.Headers["Vary"] = "Origin";
            r.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            r.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, x-api-key, anthropic-version, anthropic-beta, X-Fairlead-Route, X-Fairlead-Privacy";
            r.Headers["Access-Control-Expose-Headers"] = "X-Fairlead-Route, X-Fairlead-Target, X-Fairlead-Mode, X-Fairlead-Attempts, X-Fairlead-Request-Id, X-Fairlead-Private";
            r.Headers["Access-Control-Max-Age"] = "600";
        }

        private bool BeaconTokenMatches(HttpRequest request)
        {
            if (request.Path != "/api/heartbeat" || request.Method != "POST") return false;
            if (request.Body.Length == 0 || request.Body.Length > 512) return false;
            try
            {
                var token = Json.Read<string>(Json.Body(request), "session");
                return !string.IsNullOrEmpty(token) && FixedTimeEquals(token, SessionToken);
            }
            catch (BadRequestException) { return false; }
        }

        private bool TokenMatches(HttpRequest request)
        {
            var token = request.Header(SessionHeader);
            return !string.IsNullOrEmpty(token) && FixedTimeEquals(token, SessionToken);
        }

        private bool IsOwnOrigin(string origin)
        {
            return string.Equals(origin, "http://127.0.0.1:" + Port, StringComparison.OrdinalIgnoreCase) || string.Equals(origin, "http://localhost:" + Port, StringComparison.OrdinalIgnoreCase);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
