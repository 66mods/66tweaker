using FluentAssertions;
using Tweaker.App.ViewModels;

namespace Tweaker.App.Tests;

public sealed class GameCardPresentationTests
{
    [Theory]
    [InlineData("Roblox", "Icon.GameRoblox", "GameRobloxBrush")]
    [InlineData("Valorant", "Icon.GameValorant", "GameValorantBrush")]
    [InlineData("GTA V", "Icon.GameGta", "GameGtaBrush")]
    [InlineData("Minecraft", "Icon.GameMinecraft", "GameMinecraftBrush")]
    [InlineData("Fortnite", "Icon.GameFortnite", "GameFortniteBrush")]
    public void DetectedGame_UsesItsOwnMarkAndBrandAccent(string name, string icon, string accent)
    {
        var card = new GameCardViewModel(name, "Detected", ["Balanced FPS"]);

        card.IconKey.Should().Be(icon);
        card.AccentKey.Should().Be(accent);
        card.IsDetected.Should().BeTrue();
        card.StatusKind.Should().Be("Success");
    }

    [Fact]
    public void UndetectedGame_StaysMutedSoColourOnlyMeansAvailableHere()
    {
        var card = new GameCardViewModel("Valorant", "Not detected", ["Balanced FPS"]);

        card.IconKey.Should().Be("Icon.GameValorant");
        card.AccentKey.Should().Be("DisabledBrush");
        card.IsDetected.Should().BeFalse();
        card.StatusKind.Should().Be("Muted");
    }

    [Fact]
    public void UnknownGame_FallsBackToTheGenericMark() =>
        new GameCardViewModel("Some Future Game", "Detected", []).IconKey.Should().Be("Icon.Games");

    [Theory]
    [InlineData(1, "1 profile")]
    [InlineData(4, "4 profiles")]
    public void Subtitle_UsesSingularOnlyForOneProfile(int count, string expected) =>
        new GameCardViewModel("Valorant", "Detected", Enumerable.Repeat("p", count).ToArray())
            .Subtitle.Should().Be(expected);

    [Fact]
    public void ADetectedGameGetsItsBrandBackdropAndAnInstalledLabel()
    {
        var card = new GameCardViewModel("Valorant", "Detected", ["Balanced", "Competitive"]);

        card.BackdropKey.Should().Be("GameValorantWashBrush");
        card.AccentKey.Should().Be("GameValorantBrush");
        card.StatusText.Should().Be("Detected");
        card.ProfileCountText.Should().Be("2 profiles");
    }

    [Fact]
    public void AnUndetectedGameStaysNeutralSoColourKeepsMeaningInstalled()
    {
        var card = new GameCardViewModel("Valorant", "Not detected", ["Balanced"]);

        card.BackdropKey.Should().Be("SurfaceBrush");
        card.AccentKey.Should().Be("DisabledBrush");
        card.StatusText.Should().Be("Not installed");
        card.ProfileCountText.Should().Be("1 profile");
    }

    [Theory]
    [InlineData("Roblox", "GameRobloxWashBrush")]
    [InlineData("GTA V", "GameGtaWashBrush")]
    [InlineData("Minecraft", "GameMinecraftWashBrush")]
    [InlineData("Fortnite", "GameFortniteWashBrush")]
    public void EveryShippedGameHasItsOwnBackdrop(string name, string expected)
    {
        new GameCardViewModel(name, "Detected", ["Balanced"]).BackdropKey.Should().Be(expected);
    }
}
