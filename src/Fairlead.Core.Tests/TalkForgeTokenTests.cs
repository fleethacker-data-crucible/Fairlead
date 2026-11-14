using System;
using System.IO;
using Fairlead.Core.Config;
using Xunit;

namespace Fairlead.Core.Tests
{
    public sealed class TalkForgeTokenTests : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "fairlead-talkforge-" + Guid.NewGuid().ToString("N"));
        private readonly string _oldFile;
        private readonly string _oldSwitch;

        public TalkForgeTokenTests()
        {
            _oldFile = Environment.GetEnvironmentVariable(TalkForgeLocalToken.FileEnv);
            _oldSwitch = Environment.GetEnvironmentVariable(TalkForgeLocalToken.SwitchEnv);
            Environment.SetEnvironmentVariable(TalkForgeLocalToken.FileEnv, _file);
            Environment.SetEnvironmentVariable(TalkForgeLocalToken.SwitchEnv, null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(TalkForgeLocalToken.FileEnv, _oldFile);
            Environment.SetEnvironmentVariable(TalkForgeLocalToken.SwitchEnv, _oldSwitch);
            try { if (File.Exists(_file)) File.Delete(_file); } catch (IOException) { }
        }

        [Fact]
        public void TalkForgeIsALocalSpeechPresetOnPort25009()
        {
            var preset = ProviderPresets.Find("talkforge");
            Assert.NotNull(preset);
            Assert.True(preset.Local);
            Assert.False(preset.NeedsKey);
            Assert.Equal("http://127.0.0.1:25009/v1", preset.BaseUrl);
            var created = ProviderPresets.Create("talkforge");
            Assert.Equal("talkforge", created.Preset);
            Assert.True(TalkForgeLocalToken.Applies(created));
        }

        [Fact]
        public void AMissingKeyUsesTheLocalTokenFileAndARealKeyWins()
        {
            File.WriteAllText(_file, "talkforge-local-token-value\n");
            var talk = ProviderPresets.Create("talkforge");
            Assert.Equal("talkforge-local-token-value", SecretProtector.ApiKeyFor(talk));
            Assert.Equal("local-token", SecretProtector.KeySource(talk));

            talk.ApiKeyEnv = "TALKFORGE_TEST_KEY";
            Environment.SetEnvironmentVariable("TALKFORGE_TEST_KEY", "from-env-not-the-file");
            try
            {
                Assert.Equal("from-env-not-the-file", SecretProtector.ApiKeyFor(talk));
                Assert.Equal("env", SecretProtector.KeySource(talk));
            }
            finally
            {
                Environment.SetEnvironmentVariable("TALKFORGE_TEST_KEY", null);
            }

            var other = ProviderPresets.Create("ollama");
            Assert.Null(SecretProtector.ApiKeyFor(other));
            Assert.Equal("none", SecretProtector.KeySource(other));
        }

        [Fact]
        public void AShortTokenAndTheOffSwitchAreIgnored()
        {
            File.WriteAllText(_file, "too-short\n");
            var talk = ProviderPresets.Create("talkforge");
            Assert.Null(TalkForgeLocalToken.KeyFor(talk));
            File.WriteAllText(_file, "talkforge-local-token-value\n");
            Environment.SetEnvironmentVariable(TalkForgeLocalToken.SwitchEnv, "off");
            Assert.Null(TalkForgeLocalToken.Read());
            Assert.Equal("none", SecretProtector.KeySource(talk));
        }

        [Fact]
        public void ACustomBaseUrlOnPort25009CountsAsTalkForge()
        {
            File.WriteAllText(_file, "talkforge-local-token-value\n");
            var custom = new ProviderConfig { Id = "voice", Preset = "custom", BaseUrl = "http://127.0.0.1:25009/v1", Kind = ProviderKind.OpenAi, Local = true };
            Assert.Equal("talkforge-local-token-value", SecretProtector.ApiKeyFor(custom));
        }
    }
}
