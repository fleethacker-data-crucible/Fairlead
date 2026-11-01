using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fairlead.Core.Config;
using Fairlead.Core.Providers;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Secrets
{
    /// <summary>What Fairlead knows about one name it asked Lazarette for: never the value.</summary>
    public sealed class LazaretteName
    {
        public string Name { get; set; }
        /// <summary>ok, not-stowed, empty, or unknown (not asked yet).</summary>
        public string State { get; set; }
    }

    /// <summary>
    /// Fairlead's link to Lazarette, the suite's encrypted vault: holds the pairing code (DPAPI, in the data folder),
    /// asks Lazarette for secrets by name, and keeps the answers in memory only. Values survive Lazarette locking or
    /// closing for <see cref="HoldHours"/> after Lazarette last confirmed them (its tab closes often; dropping them at
    /// once would stop routing), and are dropped at once when Lazarette says the pairing is gone, on unpair, on exit,
    /// and when the hold runs out. Nothing here is ever logged except states.
    /// </summary>
    public sealed class LazaretteLink : IDisposable
    {
        public const string PairingFile = "lazarette-pairing.dpapi";
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan PollWhenOk = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan PollOtherwise = TimeSpan.FromSeconds(60);

        private readonly object _lock = new object();
        private readonly string _dir;
        private readonly UpstreamClient _upstream;
        private readonly Func<string, string> _protect;
        private readonly Func<string, string> _unprotect;
        private readonly Func<IList<string>> _names;
        private readonly Func<string> _lazaretteDataDir;
        private LazarettePairing _pairing;
        private Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> _states = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _revision = string.Empty;
        private string _askedKey = string.Empty;
        private DateTimeOffset? _confirmedAt;
        private Timer _timer;
        private int _polling;
        private bool _disposed;

        /// <param name="directory">Fairlead's data folder (pairing file); null keeps the pairing in memory (tests).</param>
        /// <param name="names">The names to ask for: the gateway token variables and provider key variables.</param>
        /// <param name="lazaretteDataDir">Where Lazarette writes logs\lazarette-server-*.json; null uses %LAZARETTE_DATA% or %LOCALAPPDATA%\Lazarette.</param>
        public LazaretteLink(string directory, UpstreamClient upstream, Func<IList<string>> names, Func<string> lazaretteDataDir, Func<string, string> protect, Func<string, string> unprotect)
        {
            _dir = string.IsNullOrWhiteSpace(directory) ? null : directory;
            _upstream = upstream;
            _names = names;
            _lazaretteDataDir = lazaretteDataDir ?? DefaultLazaretteDataDir;
            _protect = protect ?? SecretProtector.Protect;
            _unprotect = unprotect ?? SecretProtector.Unprotect;
            HoldHours = 8;
            State = "not-paired";
            if (_dir == null) return;
            var file = Path.Combine(_dir, PairingFile);
            if (!File.Exists(file)) return;
            _pairing = LazarettePairing.TryParse(_unprotect(File.ReadAllText(file).Trim()));
            State = _pairing == null ? "not-paired" : "unknown";
            if (_pairing == null) Message = "The saved Lazarette pairing cannot be read by this Windows account. Pair again.";
        }

        /// <summary>Raised (on a pool thread) when the set of values or their per-name states changed.</summary>
        public event Action Changed;

        /// <summary>How long values are kept after Lazarette last confirmed them; 0 drops them as soon as it is locked or closed.</summary>
        public double HoldHours { get; set; }

        /// <summary>
        /// not-paired, unknown (not asked yet), ok, locked, not-running, peer-unverified, rate-limited, error, or idle
        /// (paired but nothing to ask for).
        /// </summary>
        public string State { get; private set; }
        public string Message { get; private set; }
        public DateTimeOffset? LastChecked { get; private set; }

        public bool Paired { get { lock (_lock) return _pairing != null; } }
        public string PairingHint { get { lock (_lock) return _pairing == null ? null : _pairing.Hint; } }

        /// <summary>When the held values are forgotten unless Lazarette confirms them again; null when none are held.</summary>
        public DateTimeOffset? ExpiresAt
        {
            get
            {
                lock (_lock) return _confirmedAt.HasValue && _values.Count > 0 && HoldHours > 0 ? _confirmedAt.Value.AddHours(HoldHours) : (DateTimeOffset?)null;
            }
        }

        /// <summary>The value Lazarette shared under this name; null when none is held.</summary>
        public string Value(string name)
        {
            if (name == null) return null;
            lock (_lock)
            {
                ExpireIfDue(DateTimeOffset.UtcNow);
                string v;
                return _values.TryGetValue(name, out v) ? v : null;
            }
        }

        public List<LazaretteName> Names()
        {
            lock (_lock)
            {
                ExpireIfDue(DateTimeOffset.UtcNow);
                return (_names() ?? new List<string>()).Distinct(StringComparer.Ordinal).Select(n =>
                {
                    string s;
                    return new LazaretteName { Name = n, State = _values.ContainsKey(n) ? "ok" : _states.TryGetValue(n, out s) ? s : "unknown" };
                }).ToList();
            }
        }

        /// <summary>Saves the pairing code (DPAPI) and asks Lazarette at once. Throws ArgumentException for text that is not a code.</summary>
        public Task PairAsync(string code)
        {
            var pairing = LazarettePairing.TryParse(code);
            if (pairing == null) throw new ArgumentException("That is not a Lazarette pairing code. In Lazarette, open Settings, Fairlead, press Pair, and copy the code it shows (it starts with lzfl1.).");
            lock (_lock)
            {
                _pairing = pairing;
                Drop("unknown", null);
                if (_dir != null)
                {
                    Directory.CreateDirectory(_dir);
                    var path = Path.Combine(_dir, PairingFile);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, _protect(pairing.ToCode()));
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            RaiseChanged();
            return PollAsync();
        }

        /// <summary>Forgets the pairing and every value.</summary>
        public void Unpair()
        {
            lock (_lock)
            {
                _pairing = null;
                Drop("not-paired", null);
                if (_dir != null)
                {
                    var path = Path.Combine(_dir, PairingFile);
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            RaiseChanged();
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_timer != null || _disposed) return;
                _timer = new Timer(_ => { var ignored = PollAsync(); }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>Asks Lazarette once (also used by the Refresh button). Never throws.</summary>
        public async Task PollAsync()
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;
            var changed = false;
            try { changed = await PollOnceAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                lock (_lock) { State = "error"; Message = "Lazarette could not be asked (" + ex.GetType().Name + ")."; }
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
                lock (_lock)
                {
                    LastChecked = DateTimeOffset.UtcNow;
                    if (ExpireIfDue(DateTimeOffset.UtcNow)) changed = true;
                    if (_timer != null && !_disposed) _timer.Change(State == "ok" ? PollWhenOk : PollOtherwise, Timeout.InfiniteTimeSpan);
                }
            }
            if (changed) RaiseChanged();
        }

        private async Task<bool> PollOnceAsync()
        {
            LazarettePairing pairing;
            List<string> names;
            string ifRevision;
            lock (_lock)
            {
                pairing = _pairing;
                if (pairing == null) { State = "not-paired"; return false; }
                names = (_names() ?? new List<string>()).Where(n => n != null && LazaretteProtocol.Name.IsMatch(n)).Distinct(StringComparer.Ordinal).Take(32).ToList();
                if (names.Count == 0) { State = "idle"; Message = "Paired. Nothing to ask for yet: no provider or gateway names a key variable."; return false; }
                var key = string.Join(",", names);
                if (key != _askedKey) { _revision = string.Empty; _askedKey = key; }
                ifRevision = _revision;
            }

            var port = FindLazarettePort();
            if (port == 0) return Unreachable("not-running", "Lazarette is not running. Open it and unlock it to refresh keys.");

            var nonce = LazaretteProtocol.NewNonce();
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var body = new JObject
            {
                { "pairing", pairing.Id },
                { "nonce", nonce },
                { "time", time },
                { "names", new JArray(names.Cast<object>().ToArray()) },
                { "ifRevision", ifRevision },
                { "proof", LazaretteProtocol.Proof(pairing, time, nonce, names, ifRevision) },
            };
            var headers = new Dictionary<string, string> { { LazaretteProtocol.PeerHeader, LazaretteProtocol.PeerHeaderValue } };
            var answer = await _upstream.PostLocalJsonAsync(port, LazaretteProtocol.PeerPath, body.ToString(Formatting.None), headers, RequestTimeout).ConfigureAwait(false);
            if (answer.Key == 0) return Unreachable("not-running", "Lazarette is not answering. Open it and unlock it to refresh keys.");

            JObject reply = null;
            try { reply = JObject.Parse(answer.Value ?? string.Empty); }
            catch (JsonException) { }
            var state = reply == null ? null : (string)reply.At("state");
            var message = reply == null ? null : (string)reply.At("message");

            if (answer.Key == 200 && (state == "ok" || state == "unchanged"))
                return Accept(pairing, nonce, ifRevision, reply, state);

            lock (_lock)
            {
                switch (state)
                {
                    case "locked":
                        return Unreachable("locked", "Lazarette is locked. Unlock it to refresh keys.");
                    case "not-paired":
                        if (!ReferenceEquals(pairing, _pairing)) return false;   // re-paired meanwhile
                        Drop("not-paired", "Lazarette no longer knows this pairing (it was removed or replaced there). Pair again.");
                        return true;
                    case "peer-unverified":
                    case "rate-limited":
                        State = state;
                        Message = string.IsNullOrEmpty(message) ? "Lazarette refused the request (" + state + ")." : message;
                        return false;
                    default:
                        State = "error";
                        Message = "Lazarette answered HTTP " + answer.Key + (string.IsNullOrEmpty(state) ? string.Empty : " (" + state + ")") + ".";
                        return false;
                }
            }
        }

        private bool Accept(LazarettePairing pairing, string nonce, string ifRevision, JObject reply, string state)
        {
            var revision = (string)reply.At("revision") ?? string.Empty;
            var sealedText = (string)reply.At("sealed");
            if ((string)reply.At("pairing") != pairing.Id || (string)reply.At("nonce") != nonce || !LazaretteProtocol.Revision.IsMatch(revision) || sealedText == null)
                return Rejected();
            byte[] blob;
            try { blob = Convert.FromBase64String(sealedText); }
            catch (FormatException) { return Rejected(); }
            var plain = LazaretteProtocol.Open(pairing, nonce, state, revision, blob);
            if (plain == null) return Rejected();

            lock (_lock)
            {
                if (!ReferenceEquals(pairing, _pairing)) return false;
                if (state == "unchanged")
                {
                    if (ifRevision != revision) return Rejected();
                    State = "ok";
                    Message = null;
                    _confirmedAt = DateTimeOffset.UtcNow;
                    return false;
                }
                JObject payload;
                try { payload = JObject.Parse(Encoding.UTF8.GetString(plain)); }
                catch (JsonException) { return RejectedLocked(); }
                if ((string)payload.At("format") != LazaretteProtocol.PayloadFormat || (string)payload.At("revision") != revision) return RejectedLocked();
                var secrets = payload.At("secrets") as JObject;
                if (secrets == null) return RejectedLocked();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                var states = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in secrets.Properties())
                {
                    var s = (string)p.Value.At("state") ?? "empty";
                    var v = (string)p.Value.At("value");
                    if (s == "ok" && !string.IsNullOrEmpty(v)) values[p.Name] = v;
                    else states[p.Name] = s == "ok" ? "empty" : s;
                }
                var changed = !SameKeys(values, _values) || !SameValues(values, _values) || !SameValues(states, _states);
                _values = values;
                _states = states;
                _revision = revision;
                _confirmedAt = DateTimeOffset.UtcNow;
                State = "ok";
                Message = null;
                return changed;
            }
        }

        private bool Rejected()
        {
            lock (_lock) return RejectedLocked();
        }

        private bool RejectedLocked()
        {
            State = "error";
            Message = "An answer on Lazarette's port did not prove it came from the paired Lazarette; it was ignored.";
            return false;
        }

        /// <summary>Not running or locked: keep what is held (within the hold), say why.</summary>
        private bool Unreachable(string state, string message)
        {
            lock (_lock)
            {
                State = state;
                Message = message;
                if (HoldHours <= 0 && _values.Count > 0) { _values.Clear(); _confirmedAt = null; return true; }
                return false;
            }
        }

        private void Drop(string state, string message)
        {
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            _states = new Dictionary<string, string>(StringComparer.Ordinal);
            _revision = string.Empty;
            _askedKey = string.Empty;
            _confirmedAt = null;
            State = state;
            Message = message;
        }

        /// <summary>Caller holds the lock. True when held values ran out and were dropped.</summary>
        private bool ExpireIfDue(DateTimeOffset now)
        {
            // A hold of 0 means "only while Lazarette is open": Unreachable drops the values then, not the clock.
            if (_values.Count == 0 || !_confirmedAt.HasValue || HoldHours <= 0) return false;
            if (now < _confirmedAt.Value.AddHours(HoldHours)) return false;
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            _confirmedAt = null;
            _revision = string.Empty;
            Message = "Keys from Lazarette were forgotten after " + HoldHours.ToString(System.Globalization.CultureInfo.InvariantCulture) + " hours. Unlock Lazarette to refresh them.";
            return true;
        }

        private static bool SameKeys(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            return a.Count == b.Count && a.Keys.All(b.ContainsKey);
        }

        private static bool SameValues(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                string other;
                if (!b.TryGetValue(kv.Key, out other) || !string.Equals(other, kv.Value, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(); }
            catch (Exception) { }
        }

        /// <summary>The port of a running Lazarette from its runtime files (live pid only); 0 when none.</summary>
        private int FindLazarettePort()
        {
            var dir = _lazaretteDataDir();
            if (string.IsNullOrEmpty(dir)) return 0;
            var logs = Path.Combine(dir, "logs");
            if (!Directory.Exists(logs)) return 0;
            var best = 0;
            var bestStarted = DateTimeOffset.MinValue;
            foreach (var file in Directory.GetFiles(logs, "lazarette-server-*.json"))
            {
                try
                {
                    var o = JObject.Parse(File.ReadAllText(file));
                    var pid = (int?)o.At("pid");
                    var port = (int?)o.At("port");
                    if ((string)o.At("app") != "lazarette" || !pid.HasValue || !port.HasValue || port.Value < 1 || port.Value > 65535) continue;
                    if (!ProcessAlive(pid.Value)) continue;
                    DateTimeOffset started;
                    if (!DateTimeOffset.TryParse((string)o.At("startedAt") ?? string.Empty, out started)) started = File.GetLastWriteTimeUtc(file);
                    if (best == 0 || started > bestStarted) { best = port.Value; bestStarted = started; }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
                catch (InvalidCastException) { }
                catch (FormatException) { }
            }
            return best;
        }

        /// <summary>Overridable for tests; the default checks that a process with this id exists.</summary>
        public Func<int, bool> ProcessAlive = DefaultProcessAlive;

        private static bool DefaultProcessAlive(int pid)
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }

        private static string DefaultLazaretteDataDir()
        {
            var configured = Environment.GetEnvironmentVariable("LAZARETTE_DATA");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().Trim('"');
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Lazarette");
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                if (_timer != null) { _timer.Dispose(); _timer = null; }
                Drop(State, Message);
            }
        }
    }
}
