using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Fairlead.Core.Serialization;

namespace Fairlead.Core.Host
{
    /// <summary>
    /// Lets the fairlead CLI reach the admin API of the engine running as the same Windows user. The engine writes a
    /// second, CLI-only token to the data folder, encrypted with DPAPI for the current user: another account cannot
    /// read it, and the browser's session token never touches disk. The file is removed when the engine stops.
    /// </summary>
    public static class CliAccess
    {
        public const string FileName = "fairlead-cli.json";
        private const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fairlead.CliToken.v1");

        public static string Protect(string token)
        {
            return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>The token, or null when the value is not ours or another Windows user wrote it.</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException) { return null; }
            catch (FormatException) { return null; }
        }
    }

    public sealed partial class FairleadHost
    {
        private string _cliFile;

        /// <summary>Accepted like the session token; only ever stored DPAPI-encrypted (see <see cref="CliAccess"/>).</summary>
        public string CliToken { get; private set; }

        private void WriteCliFile(HostInfo info)
        {
            if (string.IsNullOrEmpty(Settings.DataDirectory)) return;
            try
            {
                Directory.CreateDirectory(Settings.DataDirectory);
                var path = Path.Combine(Settings.DataDirectory, CliAccess.FileName);
                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                File.WriteAllText(path, Json.Serialize(new { pid = pid, port = info.Port, url = info.Url, gateway = info.GatewayUrl, version = Version, token = CliAccess.Protect(CliToken) }), new UTF8Encoding(false));
                _cliFile = path;
            }
            catch (IOException ex) { Log.Warn("CLI_FILE", "Could not write the CLI access file: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Warn("CLI_FILE", "Could not write the CLI access file: " + ex.Message); }
            catch (CryptographicException ex) { Log.Warn("CLI_FILE", "Could not protect the CLI token: " + ex.Message); }
        }

        /// <summary>Removes the CLI file, unless another engine has written its own since.</summary>
        private void RemoveCliFile()
        {
            if (_cliFile == null) return;
            try
            {
                if (!File.Exists(_cliFile)) return;
                var pid = (int?)JObject.Parse(File.ReadAllText(_cliFile))["pid"];
                if (pid == System.Diagnostics.Process.GetCurrentProcess().Id) File.Delete(_cliFile);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
    }
}
