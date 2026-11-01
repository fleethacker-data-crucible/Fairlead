using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Fairlead.Core.Secrets
{
    /// <summary>
    /// Saves a client key where the calling tool reads it: Windows Credential Manager, a generic credential
    /// with the target "Fairlead/&lt;AppName&gt;", for the signed-in Windows user on this computer only
    /// (persisted locally, not roaming). The key is written as UTF-16 text, the same shape as
    /// <c>cmdkey /generic:Fairlead/App /pass:...</c>. Nothing is logged.
    /// </summary>
    public static class CredentialStore
    {
        public const string TargetPrefix = "Fairlead/";

        private static readonly Regex AppNamePattern = new Regex("^[A-Za-z][A-Za-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

        /// <summary>Replaceable so tests never touch the real Credential Manager. Returns null when saved, else a short reason.</summary>
        public static Func<string, string, string> Writer = WriteNative;

        public static bool IsValidAppName(string app)
        {
            return app != null && AppNamePattern.IsMatch(app);
        }

        public static string TargetFor(string app)
        {
            return TargetPrefix + app;
        }

        /// <summary>Saves <paramref name="key"/> for the app. Returns null on success, otherwise the reason.</summary>
        public static string Save(string app, string key)
        {
            if (!IsValidAppName(app)) return "App names start with a letter and use letters, digits and hyphens (up to 40 characters).";
            if (string.IsNullOrEmpty(key)) return "There is no key to save.";
            return Writer(TargetFor(app), key);
        }

        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

        private static string WriteNative(string target, string key)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return "Windows Credential Manager is not available on this system.";
            var blob = Encoding.Unicode.GetBytes(key);
            var handle = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, handle, blob.Length);
                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    Comment = "Fairlead client key",
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = handle,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = "fairlead",
                };
                if (!CredWrite(ref cred, 0)) return "Windows Credential Manager refused the key (error " + Marshal.GetLastWin32Error() + ").";
                return null;
            }
            catch (DllNotFoundException) { return "Windows Credential Manager is not available on this system."; }
            catch (EntryPointNotFoundException) { return "Windows Credential Manager is not available on this system."; }
            finally
            {
                Array.Clear(blob, 0, blob.Length);
                Marshal.FreeHGlobal(handle);
            }
        }
    }
}
