using System;
using System.Collections.Generic;
using System.IO;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Config
{
    /// <summary>
    /// The AI gateway model files and the non-secret values they name (the gateway base URL), imported through the
    /// browser. Both are kept encrypted with Windows DPAPI for the current user in Fairlead's data folder
    /// (%LOCALAPPDATA%\Fairlead by default), never in the app folder or a repository: the real file and address are
    /// deployment-specific. Tokens are never stored here; they come from Lazarette or the environment. A stored value
    /// wins over an environment variable of the same name. A store with no directory keeps everything in memory (tests).
    /// </summary>
    public sealed class GatewayStore
    {
        /// <summary>The single model file of an older import; read as the file named <see cref="LegacyName"/>.</summary>
        public const string FileName = "ai-gateway-models.dpapi";
        public const string LegacyName = "models";
        /// <summary>One encrypted file per imported model file: ai-gateway-models\&lt;name&gt;.dpapi.</summary>
        public const string FilesFolder = "ai-gateway-models";
        public const string ValuesName = "ai-gateway-values.json";
        /// <summary>Largest model file accepted (a real one is a few KB).</summary>
        public const int MaxFileChars = 256 * 1024;
        public const int MaxFiles = 16;
        private static readonly System.Text.RegularExpressions.Regex NameShape = new System.Text.RegularExpressions.Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,47}$");

        private readonly object _lock = new object();
        private readonly string _dir;
        private readonly Func<string, string> _protect;
        private readonly Func<string, string> _unprotect;
        private readonly SortedDictionary<string, string> _files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public GatewayStore(string directory) : this(directory, SecretProtector.Protect, SecretProtector.Unprotect) { }

        public GatewayStore(string directory, Func<string, string> protect, Func<string, string> unprotect)
        {
            _dir = string.IsNullOrWhiteSpace(directory) ? null : directory;
            _protect = protect;
            _unprotect = unprotect;
            if (_dir == null) return;
            var legacy = Path.Combine(_dir, FileName);
            if (File.Exists(legacy)) _files[LegacyName] = File.ReadAllText(legacy).Trim();
            var folder = Path.Combine(_dir, FilesFolder);
            if (Directory.Exists(folder))
                foreach (var f in Directory.GetFiles(folder, "*.dpapi"))
                    _files[Path.GetFileNameWithoutExtension(f)] = File.ReadAllText(f).Trim();
            var values = Path.Combine(_dir, ValuesName);
            if (File.Exists(values))
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(values), JsonSettings.File);
                if (parsed != null) foreach (var kv in parsed) _values[kv.Key] = kv.Value;
            }
        }

        /// <summary>Where a model file is kept, for messages; never its contents.</summary>
        public string Location(string name)
        {
            if (_dir == null) return "memory:" + name;
            return string.Equals(name, LegacyName, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(_dir, FileName))
                ? Path.Combine(_dir, FileName)
                : Path.Combine(_dir, FilesFolder, name + ".dpapi");
        }

        public bool HasFile { get { lock (_lock) return _files.Count > 0; } }

        /// <summary>The imported model files' names, sorted.</summary>
        public List<string> Names { get { lock (_lock) return new List<string>(_files.Keys); } }

        /// <summary>
        /// A safe name for a model file from what the user picked ("Gemini chat models.yaml" becomes
        /// "Gemini-chat-models"); null when nothing usable is left.
        /// </summary>
        public static string NameFrom(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return LegacyName;
            var n = Path.GetFileNameWithoutExtension(fileName.Trim());
            var sb = new System.Text.StringBuilder();
            foreach (var ch in n) sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-' ? ch : '-');
            var clean = sb.ToString().Trim('-', '.', '_');
            if (clean.Length > 48) clean = clean.Substring(0, 48);
            return NameShape.IsMatch(clean) ? clean : null;
        }

        /// <summary>A model file, decrypted; null when there is none or it cannot be read (another Windows user).</summary>
        public string ReadFile(string name)
        {
            lock (_lock)
            {
                string stored;
                return name != null && _files.TryGetValue(name, out stored) && !string.IsNullOrEmpty(stored) ? _unprotect(stored) : null;
            }
        }

        /// <summary>Adds a model file under <paramref name="name"/>, or replaces the one of that name.</summary>
        public void SaveFile(string name, string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml)) throw new ArgumentException("The model file is empty.");
            if (name == null || !NameShape.IsMatch(name)) throw new ArgumentException("A model file name must be letters, digits, dot, dash or underscore.");
            lock (_lock)
            {
                if (!_files.ContainsKey(name) && _files.Count >= MaxFiles) throw new ArgumentException("At most " + MaxFiles + " model files can be imported.");
                var stored = _protect(yaml);
                if (string.Equals(name, LegacyName, StringComparison.OrdinalIgnoreCase)) DeleteLegacy();
                _files[name] = stored;
                if (_dir == null) return;
                Directory.CreateDirectory(Path.Combine(_dir, FilesFolder));
                Write(Path.Combine(FilesFolder, name + ".dpapi"), stored);
            }
        }

        /// <summary>Forgets one model file; false when there was none of that name.</summary>
        public bool RemoveFile(string name)
        {
            lock (_lock)
            {
                if (name == null || !_files.Remove(name)) return false;
                if (_dir == null) return true;
                if (string.Equals(name, LegacyName, StringComparison.OrdinalIgnoreCase)) DeleteLegacy();
                var path = Path.Combine(_dir, FilesFolder, name + ".dpapi");
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
        }

        private void DeleteLegacy()
        {
            if (_dir == null) return;
            var legacy = Path.Combine(_dir, FileName);
            if (File.Exists(legacy)) File.Delete(legacy);
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

        /// <summary>Forgets every imported model file and every stored value.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                foreach (var name in new List<string>(_files.Keys)) RemoveFile(name);
                _values.Clear();
                if (_dir == null) return;
                var path = Path.Combine(_dir, ValuesName);
                if (File.Exists(path)) File.Delete(path);
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
