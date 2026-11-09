using System;
using System.IO;
using Fairlead.Core.Gateway;
using Xunit;

namespace Fairlead.Core.Tests
{
    // The local token (Gateway.LocalToken): made once, private to this account, and only a file in a temp folder here.
    public class LocalTokenTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-local-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Fact]
        public void IsMadeOnceAndReadBackTheSame()
        {
            var path = Path.Combine(_dir, "sub", "local-token");
            Assert.Null(LocalToken.Read(path));
            var a = LocalToken.Ensure(path);
            Assert.StartsWith("fl-", a);
            Assert.True(a.Length >= 19);
            Assert.Equal(a, LocalToken.Ensure(path));
            Assert.Equal(a, LocalToken.Read(path));
        }

        [Fact]
        public void OnWindowsOnlyThisAccountAndSystemMayReadIt()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            var path = Path.Combine(_dir, "local-token");
            LocalToken.Ensure(path);
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe"), "\"" + path + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            string acl;
            using (var p = System.Diagnostics.Process.Start(psi)) { acl = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            Assert.DoesNotContain("(I)", acl);
            Assert.DoesNotContain("Everyone", acl);
            Assert.DoesNotContain("BUILTIN\\Users", acl);
            Assert.Contains("SYSTEM", acl);
        }

        [Fact]
        public void IgnoresAFileThatIsNotOneOfOurKeys()
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "local-token");
            File.WriteAllText(path, "not-a-key\n");
            Assert.Null(LocalToken.Read(path));
        }

        [Fact]
        public void AppNamesAreKeptSafe()
        {
            Assert.Equal("Semaphore", LocalToken.AppName("Semaphore"));
            Assert.Equal("Local apps", LocalToken.AppName(null));
            Assert.Equal("Local apps", LocalToken.AppName("<script>"));
            Assert.Equal("Local apps", LocalToken.AppName(new string('a', 60)));
        }
    }
}
