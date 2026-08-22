using FluentAssertions;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class LegacyBundleCategoryTests
{
    [Theory]
    [InlineData("fortnite", LegacyEffectCategory.Gaming)]
    [InlineData("valorant", LegacyEffectCategory.Gaming)]
    [InlineData("gta5", LegacyEffectCategory.Gaming)]
    [InlineData("mouse", LegacyEffectCategory.Gaming)]
    [InlineData("realinputdelay", LegacyEffectCategory.Gaming)]
    [InlineData("internet", LegacyEffectCategory.Network)]
    [InlineData("lowerping", LegacyEffectCategory.Network)]
    [InlineData("nvidia", LegacyEffectCategory.Gpu)]
    [InlineData("amd", LegacyEffectCategory.Gpu)]
    [InlineData("windowstweaks", LegacyEffectCategory.Windows)]
    [InlineData("power", LegacyEffectCategory.Windows)]
    [InlineData("", LegacyEffectCategory.Windows)]
    [InlineData("a-section-nobody-has-seen", LegacyEffectCategory.Windows)]
    public void CategoryOf_MapsEverySectionToExactlyOneVisibleCategory(string section, LegacyEffectCategory expected) =>
        LegacyBundleOperation.CategoryOf(section).Should().Be(expected);

    [Fact]
    public void CategoryBreakdown_AlwaysPublishesAllFourCategoriesInAFixedOrder()
    {
        foreach (var operation in Bundles())
            operation.CategoryBreakdown.Select(x => x.Category).Should().Equal(
                LegacyEffectCategory.Windows, LegacyEffectCategory.Gaming,
                LegacyEffectCategory.Network, LegacyEffectCategory.Gpu);
    }

    [Fact]
    public void CategoryBreakdown_IsExhaustiveSoNoEffectIsSilentlyDropped()
    {
        foreach (var operation in Bundles())
            operation.CategoryBreakdown.Sum(x => x.Count).Should().Be(operation.CanonicalEffectCount,
                $"the {operation.Descriptor.Name} breakdown must account for every selected effect");
    }

    [Fact]
    public void CategoryBreakdown_MatchesTheAuditedGamingProfileTotals()
    {
        var gaming = Bundles().Single(x => x.Profile == LegacyBundleProfile.Gaming);

        gaming.CategoryBreakdown.Select(x => x.Count).Should().Equal(225, 464, 124, 327);
    }

    [Theory]
    [InlineData(0, "0 changes")]
    [InlineData(1, "1 change")]
    [InlineData(327, "327 changes")]
    public void CountLabel_UsesSingularOnlyForOne(int count, string expected) =>
        new LegacyEffectCategoryCount(LegacyEffectCategory.Windows, count).CountLabel.Should().Be(expected);

    [Fact]
    public void DisplayName_KeepsTheGpuAcronymUppercase() =>
        new LegacyEffectCategoryCount(LegacyEffectCategory.Gpu, 1).DisplayName.Should().Be("GPU");

    private static IReadOnlyList<LegacyBundleOperation> Bundles() =>
        LegacyBundleOperation.CreateAll(new FixedProcessRunner()).Cast<LegacyBundleOperation>().ToArray();
}
