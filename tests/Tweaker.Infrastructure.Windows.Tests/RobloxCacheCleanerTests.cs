using FluentAssertions;
using Tweaker.Infrastructure.Windows.Games;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class RobloxCacheCleanerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-cache", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Clean_RemovesTheDownloadedAssetCache()
    {
        var cached = File(@"Roblox\rbx-storage\ab\asset.bin", 4096);

        var result = new RobloxCacheCleaner(root).Clean(closeProcesses: false, CancellationToken.None);

        System.IO.File.Exists(cached).Should().BeFalse();
        result.DeletedFiles.Should().Be(1);
        result.FreedBytes.Should().Be(4096);
    }

    [Fact]
    public void Clean_RemovesLogsForEverySupportedLauncher()
    {
        File(@"Roblox\logs\player.log", 10);
        File(@"Fishstrap\Logs\a.log", 10);
        File(@"Bloxstrap\Logs\b.log", 10);
        File(@"Froststrap\Logs\c.log", 10);

        new RobloxCacheCleaner(root).Clean(closeProcesses: false, CancellationToken.None)
            .DeletedFiles.Should().Be(4);
    }

    [Fact]
    public void Clean_LeavesSettingsAndFastFlagsAlone()
    {
        var settings = File(@"Roblox\GlobalBasicSettings_13.xml", 200);
        var flags = File(@"Fishstrap\Modifications\ClientSettings\ClientAppSettings.json", 200);
        File(@"Roblox\rbx-storage\asset.bin", 50);

        new RobloxCacheCleaner(root).Clean(closeProcesses: false, CancellationToken.None);

        System.IO.File.Exists(settings).Should().BeTrue("configuration is not cache");
        System.IO.File.Exists(flags).Should().BeTrue("FastFlags are not cache");
    }

    [Fact]
    public void Clean_LeavesTheInstalledClientAlone()
    {
        var client = File(@"Fishstrap\Versions\version-abc\RobloxPlayerBeta.exe", 1000);

        new RobloxCacheCleaner(root).Clean(closeProcesses: false, CancellationToken.None);

        System.IO.File.Exists(client).Should().BeTrue("clearing cache must never uninstall the game");
    }

    [Fact]
    public void Clean_WithoutAnyCacheFolderReportsNothingRemoved()
    {
        Directory.CreateDirectory(root);

        var result = new RobloxCacheCleaner(root).Clean(closeProcesses: false, CancellationToken.None);

        result.DeletedFiles.Should().Be(0);
        result.FreedBytes.Should().Be(0);
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void FreedText_ReadsInMegabytesOnceItIsWorthIt()
    {
        new RobloxCacheCleanResult(0, 0, 5L * 1024 * 1024, []).FreedText.Should().Be("5.0 MB");
        new RobloxCacheCleanResult(0, 0, 2048, []).FreedText.Should().Be("2 KB");
    }

    private string File(string relative, int size)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
