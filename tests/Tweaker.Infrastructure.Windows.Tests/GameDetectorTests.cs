using FluentAssertions;
using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class GameDetectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-games", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Detect_PreservesUnicodePathAndFindsKnownConfigs()
    {
        var local = Path.Combine(root, "Пользователь", "Local");
        var roaming = Path.Combine(root, "Пользователь", "Roaming");
        var docs = Path.Combine(root, "Пользователь", "Documents");
        Directory.CreateDirectory(Path.Combine(local, "FortniteGame", "Saved", "Config", "WindowsClient"));
        File.WriteAllText(Path.Combine(local, "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"), "test");
        Directory.CreateDirectory(Path.Combine(roaming, ".minecraft"));
        File.WriteAllText(Path.Combine(roaming, ".minecraft", "options.txt"), "test");

        var games = new GameDetector(local, roaming, docs).Detect();

        games["Fortnite"].Installed.Should().BeTrue();
        games["Fortnite"].ConfigPath.Should().Contain("Пользователь");
        games["Minecraft"].Installed.Should().BeTrue();
        games["Roblox"].Installed.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
