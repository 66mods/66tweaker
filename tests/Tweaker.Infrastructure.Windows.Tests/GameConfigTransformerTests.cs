using System.Xml.Linq;
using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Infrastructure.Windows.Games;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class GameConfigTransformerTests
{
    [Fact]
    public void FortniteUltraPotato_PreservesResolutionAndReducesInternalQuality()
    {
        const string input = "[/Script/FortniteGame.FortGameUserSettings]\nResolutionSizeX=1920\nResolutionSizeY=1080\nsg.ResolutionQuality=100\nsg.ShadowQuality=3\nFrameRateLimit=144.000000\n";

        var output = new UnrealIniTransformer().Transform(input, "Fortnite", GamePerformanceProfile.UltraPotato);

        output.Should().Contain("ResolutionSizeX=1920").And.Contain("ResolutionSizeY=1080");
        output.Should().Contain("sg.ResolutionQuality=50").And.Contain("sg.ShadowQuality=0");
        output.Should().Contain("FrameRateLimit=144.000000");
    }

    [Fact]
    public void ValorantMegaFps_PreservesResolutionAndSetsQualityZero()
    {
        const string input = "[/Script/ShooterGame.ShooterGameUserSettings]\nResolutionSizeX=2560\nResolutionSizeY=1440\nsg.TextureQuality=2\nsg.EffectsQuality=2\n";
        var output = new UnrealIniTransformer().Transform(input, "Valorant", GamePerformanceProfile.MegaFps);
        output.Should().Contain("ResolutionSizeX=2560").And.Contain("ResolutionSizeY=1440");
        output.Should().Contain("sg.TextureQuality=0").And.Contain("sg.EffectsQuality=0");
    }

    [Fact]
    public void GtaUltraPotato_PreservesScreenDimensionsAndReducesGraphics()
    {
        const string input = "<Settings><video><ScreenWidth value=\"1920\"/><ScreenHeight value=\"1080\"/></video><graphics><ShadowQuality value=\"2\"/><TextureQuality value=\"2\"/><PopulationDensity value=\"1.000000\"/></graphics></Settings>";
        var output = new GtaXmlTransformer().Transform(input, GamePerformanceProfile.UltraPotato);
        var xml = XDocument.Parse(output);
        xml.Descendants("ScreenWidth").Single().Attribute("value")!.Value.Should().Be("1920");
        xml.Descendants("ScreenHeight").Single().Attribute("value")!.Value.Should().Be("1080");
        xml.Descendants("ShadowQuality").Single().Attribute("value")!.Value.Should().Be("0");
        xml.Descendants("PopulationDensity").Single().Attribute("value")!.Value.Should().Be("0.000000");
    }

    [Fact]
    public void MinecraftUltraPotato_PreservesFullscreenResolutionAndReducesDistance()
    {
        const string input = "fullscreenResolution:1920x1080@144\nrenderDistance:16\nsimulationDistance:12\ngraphicsMode:1\nparticles:0\nclouds:true\n";
        var output = new MinecraftOptionsTransformer().Transform(input, GamePerformanceProfile.UltraPotato);
        output.Should().Contain("fullscreenResolution:1920x1080@144");
        output.Should().Contain("renderDistance:4").And.Contain("simulationDistance:4");
        output.Should().Contain("clouds:false");
    }

    [Fact]
    public void RobloxProfile_DoesNotEmitUnsupportedFastFlags()
    {
        var plan = new RobloxProfilePlanner().Create(GamePerformanceProfile.UltraPotato);
        plan.AutomatedChanges.Should().BeEmpty();
        plan.ManualSteps.Should().Contain(x => x.Contains("Graphics Mode", StringComparison.Ordinal));
        plan.Warnings.Should().Contain(x => x.Contains("FastFlags", StringComparison.Ordinal));
    }
}
