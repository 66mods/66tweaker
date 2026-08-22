using FluentAssertions;
using Tweaker.Infrastructure.Windows.Gpu.Nvidia;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class NvidiaBaselineStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "66mods-baseline", Guid.NewGuid().ToString("N"), "b.json");

    [Fact]
    public void Load_WithoutAnyFileReportsNoBaseline()
    {
        var store = new NvidiaBaselineStore(path);

        store.Exists.Should().BeFalse();
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Merge_KeepsTheValueCapturedFirst()
    {
        var store = new NvidiaBaselineStore(path);
        store.Merge(Snapshot(new NvidiaSettingRestorePoint(1, "a", true, 0x11)));

        // A later apply sees the already-modified value; it must not overwrite the original.
        store.Merge(Snapshot(new NvidiaSettingRestorePoint(1, "a", true, 0x99)));

        store.Load()!.Settings.Single().Value.Should().Be(0x11);
    }

    [Fact]
    public void Merge_AccumulatesSettingsAcrossProfiles()
    {
        var store = new NvidiaBaselineStore(path);
        store.Merge(Snapshot(new NvidiaSettingRestorePoint(1, "a", true, 1)));

        store.Merge(Snapshot(new NvidiaSettingRestorePoint(2, "b", false, 0)));

        store.Load()!.Settings.Should().HaveCount(2, "a later profile can touch settings the first one did not");
    }

    [Fact]
    public void Merge_KeepsTheProfileCreatedFlagFromTheFirstTouch()
    {
        var store = new NvidiaBaselineStore(path);
        store.Merge(Snapshot(true, new NvidiaSettingRestorePoint(1, "a", false, 0)));

        store.Merge(Snapshot(false, new NvidiaSettingRestorePoint(2, "b", true, 5)));

        store.Load()!.ProfileCreatedByUs.Should().BeTrue("only the first touch can have created the profile");
    }

    [Fact]
    public void Clear_ForgetsTheBaseline()
    {
        var store = new NvidiaBaselineStore(path);
        store.Merge(Snapshot(new NvidiaSettingRestorePoint(1, "a", true, 1)));

        store.Clear();

        store.Exists.Should().BeFalse();
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Load_WithCorruptContentDoesNotThrow()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        new NvidiaBaselineStore(path).Load().Should().BeNull();
    }

    private static NvidiaDrsSnapshot Snapshot(params NvidiaSettingRestorePoint[] settings) => Snapshot(false, settings);

    private static NvidiaDrsSnapshot Snapshot(bool created, params NvidiaSettingRestorePoint[] settings) =>
        new(1, "RobloxPlayerBeta.exe", created, "66mods Roblox", settings);

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
