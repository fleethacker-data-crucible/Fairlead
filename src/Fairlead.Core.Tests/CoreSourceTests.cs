using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fairlead.Core.Host;
using Fairlead.Core.Serialization;
using Xunit;

namespace Fairlead.Core.Tests
{
    /// <summary>
    /// The core ships as source compiled by Add-Type on the workstation, where application control blocks loading any
    /// custom DLL. These checks keep the source loadable there; the CI step build\Test-FairleadAddType.ps1 compiles it
    /// with the real Windows PowerShell 5.1 compiler.
    /// </summary>
    public class CoreSourceTests
    {
        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Fairlead.sln"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("Fairlead.sln not found above " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static string[] CoreFiles()
        {
            var dir = Path.Combine(RepoRoot(), "src", "Fairlead.Core");
            return Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToArray();
        }

        [Fact]
        public void VersionMatchesTheProjectFile()
        {
            var csproj = XDocument.Load(Path.Combine(RepoRoot(), "src", "Fairlead.Core", "Fairlead.Core.csproj"));
            var version = csproj.Descendants("Version").Single().Value.Trim();
            Assert.Equal(FairleadHost.Version, version);
        }

        [Fact]
        public void LoadsNothingFromDisk()
        {
            // Loading an assembly by path (or bytes) is exactly what the workstation policy blocks; third-party
            // packages would need their DLLs next to the core.
            var forbidden = new Regex(@"\bAssembly\s*\.\s*(Load|LoadFrom|LoadFile|UnsafeLoadFrom|ReflectionOnlyLoad)\b|\bNativeLibrary\b|\bAppDomain\s*\.\s*CurrentDomain\s*\.\s*Load\b|\bNewtonsoft\b|#nullable");
            var hits = CoreFiles().SelectMany(f => File.ReadAllLines(f).Select((l, i) => new { f, i, l }))
                .Where(x => !x.l.TrimStart().StartsWith("//") && forbidden.IsMatch(x.l))
                .Select(x => Path.GetFileName(x.f) + ":" + (x.i + 1) + ": " + x.l.Trim()).ToList();
            Assert.True(hits.Count == 0, string.Join("\n", hits));
        }

        [Fact]
        public void SourceIsAscii()
        {
            // Without a BOM, the .NET Framework compiler behind Add-Type may read a file as ANSI and change string literals.
            var hits = CoreFiles().Where(f => File.ReadAllBytes(f).Any(b => b > 127)).Select(Path.GetFileName).ToList();
            Assert.True(hits.Count == 0, string.Join(", ", hits));
        }
    }

    /// <summary>The JSON reader parses untrusted request bodies and provider replies: it must fail cleanly.</summary>
    public class JsonReaderHardeningTests
    {
        [Fact]
        public void DeepNestingIsRejectedNotAStackOverflow()
        {
            var ok = new string('[', 500) + new string(']', 500);
            Assert.Equal(JTokenType.Array, JToken.Parse(ok).Type);
            foreach (var open in new[] { '[', '{' })
            {
                var deep = open == '[' ? new string('[', 100000) : string.Concat(Enumerable.Repeat("{\"a\":", 100000));
                Assert.Throws<JsonException>(() => JToken.Parse(deep));
            }
        }

        [Fact]
        public void MalformedInputThrowsJsonExceptionOnly()
        {
            foreach (var bad in new[] { "", "{", "[1,", "{\"a\"", "{\"a\":}", "\"unterminated", "[\"\\u12\"]", "[\"\\q\"]", "tru", "nul", "{} x", "[1]]", "-", "1e", "{'a':1", "/* open" })
                Assert.Throws<JsonException>(() => JToken.Parse(bad));
        }

        [Fact]
        public void BadRequestBodiesAre400s()
        {
            foreach (var bad in new[] { new string('[', 100000), "{\"a\":" + new string('1', 5000) + "}x", "{\"x\":\"\\ud800\"" })
            {
                var r = new HttpRequest { Body = System.Text.Encoding.UTF8.GetBytes(bad) };
                Assert.Throws<BadRequestException>(() => Json.Body(r));
            }
        }
    }
}
