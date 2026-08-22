using System.Xml.Linq;
using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Infrastructure.Windows.Games;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// The Roblox client's own settings are the layer the driver profile cannot reach, so these pin the two
/// things that make it safe to write: it changes only properties the file already has, and it never
/// touches the ones that belong to the player rather than to a performance profile.
///
/// The fixture is the real shape of <c>GlobalBasicSettings_13.xml</c> — properties are elements under
/// <c>Properties</c> carrying a <c>name</c> attribute, with the value as element text, and the type in the
/// element name. A test written against a guessed shape would pass while the shipped code did nothing.
/// </summary>
public sealed class RobloxSettingsTransformerTests
{
    private const string Fixture = """
        <roblox version="4">
          <Item class="UserGameSettings" referent="RBX0">
            <Properties>
              <int name="FramerateCap">240</int>
              <bool name="Fullscreen">true</bool>
              <int name="GraphicsQualityLevel">3</int>
              <float name="MasterVolume">0.3</float>
              <bool name="MaxQualityEnabled">false</bool>
              <bool name="MicroProfilerWebServerEnabled">true</bool>
              <bool name="OnScreenProfilerEnabled">true</bool>
              <bool name="PerformanceStatsVisible">true</bool>
              <bool name="PlayerNamesEnabled">true</bool>
              <bool name="PlayerListVisible">true</bool>
              <bool name="BadgeVisible">true</bool>
              <bool name="ReducedMotion">false</bool>
              <token name="SavedQualityLevel">1</token>
              <Vector2 name="StartScreenSize"><X>1440</X><Y>1440</Y></Vector2>
              <bool name="VignetteEnabled">true</bool>
              <bool name="VignetteEnabledCustomOption">true</bool>
            </Properties>
          </Item>
        </roblox>
        """;

    [Theory]
    [InlineData(GamePerformanceProfile.BalancedFps, "7")]
    [InlineData(GamePerformanceProfile.Competitive, "4")]
    [InlineData(GamePerformanceProfile.MegaFps, "2")]
    [InlineData(GamePerformanceProfile.UltraPotato, "1")]
    public void TheQualityLevelFallsWithTheProfile(GamePerformanceProfile profile, string expected)
    {
        var result = Read(profile);

        result["GraphicsQualityLevel"].Should().Be(expected);
        // The client re-derives one from the other, so a disagreement here undoes the change on next launch.
        result["SavedQualityLevel"].Should().Be(expected, "the saved level has to agree with the slider");
    }

    [Fact]
    public void ResolutionAndTheOtherSettingsThatAreNotOursAreLeftExactlyAsTheyWere()
    {
        var result = Read(GamePerformanceProfile.UltraPotato);

        // The rule GameProfilePolicy states for every other game holds here too.
        result["FramerateCap"].Should().Be("240", "capping the frame rate is not this profile's decision");
        result["Fullscreen"].Should().Be("true");
        result["MasterVolume"].Should().Be("0.3");
        // A Vector2 keeps its value in child elements, so this also proves nothing flattened it to text.
        XDocument.Parse(new RobloxSettingsTransformer().Transform(Fixture, GamePerformanceProfile.UltraPotato))
            .Descendants().Single(x => (string?)x.Attribute("name") == "StartScreenSize")
            .Element("X")!.Value.Should().Be("1440");
    }

    [Fact]
    public void UltraPotatoTakesTheInterfaceTradeAndTheMilderProfilesDoNot()
    {
        var potato = Read(GamePerformanceProfile.UltraPotato);
        var mega = Read(GamePerformanceProfile.MegaFps);

        potato["PlayerNamesEnabled"].Should().Be("false");
        potato["PlayerListVisible"].Should().Be("false");
        potato["ReducedMotion"].Should().Be("true");

        mega["PlayerNamesEnabled"].Should().Be("true", "hiding nameplates costs gameplay, not just image");
        mega["ReducedMotion"].Should().Be("false");
    }

    [Fact]
    public void InstrumentationIsSwitchedOffOnEveryProfileBecauseItCostsAndShowsNothing()
    {
        foreach (var profile in Enum.GetValues<GamePerformanceProfile>())
        {
            var result = Read(profile);
            result["PerformanceStatsVisible"].Should().Be("false", "profile {0}", profile);
            result["OnScreenProfilerEnabled"].Should().Be("false", "profile {0}", profile);
            // A background listener, not an overlay — this one is not merely cosmetic.
            result["MicroProfilerWebServerEnabled"].Should().Be("false", "profile {0}", profile);
        }
    }

    [Fact]
    public void APropertyThisClientVersionDoesNotHaveIsNotInvented()
    {
        // Roblox rewrites this file wholesale; an element it does not recognise is not a way to add a
        // setting, so the transformer must leave the document's property set exactly as it found it.
        const string sparse = """
            <roblox version="4"><Item class="UserGameSettings"><Properties>
              <int name="GraphicsQualityLevel">9</int>
            </Properties></Item></roblox>
            """;

        var document = XDocument.Parse(new RobloxSettingsTransformer().Transform(sparse, GamePerformanceProfile.UltraPotato));

        document.Descendants("Properties").Single().Elements().Should().ContainSingle()
            .Which.Attribute("name")!.Value.Should().Be("GraphicsQualityLevel");
        document.Descendants().Single(x => (string?)x.Attribute("name") == "GraphicsQualityLevel").Value.Should().Be("1");
    }

    [Fact]
    public void ThePreviewListsExactlyWhatTheWriteChanges()
    {
        // The player approves the preview, so a plan that under-reports is worse than one that writes less.
        var plan = RobloxSettingsTransformer.Plan(GamePerformanceProfile.UltraPotato);
        var result = Read(GamePerformanceProfile.UltraPotato);

        foreach (var change in plan)
            result[change.Property].Should().Be(change.Value, "the preview promised {0}", change.Display);
        plan.Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x.Display));
    }

    private static Dictionary<string, string> Read(GamePerformanceProfile profile) =>
        XDocument.Parse(new RobloxSettingsTransformer().Transform(Fixture, profile))
            .Descendants()
            .Where(x => x.Attribute("name") is not null)
            .ToDictionary(x => x.Attribute("name")!.Value, x => x.Value, StringComparer.Ordinal);
}
