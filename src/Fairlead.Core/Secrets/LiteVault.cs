using System;
using System.Collections.Generic;
using System.Linq;

namespace Fairlead.Core.Secrets
{
    /// <summary>
    /// Lazarette Lite as a key source: Lite keeps its items as generic credentials named
    /// "lazarette-lite/&lt;service&gt;/&lt;user&gt;" in Windows Credential Manager, and Fairlead runs as the same Windows
    /// user, so it reads the one whose service is the variable name (DATABRICKS_TOKEN, OPENAI_API_KEY ...) directly.
    /// There is nothing to pair, share or unlock. Exactly one item per name: two with different users are reported as
    /// ambiguous and neither is used. Answers are cached in memory for a few seconds only. Nothing here is logged.
    /// </summary>
    public sealed class LiteVault
    {
        public const string DefaultPrefix = "lazarette-lite/";

        private sealed class Entry
        {
            public string State;
            public string Value;
            public DateTime At;
        }

        private readonly object _lock = new object();
        private readonly Func<string, IList<string>> _lister;
        private readonly Func<string, string> _reader;
        private readonly bool _enabled;
        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads the real Credential Manager (through <see cref="CredentialStore"/>, which tests can replace).</summary>
        public LiteVault() : this(delegate(string f) { return CredentialStore.List(f); }, delegate(string t) { return CredentialStore.Read(t); }, true) { }

        public LiteVault(Func<string, IList<string>> lister, Func<string, string> reader) : this(lister, reader, true) { }

        private LiteVault(Func<string, IList<string>> lister, Func<string, string> reader, bool enabled)
        {
            _lister = lister;
            _reader = reader;
            _enabled = enabled;
            Prefix = DefaultPrefix;
            CacheSeconds = 5;
        }

        /// <summary>A source that never finds anything (tests that must not see the machine's real credentials).</summary>
        public static LiteVault Disabled()
        {
            return new LiteVault(null, null, false);
        }

        public string Prefix { get; set; }

        /// <summary>How long an answer is reused; 0 asks Credential Manager every time.</summary>
        public double CacheSeconds { get; set; }

        /// <summary>The secret of the Lite item whose service is this name; null when there is none, it is empty, or it is ambiguous.</summary>
        public string Value(string name)
        {
            return Resolve(name).Value;
        }

        /// <summary>ok, not-stowed, empty, ambiguous or unavailable.</summary>
        public string StateOf(string name)
        {
            return Resolve(name).State;
        }

        /// <summary>Forgets every cached answer and asks again for these names; true when one of them now answers differently.</summary>
        public bool Refresh(IEnumerable<string> names)
        {
            var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                foreach (var kv in _cache) before[kv.Key] = kv.Value.State + "|" + kv.Value.Value;
                _cache.Clear();
            }
            var changed = false;
            foreach (var n in (names ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var e = Resolve(n);
                string old;
                var now = e.State + "|" + e.Value;
                if (before.TryGetValue(n, out old) ? old != now : e.State == "ok") changed = true;
            }
            return changed;
        }

        public List<LazaretteName> Names(IEnumerable<string> names)
        {
            return (names ?? new string[0]).Distinct(StringComparer.Ordinal)
                .Select(n => new LazaretteName { Name = n, State = StateOf(n) }).ToList();
        }

        private Entry Resolve(string name)
        {
            if (!_enabled) return new Entry { State = "unavailable" };
            if (name == null || !LazaretteProtocol.Name.IsMatch(name)) return new Entry { State = "not-stowed" };
            lock (_lock)
            {
                Entry cached;
                if (CacheSeconds > 0 && _cache.TryGetValue(name, out cached) && (DateTime.UtcNow - cached.At).TotalSeconds < CacheSeconds) return cached;
            }
            var fresh = Look(name);
            fresh.At = DateTime.UtcNow;
            lock (_lock) _cache[name] = fresh;
            return fresh;
        }

        private Entry Look(string name)
        {
            try
            {
                var prefix = Prefix;
                var targets = (_lister(prefix + "*") ?? new List<string>())
                    .Where(t => t != null && t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Where(t =>
                    {
                        var rest = t.Substring(prefix.Length);
                        var slash = rest.IndexOf('/');
                        return string.Equals(slash < 0 ? rest : rest.Substring(0, slash), name, StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                if (targets.Count == 0) return new Entry { State = "not-stowed" };
                if (targets.Count > 1) return new Entry { State = "ambiguous" };
                var secret = _reader(targets[0]);
                if (string.IsNullOrEmpty(secret)) return new Entry { State = "empty" };
                return new Entry { State = "ok", Value = secret };
            }
            catch (Exception)
            {
                return new Entry { State = "unavailable" };
            }
        }
    }
}
