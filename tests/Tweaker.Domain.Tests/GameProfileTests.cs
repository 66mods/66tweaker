using FluentAssertions;
using Tweaker.Domain.Games;

namespace Tweaker.Domain.Tests;

public sealed class GameProfileTests
{
    [Theory]
    [InlineData(GamePerformanceProfile.BalancedFps, 100)]
    [InlineData(GamePerformanceProfile.Competitive, 90)]
    [InlineData(GamePerformanceProfile.MegaFps, 70)]
    [InlineData(GamePerformanceProfile.UltraPotato, 50)]
    public void RenderScalePolicy_UsesApprovedValues(GamePerformanceProfile profile, int expected)
    {
        GameProfilePolicy.RenderScale(profile).Should().Be(expected);
    }

    [Theory]
    [InlineData("ResolutionSizeX")]
    [InlineData("ResolutionSizeY")]
    [InlineData("ScreenWidth")]
    [InlineData("ScreenHeight")]
    [InlineData("VideoMode")]
    public void ProtectedKeys_RecognizeOutputResolution(string key)
    {
        GameProfilePolicy.IsProtectedResolutionKey(key).Should().BeTrue();
    }

    [Fact]
    public void Catalog_ContainsEveryApprovedGameAndProfile()
    {
        var catalog = GameProfileCatalog.Create();

        catalog.Keys.Should().BeEquivalentTo("Fortnite", "Valorant", "GTA V", "Minecraft", "Roblox");
        catalog.Values.Should().OnlyContain(x => Enum.GetValues<GamePerformanceProfile>().All(x.Profiles.Contains));
    }
}
