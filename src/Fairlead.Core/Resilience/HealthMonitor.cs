using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Host;
using Fairlead.Core.Providers;

namespace Fairlead.Core.Resilience
{
    public enum InternetState { Unknown, Up, Down }

    /// <summary>Where traffic goes right now: cloud and local, or local only (Kedge mode).</summary>
    public sealed class ConnectivitySnapshot
    {
        private InternetState _internet = InternetState.Unknown;
        public InternetState Internet { get { return _internet; } set { _internet = value; } }
        /// <summary>Local models only. True when forced on, or when the internet is down in auto mode.</summary>
        public bool Kedge { get; set; }
        /// <summary>"forced", "offline" or null.</summary>
        public string KedgeReason { get; set; }
        public KedgeMode Mode { get; set; }
        public DateTimeOffset? Since { get; set; }
        public DateTimeOffset? LastProbe { get; set; }
        /// <summary>Probe targets that answered in the last probe.</summary>
        private List<string> _reached = new List<string>();
        public List<string> Reached { get { return _reached; } set { _reached = value; } }
        private bool _osReportsNetwork = true;
        public bool OsReportsNetwork { get { return _osReportsNetwork; } set { _osReportsNetwork = value; } }
    }

    public sealed class ProviderHealth
    {
        private string _id = string.Empty;
        public string Id { get { return _id; } set { _id = value; } }
        public bool Local { get; set; }
        /// <summary>The last probe reached the endpoint (TCP for cloud, the model list for local).</summary>
        public bool? Reachable { get; set; }
        /// <summary>The key was accepted by the last deep check; null until one has run.</summary>
        public bool? AuthOk { get; set; }
        public int? ProbeMs { get; set; }
        public string LastError { get; set; }
        public DateTimeOffset? LastChecked { get; set; }
        public DateTimeOffset? LastDeepCheck { get; set; }
        /// <summary>Models found by discovery (Ollama tags, /models); merged with the configured list when routing.</summary>
        private List<ModelInfo> _models = new List<ModelInfo>();
        public List<ModelInfo> Models { get { return _models; } set { _models = value; } }
        /// <summary>Models a local server has in memory right now (Ollama /api/ps); preferred when Fairlead picks one itself.</summary>
        private List<string> _loaded = new List<string>();
        public List<string> Loaded { get { return _loaded; } set { _loaded = value; } }
    }

    /// <summary>
    /// Watches the network and every provider so routing never waits on a dead endpoint:
    /// <list type="bullet">
    /// <item>Windows network-change events trigger an immediate probe (cable out, Wi-Fi gone, VPN up).</item>
    /// <item>Every few seconds, plain TCP connects to the probe hosts and the cloud endpoints decide whether the internet is up,
    /// with hysteresis so one dropped packet does not flip the mode.</item>
    /// <item>Local servers are asked for their model list each round, which also discovers newly pulled models.</item>
    /// <item>Cloud keys are verified with a real (free) model-list call at start and every few minutes.</item>
    /// <item>The gateway nudges the monitor when a real call fails at the network level, so Kedge mode starts within one probe.</item>
    /// </list>
    /// </summary>
    public sealed class HealthMonitor : IDisposable
    {
        private readonly ConfigStore _config;
        private readonly UpstreamClient _upstream;
        private readonly BreakerBoard _breakers;
        private readonly Diagnostics _log;
        private readonly ConcurrentDictionary<string, ProviderHealth> _providers = new ConcurrentDictionary<string, ProviderHealth>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private readonly object _stateLock = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private ConnectivitySnapshot _state = new ConnectivitySnapshot();
        private int _failStreak;
        private int _okStreak;
        private Task _loop;
        private bool _deepPending = true;
        private DateTimeOffset _lastPassiveSuccess = DateTimeOffset.MinValue;

        public HealthMonitor(ConfigStore config, UpstreamClient upstream, BreakerBoard breakers, Diagnostics log)
        {
            _config = config;
            _upstream = upstream;
            _breakers = breakers;
            _log = log;
            _state.Mode = config.Current.Kedge;
            ApplyMode(_state, config.Current.Kedge);
            _config.Changed += c =>
            {
                lock (_stateLock)
                {
                    var next = Copy(_state);
                    ApplyMode(next, c.Kedge);
                    Publish(next);
                }
                _deepPending = true;
                Nudge();
            };
        }

        /// <summary>Raised when the internet state or Kedge mode changes.</summary>
        public event Action<ConnectivitySnapshot> Changed;

        /// <summary>Set by tests to drive the internet state without sockets.</summary>
        internal Func<CancellationToken, Task<List<string>>> InternetProbeOverride { get; set; }

        public ConnectivitySnapshot Connectivity
        {
            get { lock (_stateLock) return Copy(_state); }
        }

        public bool KedgeActive
        {
            get { lock (_stateLock) return _state.Kedge; }
        }

        public ProviderHealth Provider(string id)
        {
            ProviderHealth h;
            return _providers.TryGetValue(id, out h) ? h : null;
        }

        public IReadOnlyList<ProviderHealth> Providers()
        {
            return _providers.Values.OrderBy(p => p.Id).ToList();
        }

        public void Start()
        {
            if (_loop != null) return;
            try { NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability; } catch (Exception ex) { if (!(ex is PlatformNotSupportedException || ex is NotImplementedException)) throw; }
            try { NetworkChange.NetworkAddressChanged += OnNetworkAddress; } catch (Exception ex) { if (!(ex is PlatformNotSupportedException || ex is NotImplementedException)) throw; }
            _loop = Task.Run(() => Loop(_cts.Token));
        }

        /// <summary>Probe now instead of at the next interval.</summary>
        public void Nudge()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
        }

        /// <summary>A real call to a cloud provider succeeded: the internet is up, whatever the probes said.</summary>
        public void ReportCloudSuccess()
        {
            _lastPassiveSuccess = DateTimeOffset.UtcNow;
            bool wasDown;
            lock (_stateLock) wasDown = _state.Internet != InternetState.Up;
            if (wasDown) Nudge();
        }

        private void OnNetworkAvailability(object sender, NetworkAvailabilityEventArgs e)
        {
            _log.Info("NETWORK_CHANGE", e.IsAvailable ? "Windows reports a network is available." : "Windows reports no network.");
            lock (_stateLock)
            {
                if (!e.IsAvailable)
                {
                    // No adapter at all: there is nothing to wait for. Go local immediately.
                    _failStreak = Math.Max(_failStreak, _config.Current.Network.OfflineAfterFailures);
                    var next = Copy(_state);
                    next.OsReportsNetwork = false;
                    SetInternet(next, InternetState.Down);
                    Publish(next);
                }
            }
            Nudge();
        }

        private void OnNetworkAddress(object sender, EventArgs e)
        {
            Nudge();
        }

        private async Task Loop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await ProbeOnceAsync(token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException && token.IsCancellationRequested) break;
                    _log.Warn("PROBE_FAILED", ex.GetType().Name + ": " + ex.Message);
                }
                var interval = TimeSpan.FromSeconds(_config.Current.Network.ProbeIntervalSeconds);
                // Probe faster while the internet is down, so the return to cloud is quick too.
                if (Connectivity.Internet == InternetState.Down) interval = TimeSpan.FromSeconds(Math.Max(3, interval.TotalSeconds / 3));
                try { await _wake.WaitAsync(interval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        /// <summary>One full round: internet, local servers, cloud endpoints. Public so the UI's "check now" can await it.</summary>
        public async Task ProbeOnceAsync(CancellationToken token)
        {
            var cfg = _config.Current;
            var net = cfg.Network;
            var reached = InternetProbeOverride != null ? await InternetProbeOverride(token).ConfigureAwait(false) : await ProbeInternetAsync(cfg, token).ConfigureAwait(false);
            var passive = DateTimeOffset.UtcNow - _lastPassiveSuccess < TimeSpan.FromSeconds(net.ProbeIntervalSeconds * 2);
            var up = reached.Count > 0 || passive;
            var deep = _deepPending;
            _deepPending = false;
            var tasks = new List<Task>();
            foreach (var p in cfg.Providers.Where(x => x.Enabled))
            {
                tasks.Add(ProbeProviderAsync(p, up, deep, net, token));
            }
            foreach (var stale in _providers.Keys.Where(k => cfg.FindProvider(k) == null).ToList())
            {
                ProviderHealth removed;
                _providers.TryRemove(stale, out removed);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            lock (_stateLock)
            {
                var next = Copy(_state);
                next.LastProbe = DateTimeOffset.UtcNow;
                next.Reached = reached;
                next.OsReportsNetwork = SafeNetworkAvailable();
                if (up) { _okStreak++; _failStreak = 0; } else { _failStreak++; _okStreak = 0; }
                if (next.Internet != InternetState.Up && up && (_okStreak >= net.OnlineAfterSuccesses || next.Internet == InternetState.Unknown || passive)) SetInternet(next, InternetState.Up);
                else if (next.Internet != InternetState.Down && !up && (_failStreak >= net.OfflineAfterFailures || !next.OsReportsNetwork)) SetInternet(next, InternetState.Down);
                ApplyMode(next, cfg.Kedge);
                Publish(next);
            }
        }

        private async Task<List<string>> ProbeInternetAsync(FairleadConfig cfg, CancellationToken token)
        {
            var targets = new List<KeyValuePair<string, int>>();
            foreach (var h in cfg.Network.ProbeHosts)
            {
                var colon = h.LastIndexOf(':');
                int port;
                if (colon > 0 && int.TryParse(h.Substring(colon + 1), out port)) targets.Add(new KeyValuePair<string, int>(h.Substring(0, colon), port));
            }
            // The cloud endpoints themselves count: some networks block public DNS resolvers but allow the APIs.
            foreach (var p in cfg.Providers.Where(x => x.Enabled && !x.Local))
            {
                Uri u;
                if (Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out u)) targets.Add(new KeyValuePair<string, int>(u.Host, u.Port));
            }
            var distinct = targets.GroupBy(t => t.Key + ":" + t.Value).Select(g => g.First()).ToList();
            var results = await Task.WhenAll(distinct.Select(async t =>
                (await UpstreamClient.TcpReachableAsync(t.Key, t.Value, cfg.Network.ProbeTimeoutMs, token).ConfigureAwait(false)) ? t.Key + ":" + t.Value : null)).ConfigureAwait(false);
            return results.Where(r => r != null).Select(r => r).ToList();
        }

        private async Task ProbeProviderAsync(ProviderConfig p, bool internetUp, bool deep, NetworkConfig net, CancellationToken token)
        {
            var h = _providers.GetOrAdd(p.Id, id => new ProviderHealth { Id = id });
            h.Local = p.Local;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var dueDeep = deep || h.LastDeepCheck == null || DateTimeOffset.UtcNow - h.LastDeepCheck.Value > TimeSpan.FromMinutes(Math.Max(1, net.DeepCheckMinutes));
            try
            {
                if (p.Local || (dueDeep && internetUp))
                {
                    var models = await _upstream.ListModelsAsync(p, token, p.Local ? 4000 : 8000).ConfigureAwait(false);
                    h.Models = models;
                    if (p.Kind == ProviderKind.Ollama) h.Loaded = await _upstream.ListLoadedAsync(p, token).ConfigureAwait(false);
                    h.Reachable = true;
                    h.AuthOk = true;
                    h.LastError = null;
                    h.LastDeepCheck = DateTimeOffset.UtcNow;
                    var b = _breakers.Find(p.Id);
                    if (b != null && b.State(DateTimeOffset.UtcNow) != BreakerState.Closed && b.LastFailure != FailureKind.RateLimited) b.Reset();
                }
                else if (!p.Local)
                {
                    Uri u;
                    var ok = Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out u) && await UpstreamClient.TcpReachableAsync(u.Host, u.Port, net.ProbeTimeoutMs, token).ConfigureAwait(false);
                    h.Reachable = ok;
                    h.LastError = ok ? h.LastError : "Not reachable over TCP.";
                }
            }
            catch (UpstreamException ex)
            {
                h.Reachable = ex.Kind != FailureKind.Network && ex.Kind != FailureKind.Timeout;
                if (ex.Kind == FailureKind.Auth) { h.AuthOk = false; _breakers.For(p.Id).Failure(FailureKind.Auth, ex.Message); }
                h.LastError = ex.Message;
                h.LastDeepCheck = DateTimeOffset.UtcNow;
                if (p.Local && ex.Kind == FailureKind.Network) h.Models = new List<ModelInfo>();
            }
            h.ProbeMs = (int)sw.ElapsedMilliseconds;
            h.LastChecked = DateTimeOffset.UtcNow;
        }

        private static bool SafeNetworkAvailable()
        {
            try { return NetworkInterface.GetIsNetworkAvailable(); }
            catch (NetworkInformationException) { return true; }
            catch (PlatformNotSupportedException) { return true; }
        }

        private void SetInternet(ConnectivitySnapshot s, InternetState state)
        {
            if (s.Internet == state) return;
            _log.Info(state == InternetState.Up ? "INTERNET_UP" : "INTERNET_DOWN", state == InternetState.Up ? "Internet reachable." : "Internet unreachable.");
            s.Internet = state;
        }

        private static void ApplyMode(ConnectivitySnapshot s, KedgeMode mode)
        {
            s.Mode = mode;
            if (mode == KedgeMode.On) { s.Kedge = true; s.KedgeReason = "forced"; }
            else if (mode == KedgeMode.Auto && s.Internet == InternetState.Down) { s.Kedge = true; s.KedgeReason = "offline"; }
            else { s.Kedge = false; s.KedgeReason = null; }
        }

        private void Publish(ConnectivitySnapshot next)
        {
            var changed = next.Kedge != _state.Kedge || next.Internet != _state.Internet || next.Mode != _state.Mode;
            if (changed) next.Since = DateTimeOffset.UtcNow;
            if (changed && next.Kedge != _state.Kedge)
                _log.Info(next.Kedge ? "KEDGE_ON" : "KEDGE_OFF", next.Kedge ? "Kedge mode: local models only (" + next.KedgeReason + ")." : "Kedge mode off: cloud routes are back.");
            _state = next;
            if (changed)
            {
                var handler = Changed;
                if (handler != null)
                {
                    var copy = Copy(next);
                    Task.Run(() => handler(copy));
                }
            }
        }

        private static ConnectivitySnapshot Copy(ConnectivitySnapshot s)
        {
            return new ConnectivitySnapshot
            {
                Internet = s.Internet,
                Kedge = s.Kedge,
                KedgeReason = s.KedgeReason,
                Mode = s.Mode,
                Since = s.Since,
                LastProbe = s.LastProbe,
                Reached = new List<string>(s.Reached),
                OsReportsNetwork = s.OsReportsNetwork,
            };
        }

        public void Dispose()
        {
            try { NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability; } catch (Exception) { }
            try { NetworkChange.NetworkAddressChanged -= OnNetworkAddress; } catch (Exception) { }
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { if (_loop != null) _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _cts.Dispose();
            _wake.Dispose();
        }
    }
}
