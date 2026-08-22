using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Infrastructure.Windows.Gpu.Nvidia;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class NvidiaGameProfileCatalogTests
{
    [Theory]
    [InlineData(1, 0x00000008u)]
    [InlineData(3, 0x00000018u)]   // the value Profile Inspector shows for +3.0000
    [InlineData(15, 0x00000078u)]  // the owner's "no textures" value
    public void LodBias_UsesTheDirect3DEncodingOfEightUnitsPerStep(double steps, uint expected) =>
        NvidiaSettingValues.LodBias(steps).Should().Be(expected);

    [Fact]
    public void MegaFps_CarriesTheOwnerScreenshotValues()
    {
        var intents = Intents(GamePerformanceProfile.MegaFps).ToDictionary(x => x.SettingName, x => x.Value);

        intents[NvidiaSettingNames.TripleBuffering].Should().Be(0x00000001);
        intents[NvidiaSettingNames.TransparencySupersampling].Should().Be(0x00000008);
        intents[NvidiaSettingNames.AnisotropicMode].Should().Be(0x00000001);
        intents[NvidiaSettingNames.AnisotropicSetting].Should().Be(0x00000000);
        intents[NvidiaSettingNames.NegativeLodBias].Should().Be(0x00000000);
        intents[NvidiaSettingNames.LodBias].Should().Be(0x00000018);
        intents[NvidiaSettingNames.OpenGlLodBias].Should().Be(0x00000030);
    }

    [Fact]
    public void UltraPotato_KeepsEverySettingMegaFpsWrites()
    {
        var potato = Intents(GamePerformanceProfile.UltraPotato).Select(x => x.SettingName);

        potato.Should().Contain(Intents(GamePerformanceProfile.MegaFps).Select(x => x.SettingName));
    }

    [Fact]
    public void UltraPotato_PushesTheBiasToTheNoTexturesValue() =>
        Bias(GamePerformanceProfile.UltraPotato).Should().Be(NvidiaSettingValues.LodBiasNoTextures);

    /// <summary>
    /// The gates are the reason the rest of the profile counts: each releases an override written
    /// elsewhere, and a driver left free to ignore one silently discards the setting it guards. This is
    /// asserted by name rather than by count — the profile is meant to grow, and a count would only ever
    /// be updated to whatever the code now says.
    /// </summary>
    [Fact]
    public void UltraPotato_ReleasesEveryGateThatWouldOtherwiseDiscardItsOverrides()
    {
        var intents = Intents(GamePerformanceProfile.UltraPotato).ToDictionary(x => x.SettingName, x => x.Value);

        foreach (var gate in new[]
        {
            NvidiaSettingNames.DriverControlledLodBias,
            NvidiaSettingNames.NoAnisotropicOverride,
            NvidiaSettingNames.PredefinedFxaaUsage,
            NvidiaSettingNames.PredefinedAmbientOcclusionUsage,
            NvidiaSettingNames.AntialiasingBehaviorFlags,
            NvidiaSettingNames.VsyncBehaviorFlags
        })
            intents.Should().ContainKey(gate).WhoseValue.Should().Be(NvidiaSettingValues.GateReleased,
                "{0} decides whether an override this profile already writes is honoured", gate);
    }

    /// <summary>
    /// The milder profiles are the owner's own hand-tuned values, read out of their driver. Ultra Potato
    /// growing must never reach back into them.
    /// </summary>
    [Theory]
    [InlineData(GamePerformanceProfile.BalancedFps)]
    [InlineData(GamePerformanceProfile.Competitive)]
    [InlineData(GamePerformanceProfile.MegaFps)]
    public void TheMilderProfilesDoNotInheritUltraPotatoAdditions(GamePerformanceProfile profile)
    {
        var names = Intents(profile).Select(x => x.SettingName);

        names.Should().NotContain(NvidiaSettingNames.BatteryBoostApplicationFps);
        names.Should().NotContain(NvidiaSettingNames.GsyncGlobal);
        names.Should().NotContain(NvidiaSettingNames.DriverControlledLodBias);
        names.Should().NotContain(NvidiaSettingNames.EnableOverlay);
    }

    /// <summary>
    /// Ultra Potato exists to produce the largest number the machine can, and variable refresh holds the
    /// frame rate at the panel's rate. Switching it off is the deliberate trade; pinned so nobody
    /// "fixes" it back without meeting this sentence first.
    /// </summary>
    [Fact]
    public void UltraPotato_TurnsOffVariableRefreshDeliberately()
    {
        var intents = Intents(GamePerformanceProfile.UltraPotato).ToDictionary(x => x.SettingName, x => x.Value);

        intents[NvidiaSettingNames.VariableRefreshRate].Should().Be(NvidiaSettingValues.Off);
        intents[NvidiaSettingNames.GsyncGlobal].Should().Be(NvidiaSettingValues.Off);
    }

    /// <summary>A setting written twice is a setting whose final value depends on enumeration order.</summary>
    [Fact]
    public void NoProfileWritesTheSameSettingTwice()
    {
        foreach (var profile in Enum.GetValues<GamePerformanceProfile>())
            Intents(profile).Select(x => x.SettingName).Should().OnlyHaveUniqueItems("profile {0}", profile);
    }

    [Fact]
    public void BalancedFps_WritesTheOwnerHandTunedProfile()
    {
        var intents = Intents(GamePerformanceProfile.BalancedFps).ToDictionary(x => x.SettingName, x => x.Value);

        intents[NvidiaSettingNames.PowerManagementMode].Should().Be(0x00000001);
        intents[NvidiaSettingNames.MaximumPreRenderedFrames].Should().Be(0x00000001);
        intents[NvidiaSettingNames.VerticalSync].Should().Be(0x08416747);
        intents[NvidiaSettingNames.FrameRateLimiter].Should().Be(0x00000000);
        intents[NvidiaSettingNames.PreferredRefreshRate].Should().Be(0x00000001);
        intents[NvidiaSettingNames.VulkanOpenGlPresentMethod].Should().Be(0x00000002);
        intents[NvidiaSettingNames.TextureQuality].Should().Be(NvidiaSettingValues.TextureQualityHighPerformance);
    }

    [Fact]
    public void BalancedFps_KeepsTexturesSharpByWritingNoLodBias() =>
        Intents(GamePerformanceProfile.BalancedFps).Should().NotContain(x => x.SettingName == NvidiaSettingNames.LodBias);

    [Fact]
    public void EveryProfile_IncludesTheBalancedBaseline()
    {
        var baseline = Intents(GamePerformanceProfile.BalancedFps).Select(x => x.SettingName);

        foreach (var profile in Aggressive)
            Intents(profile).Select(x => x.SettingName).Should().Contain(baseline,
                $"{profile} is layered on top of the Balanced baseline");
    }

    [Fact]
    public void AProfileOverridesTheBaselineRatherThanWritingTheSettingTwice()
    {
        var competitive = Intents(GamePerformanceProfile.Competitive);

        competitive.Count(x => x.SettingName == NvidiaSettingNames.TextureQuality).Should().Be(1);
        competitive.Single(x => x.SettingName == NvidiaSettingNames.TextureQuality).Value
            .Should().Be(NvidiaSettingValues.TextureQualityPerformance, "the profile's own value wins over the baseline");
    }

    [Fact]
    public void BalancedBaseline_WritesNegativeLodBiasBeforeAnyProfileAddsABias()
    {
        var ultra = Intents(GamePerformanceProfile.UltraPotato).Select(x => x.SettingName).ToList();

        ultra.IndexOf(NvidiaSettingNames.NegativeLodBias)
            .Should().BeLessThan(ultra.IndexOf(NvidiaSettingNames.LodBias),
                "the driver ignores a bias while it is still clamping");
    }

    [Fact]
    public void Ladder_ReducesBlurAsTheProfileGetsLessAggressive()
    {
        Bias(GamePerformanceProfile.UltraPotato).Should().BeGreaterThan(Bias(GamePerformanceProfile.MegaFps));
        Bias(GamePerformanceProfile.MegaFps).Should().BeGreaterThan(Bias(GamePerformanceProfile.Competitive));
        Bias(GamePerformanceProfile.Competitive).Should().BeGreaterThan(0);
    }

    [Fact]
    public void EveryAggressiveProfile_AllowsNegativeLodBiasSoTheAdjustmentIsNotClampedAway()
    {
        foreach (var profile in Aggressive)
            Intents(profile).Should().Contain(x =>
                x.SettingName == NvidiaSettingNames.NegativeLodBias &&
                x.Value == NvidiaSettingValues.NegativeLodBiasAllow);
    }

    [Fact]
    public void EveryIntent_CarriesANameAndAReadableValue() =>
        Aggressive.SelectMany(Intents).Should().OnlyContain(x =>
            !string.IsNullOrWhiteSpace(x.SettingName) && !string.IsNullOrWhiteSpace(x.Display));

    [Fact]
    public void EveryIntent_TargetsADistinctSetting()
    {
        foreach (var profile in Enum.GetValues<GamePerformanceProfile>())
            Intents(profile).Select(x => x.SettingName).Should().OnlyHaveUniqueItems();
    }

    private static readonly GamePerformanceProfile[] Aggressive =
        [GamePerformanceProfile.UltraPotato, GamePerformanceProfile.MegaFps, GamePerformanceProfile.Competitive];

    private static IReadOnlyList<NvidiaSettingIntent> Intents(GamePerformanceProfile profile) =>
        NvidiaGameProfileCatalog.ForRoblox(profile);

    private static uint Bias(GamePerformanceProfile profile) =>
        Intents(profile).Single(x => x.SettingName == NvidiaSettingNames.LodBias).Value;
}
