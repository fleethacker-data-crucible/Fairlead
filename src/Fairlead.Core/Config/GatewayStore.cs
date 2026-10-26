using System;
using System.Collections.Generic;
using System.IO;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Config
{
    /// <summary>
    /// The AI gateway model file and the non-secret values it names (the gateway base URL), imported through the
    /// browser. Both are kept encrypted with Windows DPAPI for the current user in Fairlead's data folder
    /// (%LOCALAPPDATA%\Fairlead by default), never in the app folder or a repository: the real file and address are
    /// deployment-specific. Tokens are never stored here; they come from Lazarette or the environment. A stored value
    /// wins over an environment variable of the same name. A store with no directory keeps everything in memory (tests).
    /// </summary>
    public sealed class GatewayStore
    {
        public const string FileName = "ai-gateway-models.dpapi";
        public const string ValuesName = "ai-gateway-values.json";
        /// <summary>Largest model file accepted (a real one is a few KB).</summary>
        public const int MaxFileChars = 256 * 1024;

        private readonly object _lock = new object();
        private readonly string _dir;
        private readonly Func<string, string> _protect;
        private readonly Func<string, string> _unprotect;
        private string _file;
        private Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public GatewayStore(string directory) : this(directory, SecretProtector.Protect, SecretProtector.Unprotect) { }

        public GatewayStore(string directory, Func<string, string> protect, Func<string, string> unprotect)
        {
            _dir = string.IsNullOrWhiteSpace(directory) ? null : directory;
            _protect = protect;
            _unprotect = unprotect;
            if (_dir == null) return;
            var file = Path.Combine(_dir, FileName);
            if (File.Exists(file)) _file = File.ReadAllText(file).Trim();
            var values = Path.Combine(_dir, ValuesName);
            if (File.Exists(values))
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(values), JsonSettings.File);
                if (parsed != null) foreach (var kv in parsed) _values[kv.Key] = kv.Value;
            }
        }

        /// <summary>Where the imported file is kept, for messages; never its contents.</summary>
        public string Location { get { return _dir == null ? "memory" : Path.Combine(_dir, FileName); } }

        public bool HasFile { get { lock (_lock) return !string.IsNullOrEmpty(_file); } }

        /// <summary>The imported model file, decrypted; null when there is none or it cannot be read (another Windows user).</summary>
        public string ReadFile()
        {
            lock (_lock) return string.IsNullOrEmpty(_file) ? null : _unprotect(_file);
        }

        public void SaveFile(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml)) throw new ArgumentException("The model file is empty.");
            lock (_lock)
            {
                _file = _protect(yaml);
                Write(FileName, _file);
            }
        }

        /// <summary>The stored value of a variable, decrypted; null when none is stored.</summary>
        public string Value(string name)
        {
            lock (_lock)
            {
                string stored;
                if (name == null || !_values.TryGetValue(name, out stored)) return null;
                var plain = _unprotect(stored);
                return string.IsNullOrEmpty(plain) ? null : plain;
            }
        }

        public bool HasValue(string name) { return Value(name) != null; }

        /// <summary>Stores each value encrypted; an empty value forgets the stored one.</summary>
        public void SetValues(IDictionary<string, string> values)
        {
            lock (_lock)
            {
                foreach (var kv in values)
                {
                    if (string.IsNullOrWhiteSpace(kv.Value)) _values.Remove(kv.Key);
                    else _values[kv.Key] = _protect(kv.Value.Trim());
                }
                Write(ValuesName, JsonConvert.SerializeObject(_values, Formatting.Indented, JsonSettings.File));
            }
        }

        /// <summary>Forgets the imported file and every stored value.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _file = null;
                _values.Clear();
                if (_dir == null) return;
                foreach (var name in new[] { FileName, ValuesName })
                {
                    var path = Path.Combine(_dir, name);
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }

        /// <summary>Variable lookup for the model file: the stored value, else <paramref name="fallback"/> (the process environment).</summary>
        public Func<string, string> Environment(Func<string, string> fallback)
        {
            if (fallback == null) fallback = System.Environment.GetEnvironmentVariable;
            return name => Value(name) ?? fallback(name);
        }

        private void Write(string name, string text)
        {
            if (_dir == null) return;
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, name);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
