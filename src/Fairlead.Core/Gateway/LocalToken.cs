using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Fairlead.Core.Gateway
{
    /// <summary>
    /// The local token: apps running as this Windows account reach Fairlead with nothing to configure.
    /// Fairlead makes one client key (fl-...) on first start and keeps it in a file only this account can read,
    /// %LOCALAPPDATA%\Fairlead\local-token. The gateway accepts it from loopback callers as "Local apps"; a caller
    /// names itself with X-Fairlead-Label (as every tool already does) so usage is still shown per app. Other accounts cannot read the file. A
    /// program running as the same user could, but it could equally read a key from that user's Credential Manager,
    /// so a key created and pasted by hand adds no protection there. Keys made in Fairlead > Clients keep working.
    /// FAIRLEAD_LOCAL_TOKEN=off turns it off; FAIRLEAD_LOCAL_TOKEN_FILE moves the file.
    /// </summary>
    public static class LocalToken
    {
        public const string FileEnv = "FAIRLEAD_LOCAL_TOKEN_FILE";
        public const string SwitchEnv = "FAIRLEAD_LOCAL_TOKEN";

        public static bool Enabled()
        {
            var v = (Environment.GetEnvironmentVariable(SwitchEnv) ?? string.Empty).Trim();
            return !Regex.IsMatch(v, "^(off|0|false|no)$", RegexOptions.IgnoreCase);
        }

        public static string DefaultPath()
        {
            var custom = Environment.GetEnvironmentVariable(FileEnv);
            if (!string.IsNullOrEmpty(custom)) return custom;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(local, "Fairlead"), "local-token");
        }

        /// <summary>The key in the file, or null when there is none (or it is not one of ours).</summary>
        public static string Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var first = (File.ReadAllText(path).Split('\n')[0] ?? string.Empty).Trim();
                return first.StartsWith("fl-", StringComparison.Ordinal) && first.Length >= 19 ? first : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>The key, made on first use. Two Fairleads starting at once agree on one key: the first file in place wins.</summary>
        public static string Ensure(string path)
        {
            var existing = Read(path);
            if (existing != null) return existing;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var key = GatewayEngine.NewClientKey();
            var tmp = path + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, key + "\n");
            LockToUser(tmp);
            var raced = Read(path);
            if (raced != null) { TryDelete(tmp); return raced; }
            try { File.Move(tmp, path); }
            catch (IOException) { TryDelete(tmp); }
            return Read(path) ?? key;
        }

        /// <summary>
        /// Windows: only this account and SYSTEM may read the file (inheritance removed), with the inbox icacls.
        /// Best effort: %LOCALAPPDATA% is already private to the account.
        /// </summary>
        public static bool LockToUser(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            try
            {
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (sid == null) return false;
                var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var psi = new ProcessStartInfo(Path.Combine(sys32, "icacls.exe"),
                    "\"" + path + "\" /inheritance:r /grant:r *" + sid.Value + ":F *S-1-5-18:F")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>The app name a local caller sends in X-Fairlead-Label, made safe for logs and the Clients page.</summary>
        public static string AppName(string header)
        {
            var v = (header ?? string.Empty).Trim();
            return Regex.IsMatch(v, "^[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}$") ? v : "Local apps";
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
