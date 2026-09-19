using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Infrastructure.Windows.Games;
using Tweaker.Infrastructure.Windows.Gpu.Amd;
using Tweaker.Infrastructure.Windows.Gpu.Intel;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// The Intel and AMD ladders have to read like the NVIDIA one: each step keeps what the milder step
/// wrote and adds to it, nothing is written twice, and every row has a line the preview can show.
/// </summary>
public sealed class VendorCatalogTests
{
    private static readonly GameDriverTarget Fortnite = GameDriverTargets.Require("Fortnite");

    [Fact]
    public void IntelLadder_GrowsFromBalancedToUltraPotato()
    {
        var balanced = IntelGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.BalancedFps).Select(x => x.Feature);
        var competitive = IntelGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.Competitive).Select(x => x.Feature);
        var mega = IntelGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.MegaFps).Select(x => x.Feature);
        var potato = IntelGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.UltraPotato).Select(x => x.Feature);

        competitive.Should().Contain(balanced);
        mega.Should().Contain(competitive);
        potato.Should().Contain(mega);
        potato.Should().Contain(Ctl3DFeature.Msaa).And.Contain(Ctl3DFeature.AdaptiveTessellation);
    }

    [Fact]
    public void IntelUltraPotato_TradesFilteringForFrames()
    {
        var rows = IntelGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.UltraPotato).ToDictionary(x => x.Feature);

        rows[Ctl3DFeature.TextureFilteringQuality].Raw.Should().Be(CtlValues.TextureFilteringPerformance);
        rows[Ctl3DFeature.Msaa].Raw.Should().Be(CtlValues.MsaaDisabled);
        rows[Ctl3DFeature.GamingFlipModes].Raw.Should().Be(CtlValues.FlipModeVsyncOff);
        rows[Ctl3DFeature.FrameLimit].Enable.Should().BeFalse();
        rows[Ctl3DFeature.EnduranceGaming].Enable.Should().BeFalse();
    }

    [Fact]
    public void IntelBalanced_KeepsTextureQualityAsTheGameSetsIt()
    {
        var rows = IntelGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.BalancedFps).ToDictionary(x => x.Feature);

        rows[Ctl3DFeature.TextureFilteringQuality].Raw.Should().Be(CtlValues.TextureFilteringBalanced);
        rows.Should().NotContainKey(Ctl3DFeature.Msaa);
    }

    [Fact]
    public void AmdLadder_GrowsFromBalancedToUltraPotato()
    {
        var balanced = AmdGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.BalancedFps).Select(x => x.Setting);
        var potato = AmdGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.UltraPotato).Select(x => x.Setting);

        potato.Should().Contain(balanced);
        potato.Should().Contain(AdlxSetting.RadeonSuperResolution).And.Contain(AdlxSetting.MorphologicalAntiAliasing);
    }

    [Fact]
    public void AmdNeverTurnsBoostOnBecauseItChangesResolution()
    {
        foreach (var profile in Enum.GetValues<GamePerformanceProfile>())
            AmdGameProfileCatalog.ForGame(Fortnite, profile).Single(x => x.Setting == AdlxSetting.Boost).Value
                .Should().Be(AdlxValues.Off, "{0}", profile);
    }

    [Fact]
    public void AmdUltraPotato_TurnsVsyncAlwaysOff()
    {
        AmdGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.UltraPotato)
            .Single(x => x.Setting == AdlxSetting.WaitForVerticalRefresh).Value.Should().Be(AdlxValues.WaitForVerticalRefreshAlwaysOff);
        AmdGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.BalancedFps)
            .Single(x => x.Setting == AdlxSetting.WaitForVerticalRefresh).Value.Should().Be(AdlxValues.WaitForVerticalRefreshOffUnlessAppSpecifies);
    }

    [Fact]
    public void NoVendorProfileWritesTheSameSettingTwiceAndEveryRowHasALine()
    {
        foreach (var profile in Enum.GetValues<GamePerformanceProfile>())
        {
            var intel = IntelGameProfileCatalog.ForGame(Fortnite, profile);
            intel.Select(x => x.Feature).Should().OnlyHaveUniqueItems("Intel {0}", profile);
            intel.Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x.Display));
            var amd = AmdGameProfileCatalog.ForGame(Fortnite, profile);
            amd.Select(x => x.Setting).Should().OnlyHaveUniqueItems("AMD {0}", profile);
            amd.Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x.Display));
        }
    }

    [Fact]
    public void EveryGameHasTheSameVendorLadderAsFortnite()
    {
        foreach (var target in GameDriverTargets.All)
        {
            IntelGameProfileCatalog.ForGame(target, GamePerformanceProfile.UltraPotato)
                .Should().Equal(IntelGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.UltraPotato));
            AmdGameProfileCatalog.ForGame(target, GamePerformanceProfile.UltraPotato)
                .Should().Equal(AmdGameProfileCatalog.ForGame(Fortnite, GamePerformanceProfile.UltraPotato));
        }
    }

    [Theory]
    [InlineData("Fortnite")]
    [InlineData("Valorant")]
    [InlineData("GTA V")]
    [InlineData("Minecraft")]
    [InlineData("Roblox")]
    public void TheClientPlanDescribesEveryGameAndTellsUltraPotatoFromBalanced(string game)
    {
        var balanced = GameClientPlan.Describe(game, GamePerformanceProfile.BalancedFps);
        var potato = GameClientPlan.Describe(game, GamePerformanceProfile.UltraPotato);

        balanced.Should().NotBeEmpty();
        potato.Should().NotBeEmpty();
        potato.Should().NotEqual(balanced, "the preview has to show that the profiles differ");
    }

    [Fact]
    public void TheFortnitePlanNamesTheRenderScaleTheTransformerWrites() =>
        GameClientPlan.Describe("Fortnite", GamePerformanceProfile.UltraPotato).Should().Contain("Render scale: 50%");
}
