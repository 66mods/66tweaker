using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class RobloxDetectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-roblox", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Detect_WithoutAnyLauncherReportsNothing() =>
        new RobloxDetector(root).Detect().Should().BeNull();

    [Theory]
    [InlineData("Roblox", RobloxLauncherKind.Official)]
    [InlineData("Bloxstrap", RobloxLauncherKind.Bloxstrap)]
    [InlineData("Fishstrap", RobloxLauncherKind.Fishstrap)]
    [InlineData("Froststrap", RobloxLauncherKind.Froststrap)]
    public void Detect_FindsThePlayerUnderEverySupportedLauncher(string folder, RobloxLauncherKind expected)
    {
        var executable = Player(folder, "version-abc");

        var result = new RobloxDetector(root).Detect();

        result.Should().NotBeNull();
        result!.Launcher.Should().Be(expected);
        result.ExecutablePath.Should().Be(executable);
    }

    [Fact]
    public void Detect_UsesTheOfficialInstallWhenACommunityLauncherIsAlsoPresent()
    {
        Player("Fishstrap", "version-fish");
        var official = Player("Roblox", "version-official");

        new RobloxDetector(root).Detect()!.ExecutablePath.Should().Be(official);
    }

    [Fact]
    public void Detect_IgnoresAVersionFolderWithoutThePlayerExecutable()
    {
        Directory.CreateDirectory(Path.Combine(root, "Fishstrap", "Versions", "version-empty"));

        new RobloxDetector(root).Detect().Should().BeNull();
    }

    [Fact]
    public void Detect_PrefersTheNewestVersionFolder()
    {
        var older = Player("Fishstrap", "version-old");
        var newer = Player("Fishstrap", "version-new");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-5));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        new RobloxDetector(root).Detect()!.ExecutablePath.Should().Be(newer);
    }

    [Fact]
    public void Detect_SharesTheOfficialStableSettingsFileAcrossLaunchers()
    {
        Player("Fishstrap", "version-abc");

        new RobloxDetector(root).Detect()!.StableSettingsPath
            .Should().Be(Path.Combine(root, "Roblox", "GlobalBasicSettings_13.xml"));
    }

    [Theory]
    [InlineData("Bloxstrap", "Bloxstrap/Modifications/ClientSettings/ClientAppSettings.json")]
    [InlineData("Fishstrap", "Fishstrap/Modifications/ClientSettings/ClientAppSettings.json")]
    [InlineData("Froststrap", "Froststrap/ClientSettings/ClientAppSettings.json")]
    [InlineData("Roblox", "Roblox/ClientSettings/ClientAppSettings.json")]
    public void Detect_UsesTheFastFlagLayoutOfTheLauncherItFound(string folder, string relative)
    {
        Player(folder, "version-abc");

        new RobloxDetector(root).Detect()!.FastFlagsPath
            .Should().Be(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void LauncherName_IsTheLauncherTheUserActuallyRuns() =>
        new RobloxInstallation(RobloxLauncherKind.Fishstrap, "a", "b", "c", "d").LauncherName.Should().Be("Fishstrap");

    private string Player(string folder, string version)
    {
        var directory = Path.Combine(root, folder, "Versions", version);
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "RobloxPlayerBeta.exe");
        File.WriteAllText(executable, "stub");
        return executable;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
