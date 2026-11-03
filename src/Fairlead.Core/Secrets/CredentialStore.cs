using System;
using System.Collections.Generic;
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

        /// <summary>Replaceable so tests never touch the real Credential Manager. Target names matching a filter such as "prefix*"; no secrets.</summary>
        public static Func<string, IList<string>> Lister = ListNative;

        /// <summary>Replaceable so tests never touch the real Credential Manager. The secret text of one generic credential, or null.</summary>
        public static Func<string, string> Reader = ReadNative;

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

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reserved, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        private const int ERROR_NOT_FOUND = 1168;

        /// <summary>Target names of the current user's generic credentials that match <paramref name="filter"/>. Throws when Credential Manager is unavailable.</summary>
        public static IList<string> List(string filter)
        {
            return Lister(filter);
        }

        /// <summary>The secret of the generic credential with this exact target, or null when there is none.</summary>
        public static string Read(string target)
        {
            return Reader(target);
        }

        private static IList<string> ListNative(string filter)
        {
            var names = new List<string>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new InvalidOperationException("Windows Credential Manager is not available on this system.");
            int count;
            IntPtr list;
            if (!CredEnumerate(filter, 0, out count, out list))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND) return names;
                throw new InvalidOperationException("Windows Credential Manager could not be listed (error " + err + ").");
            }
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    if (item == IntPtr.Zero) continue;
                    var cred = (CREDENTIAL)Marshal.PtrToStructure(item, typeof(CREDENTIAL));
                    if (cred.Type == CRED_TYPE_GENERIC && cred.TargetName != null) names.Add(cred.TargetName);
                }
            }
            finally { CredFree(list); }
            return names;
        }

        private static string ReadNative(string target)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            IntPtr ptr;
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out ptr))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND) return null;
                throw new InvalidOperationException("Windows Credential Manager could not be read (error " + err + ").");
            }
            try
            {
                var cred = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
                if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero) return string.Empty;
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                try { return Encoding.Unicode.GetString(bytes); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
            }
            finally { CredFree(ptr); }
        }

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
