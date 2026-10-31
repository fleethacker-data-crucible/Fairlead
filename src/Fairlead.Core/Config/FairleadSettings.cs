using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fairlead.Core.Config
{
    /// <summary>
    /// Process settings from the environment and config\fairlead.env: where things live, the port and the bind.
    /// Everything the browser configures lives in fairlead.json (<see cref="FairleadConfig"/>) instead.
    /// </summary>
    public sealed class FairleadSettings
    {
        /// <summary>Registry port (docs/UNIFIED-UI.md section 1). Tools are pointed at it, so it should not move.</summary>
        public const int RegistryPort = 8776;

        public string RootDirectory { get; set; }
        /// <summary>%LOCALAPPDATA%\Fairlead: fairlead.json and its backup.</summary>
        public string DataDirectory { get; set; }
        /// <summary>logs\ under the app root: runtime files and JSONL logs (never prompts or keys).</summary>
        public string LogDirectory { get; set; }

        /// <summary>Request-log retention (FAIRLEAD_LOG_MAX_DAYS, FAIRLEAD_LOG_MAX_MB). 0 = keep everything, the default.</summary>
        public int LogMaxAgeDays { get; set; }
        public int LogMaxMegabytes { get; set; }
        /// <summary>The most seconds one request may take, however busy its stream (FAIRLEAD_MAX_REQUEST_SECONDS). 0 = no cap, the default; the provider timeout stays an idle limit.</summary>
        public int MaxRequestSeconds { get; set; }

        /// <summary>Folder of documents for cited answers (Bearing). Empty = off. Set by FAIRLEAD_BEARING_DIR; never in fairlead.json.</summary>
        public string BearingDirectory { get; set; }
        public string WebRootDirectory { get; set; }
        public int PreferredPort { get; set; }
        /// <summary>
        /// Off by default: Fairlead is a gateway for other tools, so closing the setup tab must not stop it
        /// (the TechStack tray exception). FAIRLEAD_AUTO_STOP=1 makes the tab own the engine.
        /// </summary>
        public bool AutoStop { get; set; }

        public string ConfigPath { get { return Path.Combine(DataDirectory, "fairlead.json"); } }

        public FairleadSettings()
        {
            RootDirectory = string.Empty;
            DataDirectory = string.Empty;
            LogDirectory = string.Empty;
            PreferredPort = RegistryPort;
        }

        public static FairleadSettings FromEnvironment(string rootDirectory)
        {
            var s = new FairleadSettings { RootDirectory = rootDirectory };
            var data = Env("FAIRLEAD_DATA_DIR");
            if (string.IsNullOrEmpty(data))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                data = string.IsNullOrEmpty(local) ? Path.Combine(rootDirectory, "data") : Path.Combine(local, "Fairlead");
            }
            s.DataDirectory = data;
            s.LogDirectory = Env("FAIRLEAD_LOG_DIR") ?? Path.Combine(rootDirectory, "logs");
            s.WebRootDirectory = Env("FAIRLEAD_WEBROOT");
            int retain;
            if (int.TryParse(Env("FAIRLEAD_LOG_MAX_DAYS"), out retain) && retain > 0) s.LogMaxAgeDays = retain;
            if (int.TryParse(Env("FAIRLEAD_LOG_MAX_MB"), out retain) && retain > 0) s.LogMaxMegabytes = retain;
            int cap;
            if (int.TryParse(Env("FAIRLEAD_MAX_REQUEST_SECONDS"), out cap) && cap > 0) s.MaxRequestSeconds = cap;
            s.BearingDirectory = Env("FAIRLEAD_BEARING_DIR");
            int port;
            var portText = Env("FAIRLEAD_PORT");
            if (portText != null && int.TryParse(portText, out port) && port > 0 && port < 65536) s.PreferredPort = port;
            // Fairlead listens on loopback only (Fleet rule). The old FAIRLEAD_BIND=lan is refused with a clear message.
            var bind = (Env("FAIRLEAD_BIND") ?? "loopback").ToLowerInvariant();
            if (bind != "loopback") throw new ArgumentException("FAIRLEAD_BIND=" + bind + " is not supported: Fairlead listens on this machine only (loopback). Remove FAIRLEAD_BIND from config\\fairlead.env.");
            s.AutoStop = Env("FAIRLEAD_AUTO_STOP") == "1";
            return s;
        }

        private static string Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        /// <summary>Applies KEY=VALUE lines to the process environment without overriding variables already set.</summary>
        public static int LoadEnvFile(string path)
        {
            if (!File.Exists(path)) return 0;
            var applied = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var m = Regex.Match(line, @"^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$");
                if (!m.Success) continue;
                var key = m.Groups[1].Value;
                var value = m.Groups[2].Value.Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                    value = value.Substring(1, value.Length - 2);
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) continue;
                Environment.SetEnvironmentVariable(key, value);
                applied++;
            }
            return applied;
        }
    }

    /// <summary>Removes token-shaped strings before anything reaches a log.</summary>
    public static class Redactor
    {
        private static readonly Regex TokenShaped = new Regex(
            @"\b(sk-ant-[A-Za-z0-9_\-]{10,}|sk-[A-Za-z0-9_\-]{10,}|fl-[A-Za-z0-9]{16,}|AIza[A-Za-z0-9_\-]{20,}|gsk_[A-Za-z0-9]{20,}|xai-[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|glpat-[A-Za-z0-9_\-]{15,}|eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})\b|(?i:bearer\s+)[A-Za-z0-9._\-]{16,}",
            RegexOptions.Compiled);

        public static string Redact(string text)
        {
            return string.IsNullOrEmpty(text) ? text : TokenShaped.Replace(text, "[REDACTED]");
        }
    }

    /// <summary>Shell-style globs for model aliases: * any run, ? one character, [0-9] a class.</summary>
    public static class Glob
    {
        private static readonly Dictionary<string, Regex> Cache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        public static bool IsMatch(string pattern, string text)
        {
            Regex re;
            lock (Cache)
            {
                if (!Cache.TryGetValue(pattern, out re))
                {
                    var sb = new System.Text.StringBuilder("^");
                    var inClass = false;
                    foreach (var ch in pattern)
                    {
                        if (inClass) { sb.Append(ch == ']' ? "]" : (ch == '\\' ? "\\\\" : ch.ToString())); if (ch == ']') inClass = false; continue; }
                        switch (ch)
                        {
                            case '*': sb.Append(".*"); break;
                            case '?': sb.Append('.'); break;
                            case '[': sb.Append('['); inClass = true; break;
                            default: sb.Append(Regex.Escape(ch.ToString())); break;
                        }
                    }
                    if (inClass) sb.Append(']');
                    sb.Append('$');
                    re = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    Cache[pattern] = re;
                }
            }
            return re.IsMatch(text);
        }

        /// <summary>An exact pattern wins; otherwise the first glob in file order (so "*haiku*" listed before "claude-*" wins).</summary>
        public static string Resolve(IDictionary<string, string> aliases, string text)
        {
            foreach (var kv in aliases) if (string.Equals(kv.Key, text, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            foreach (var kv in aliases)
            {
                if (IsMatch(kv.Key, text)) return kv.Value;
            }
            return null;
        }
    }
}
