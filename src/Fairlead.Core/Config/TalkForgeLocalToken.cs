using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Fairlead.Core.Config
{
    /// <summary>
    /// TalkForge's same-account token: %LOCALAPPDATA%\TalkForge\local-token (first line, 16 characters or more).
    /// Used only when that backend has no key of its own. TALKFORGE_LOCAL_TOKEN=off turns the read off.
    /// TALKFORGE_LOCAL_TOKEN_FILE moves the file. The value is never logged.
    /// </summary>
    public static class TalkForgeLocalToken
    {
        public const string FileEnv = "TALKFORGE_LOCAL_TOKEN_FILE";
        public const string SwitchEnv = "TALKFORGE_LOCAL_TOKEN";
        public const int MinLength = 16;

        public static bool Applies(ProviderConfig provider)
        {
            if (provider == null) return false;
            if (string.Equals(provider.Preset, "talkforge", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(provider.Id, "talkforge", StringComparison.OrdinalIgnoreCase)) return true;
            var url = provider.BaseUrl ?? string.Empty;
            return url.IndexOf("://127.0.0.1:25009", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("://localhost:25009", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string Read()
        {
            var sw = (Environment.GetEnvironmentVariable(SwitchEnv) ?? string.Empty).Trim();
            if (Regex.IsMatch(sw, "^(off|0|false|no)$", RegexOptions.IgnoreCase)) return null;
            try
            {
                var path = Environment.GetEnvironmentVariable(FileEnv);
                if (string.IsNullOrEmpty(path))
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    path = Path.Combine(Path.Combine(local, "TalkForge"), "local-token");
                }
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path);
                var line = text.Split('\n')[0] ?? string.Empty;
                line = line.Trim().TrimEnd('\r');
                return line.Length >= MinLength ? line : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>The local token when this provider is TalkForge and the file has one; otherwise null.</summary>
        public static string KeyFor(ProviderConfig provider)
        {
            if (!Applies(provider)) return null;
            return Read();
        }
    }
}
