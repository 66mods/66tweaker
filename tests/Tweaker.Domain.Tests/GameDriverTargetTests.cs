using FluentAssertions;
using Tweaker.Domain.Games;

namespace Tweaker.Domain.Tests;

/// <summary>
/// The executables every vendor keys its driver profile on. Pinned here because a typo in one of them
/// would write a profile the game never matches, silently, on every vendor at once.
/// </summary>
public sealed class GameDriverTargetTests
{
    [Fact]
    public void EverySupportedGameHasADriverTarget()
    {
        foreach (var game in GameProfileCatalog.Create().Keys)
            GameDriverTargets.Find(game).Should().NotBeNull("{0} has a Games card and so needs a driver target", game);
    }

    [Theory]
    [InlineData("Fortnite", "FortniteClient-Win64-Shipping.exe")]
    [InlineData("Valorant", "VALORANT-Win64-Shipping.exe")]
    [InlineData("GTA V", "GTA5.exe")]
    [InlineData("Minecraft", "javaw.exe")]
    [InlineData("Roblox", "RobloxPlayerBeta.exe")]
    public void TheProfileIsLookedUpByTheGameOwnExecutable(string game, string executable) =>
        GameDriverTargets.Require(game).Executables[0].Should().Be(executable);

    [Fact]
    public void GtaVCoversBothTheLegacyAndTheEnhancedBuild() =>
        GameDriverTargets.Require("GTA V").Executables.Should().Equal("GTA5.exe", "GTA5_Enhanced.exe");

    [Fact]
    public void MinecraftWarnsThatJavawIsEveryJavaProgram() =>
        GameDriverTargets.Require("Minecraft").Caveat.Should().Contain("javaw.exe").And.Contain("every Java program");

    [Fact]
    public void OnlyMinecraftCarriesACaveat() =>
        GameDriverTargets.All.Where(x => x.Caveat is not null).Select(x => x.Game).Should().Equal("Minecraft");

    [Fact]
    public void SlugsAreSafeForOperationIdsAndFileNames()
    {
        foreach (var target in GameDriverTargets.All)
        {
            target.Slug.Should().MatchRegex("^[a-z0-9-]+$");
            target.Slug.Should().Be(target.Game.ToLowerInvariant().Replace(' ', '-'));
        }
        GameDriverTargets.Require("Roblox").Slug.Should().Be("roblox", "the 1.1 operation id and baseline file are keyed on it");
    }

    [Fact]
    public void EveryExecutableIsAFileNameNotAPath()
    {
        foreach (var executable in GameDriverTargets.All.SelectMany(x => x.Executables))
        {
            executable.Should().EndWith(".exe");
            executable.Should().NotContain("\\").And.NotContain("/");
        }
    }

    [Fact]
    public void LookupIgnoresCase() => GameDriverTargets.Find("gta v").Should().NotBeNull();

    [Fact]
    public void RequireFailsLoudlyForAnUnknownGame() =>
        FluentActions.Invoking(() => GameDriverTargets.Require("Some Future Game")).Should().Throw<ArgumentException>();
}
