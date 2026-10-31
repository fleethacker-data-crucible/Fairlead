using System.Linq;
using Fairlead.Core.Config;
using Xunit;

namespace Fairlead.Core.Tests
{
    public class PresetTests
    {
        [Fact]
        public void PhoenixLaiIsALocalPresetAtTheFleetDefaultAddress()
        {
            var preset = ProviderPresets.Find("phoenix-lai");
            Assert.NotNull(preset);
            Assert.True(preset!.Local);
            Assert.False(preset.NeedsKey);
            var p = ProviderPresets.Create("phoenix-lai");
            Assert.Equal("phoenix-lai", p.Id);
            Assert.Equal("http://127.0.0.1:1234/v1", p.BaseUrl);
            Assert.Equal(ProviderKind.OpenAi, p.Kind);
            Assert.True(p.Local);
        }

        [Fact]
        public void PresetIdsAreUniqueAndLocalPresetsNeedNoKey()
        {
            Assert.Equal(ProviderPresets.All.Count, ProviderPresets.All.Select(x => x.Id).Distinct().Count());
            Assert.All(ProviderPresets.All.Where(x => x.Local), x => Assert.False(x.NeedsKey));
        }

        [Fact]
        public void ACustomIdKeepsThePresetDefaults()
        {
            var p = ProviderPresets.Create("phoenix-lai", "gemma-desk");
            Assert.Equal("gemma-desk", p.Id);
            Assert.Equal("http://127.0.0.1:1234/v1", p.BaseUrl);
        }
    }
}
