using FluentAssertions;
using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class ValorantGameDetectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-valorant", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Detect_FindsNewestAccountGameUserSettingsFile()
    {
        var older = Path.Combine(root, "VALORANT", "Saved", "Config", "account-a", "Windows", "GameUserSettings.ini");
        var newer = Path.Combine(root, "VALORANT", "Saved", "Config", "account-b", "Windows", "GameUserSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        Directory.CreateDirectory(Path.GetDirectoryName(newer)!);
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var detected = new GameDetector(root, root, root).Detect()["Valorant"];

        detected.Installed.Should().BeTrue();
        detected.ConfigPath.Should().Be(newer);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
