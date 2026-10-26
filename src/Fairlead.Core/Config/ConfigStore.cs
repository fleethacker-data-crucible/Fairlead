using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Config
{
    /// <summary>
    /// Owns fairlead.json. Readers get an immutable snapshot (a deep copy); writers go through <see cref="Update"/>,
    /// which applies the change to a copy, validates it, saves atomically and only then publishes it. Managed providers
    /// (from the AI gateway model file) are part of every snapshot but never of fairlead.json.
    /// </summary>
    public sealed class ConfigStore
    {
        private static readonly JsonSettings FileJson = JsonSettings.File;

        private readonly object _lock = new object();
        private readonly string _path;
        private FairleadConfig _current;
        private FairleadConfig _view;
        private System.Collections.Generic.List<ProviderConfig> _managed = new System.Collections.Generic.List<ProviderConfig>();
        private long _revision;

        /// <summary>A store backed by <paramref name="path"/>; null keeps the config in memory only (tests).</summary>
        public ConfigStore(string path)
        {
            _path = path;
            _current = Load(path) ?? FairleadConfig.CreateDefault();
            Validate(_current);
            _view = _current;
        }

        public ConfigStore(FairleadConfig config)
        {
            _path = null;
            _current = Clone(config);
            Validate(_current);
            _view = _current;
        }

        public string FilePath { get { return _path; } }
        public long Revision { get { lock (_lock) return _revision; } }

        /// <summary>Raised after a change is saved, with the new snapshot.</summary>
        public event Action<FairleadConfig> Changed;

        /// <summary>A private copy; changing it does nothing.</summary>
        public FairleadConfig Snapshot()
        {
            lock (_lock) return Clone(_view);
        }

        /// <summary>The live object, for hot paths that only read. Never mutate it.</summary>
        internal FairleadConfig Current
        {
            get { lock (_lock) return _view; }
        }

        /// <summary>
        /// Applies <paramref name="change"/> to a copy of the saved config (managed providers are not in it), validates the
        /// result together with the managed providers, saves it and publishes it.
        /// </summary>
        public FairleadConfig Update(Action<FairleadConfig> change)
        {
            FairleadConfig view;
            lock (_lock)
            {
                var next = Clone(_current);
                change(next);
                Validate(next);
                view = Merge(next, _managed);
                Validate(view);
                Save(next);
                _current = next;
                _view = view;
                _revision++;
            }
            var handler = Changed;
            if (handler != null) handler(Clone(view));
            return Clone(view);
        }

        /// <summary>
        /// Replaces the managed providers (the AI gateway model file). Throws ArgumentException when one clashes with a
        /// saved provider or is invalid; the previous set stays in place then.
        /// </summary>
        public void SetManaged(System.Collections.Generic.IEnumerable<ProviderConfig> providers)
        {
            var list = new System.Collections.Generic.List<ProviderConfig>();
            foreach (var p in providers ?? new ProviderConfig[0])
            {
                var copy = CloneProvider(p);
                copy.Managed = true;
                list.Add(copy);
            }
            lock (_lock)
            {
                foreach (var p in list)
                    if (_current.FindProvider(p.Id) != null) throw new ArgumentException("A provider called '" + p.Id + "' is already set up in Fairlead; rename it or the gateway endpoint.");
                var view = Merge(_current, list);
                Validate(view);
                _managed = list;
                _view = view;
                _revision++;
            }
        }

        public bool IsManaged(string providerId)
        {
            lock (_lock) return _managed.Exists(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        }

        private static FairleadConfig Merge(FairleadConfig saved, System.Collections.Generic.List<ProviderConfig> managed)
        {
            if (managed.Count == 0) return saved;
            var view = Clone(saved);
            foreach (var p in managed) view.Providers.Add(CloneProvider(p));
            return view;
        }

        private static ProviderConfig CloneProvider(ProviderConfig p)
        {
            return JsonConvert.DeserializeObject<ProviderConfig>(JsonConvert.SerializeObject(p, Formatting.None, FileJson), FileJson);
        }

        private void Save(FairleadConfig config)
        {
            if (_path == null) return;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(config, Formatting.Indented, FileJson), new UTF8Encoding(false));
            if (File.Exists(_path))
            {
                File.Replace(tmp, _path, _path + ".bak", true);
            }
            else
            {
                File.Move(tmp, _path);
            }
        }

        private static FairleadConfig Load(string path)
        {
            if (path == null || !File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return JsonConvert.DeserializeObject<FairleadConfig>(text, FileJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("fairlead.json is not valid (" + ex.Message + "). Fix it, or rename it to start over; a backup of the previous version is fairlead.json.bak.", ex);
            }
        }

        public static FairleadConfig Clone(FairleadConfig config)
        {
            return JsonConvert.DeserializeObject<FairleadConfig>(JsonConvert.SerializeObject(config, Formatting.None, FileJson), FileJson);
        }

        public static string Serialize(FairleadConfig config)
        {
            return JsonConvert.SerializeObject(config, Formatting.Indented, FileJson);
        }

        /// <summary>Throws ArgumentException with a message the browser can show.</summary>
        public static void Validate(FairleadConfig c)
        {
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in c.Providers)
            {
                if (string.IsNullOrWhiteSpace(p.Id) || !IsSlug(p.Id)) throw new ArgumentException("Provider id '" + p.Id + "' must be letters, digits, dot, dash or underscore.");
                if (!ids.Add(p.Id)) throw new ArgumentException("Two providers are called '" + p.Id + "'.");
                Uri uri;
                if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    throw new ArgumentException("Provider '" + p.Id + "' needs an http or https base URL.");
                if (p.TimeoutSeconds < 5 || p.TimeoutSeconds > 3600) throw new ArgumentException("Provider '" + p.Id + "': timeout must be 5 to 3600 seconds.");
                if (p.Kind == ProviderKind.Responses && (string.IsNullOrEmpty(p.Path) || p.Path[0] != '/')) throw new ArgumentException("Provider '" + p.Id + "': a Responses endpoint needs a path starting with /.");
                if (p.Retries.HasValue && (p.Retries.Value < 0 || p.Retries.Value > 5)) throw new ArgumentException("Provider '" + p.Id + "': retries must be 0 to 5.");
            }
            var routes = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in c.Routes)
            {
                if (string.IsNullOrWhiteSpace(r.Id) || !IsSlug(r.Id)) throw new ArgumentException("Route id '" + r.Id + "' must be letters, digits, dot, dash or underscore.");
                if (!routes.Add(r.Id)) throw new ArgumentException("Two routes are called '" + r.Id + "'.");
                if (r.Id.IndexOf('/') >= 0) throw new ArgumentException("Route ids cannot contain '/'; that separates provider and model.");
                foreach (var t in r.Targets)
                {
                    var slash = t.IndexOf('/');
                    if (slash <= 0 || slash == t.Length - 1) throw new ArgumentException("Route '" + r.Id + "': target '" + t + "' must be provider/model.");
                }
                if (r.HedgeAfterMs < 0 || r.HedgeAfterMs > 120000) throw new ArgumentException("Route '" + r.Id + "': hedge delay must be 0 to 120000 ms.");
            }
            foreach (var rule in c.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Route) || !routes.Contains(rule.Route)) throw new ArgumentException("Rule '" + rule.Name + "' points at unknown route '" + rule.Route + "'.");
                var auto = c.FindRoute(rule.Route);
                if (auto != null && auto.Strategy == RouteStrategy.Auto) throw new ArgumentException("Rule '" + rule.Name + "' cannot point at an auto route.");
            }
            foreach (var kv in c.Aliases)
            {
                if (!routes.Contains(kv.Value)) throw new ArgumentException("Alias '" + kv.Key + "' points at unknown route '" + kv.Value + "'.");
            }
            if (!routes.Contains(c.DefaultRoute)) throw new ArgumentException("Default route '" + c.DefaultRoute + "' does not exist.");
            if (!routes.Contains(c.AutoFallbackRoute)) throw new ArgumentException("Auto fallback route '" + c.AutoFallbackRoute + "' does not exist.");
            var fb = c.FindRoute(c.AutoFallbackRoute);
            if (fb != null && fb.Strategy == RouteStrategy.Auto) throw new ArgumentException("The auto fallback route cannot itself be an auto route.");
            if (double.IsNaN(c.LazaretteHoldHours) || c.LazaretteHoldHours < 0 || c.LazaretteHoldHours > 24) throw new ArgumentException("Hours to keep keys from Lazarette must be 0 to 24.");
            if (c.Network.ProbeIntervalSeconds < 3 || c.Network.ProbeIntervalSeconds > 600) throw new ArgumentException("Probe interval must be 3 to 600 seconds.");
            if (c.Network.ProbeTimeoutMs < 200 || c.Network.ProbeTimeoutMs > 30000) throw new ArgumentException("Probe timeout must be 200 to 30000 ms.");
            foreach (var h in c.Network.ProbeHosts)
            {
                int port;
                var colon = h.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(h.Substring(colon + 1), out port) || port < 1 || port > 65535) throw new ArgumentException("Probe host '" + h + "' must be host:port.");
            }
            foreach (var o in c.CorsOrigins)
            {
                Uri u;
                if (!Uri.TryCreate(o, UriKind.Absolute, out u) || u.AbsolutePath != "/" || (u.Scheme != "http" && u.Scheme != "https"))
                    throw new ArgumentException("CORS origin '" + o + "' must look like http://localhost:5173 (scheme, host, optional port; no path).");
            }
        }

        private static bool IsSlug(string s)
        {
            foreach (var ch in s)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')) return false;
            }
            return s.Length <= 64;
        }
    }

    /// <summary>API keys at rest: Windows DPAPI, bound to the current user. Nothing else on disk can read them.</summary>
    public static class SecretProtector
    {
        private const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fairlead.ApiKey.v1");

        public static string Protect(string secret)
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }

        /// <summary>Null when the value cannot be read (another user's file, a corrupted value).</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (CryptographicException) { return null; }
            catch (FormatException) { return null; }
            catch (PlatformNotSupportedException) { return null; }
        }

        /// <summary>The key to use for a provider: the stored one, else its environment variable.</summary>
        public static string ApiKeyFor(ProviderConfig p)
        {
            return ApiKeyFor(p, null);
        }

        /// <summary>The key to use: the one stored in Fairlead, else Lazarette's (by the variable name), else the environment variable.</summary>
        public static string ApiKeyFor(ProviderConfig p, Func<string, string> vault)
        {
            var stored = Unprotect(p.ApiKeyProtected);
            if (!string.IsNullOrEmpty(stored)) return stored;
            if (!string.IsNullOrEmpty(p.ApiKeyEnv))
            {
                var fromVault = vault == null ? null : vault(p.ApiKeyEnv);
                if (!string.IsNullOrEmpty(fromVault)) return fromVault;
                var env = Environment.GetEnvironmentVariable(p.ApiKeyEnv);
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            }
            return null;
        }

        /// <summary>"stored", "env" or "none": where the key comes from, for the UI.</summary>
        public static string KeySource(ProviderConfig p)
        {
            return KeySource(p, null);
        }

        /// <summary>"stored", "lazarette", "env" or "none": where the key comes from, for the UI.</summary>
        public static string KeySource(ProviderConfig p, Func<string, string> vault)
        {
            if (!string.IsNullOrEmpty(Unprotect(p.ApiKeyProtected))) return "stored";
            if (!string.IsNullOrEmpty(p.ApiKeyEnv) && vault != null && !string.IsNullOrEmpty(vault(p.ApiKeyEnv))) return "lazarette";
            if (!string.IsNullOrEmpty(p.ApiKeyEnv) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(p.ApiKeyEnv))) return "env";
            return "none";
        }

        public static string KeyHint(ProviderConfig p)
        {
            var k = ApiKeyFor(p);
            if (string.IsNullOrEmpty(k) || k.Length < 8) return null;
            return "\u2026" + k.Substring(k.Length - 4);
        }
    }
}
