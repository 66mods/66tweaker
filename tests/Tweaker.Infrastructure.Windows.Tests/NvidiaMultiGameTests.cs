using System.Text.Json;
using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Infrastructure.Windows.Gpu.Nvidia;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// The NVIDIA layer used to know one executable. It now knows five games, one of them with two builds,
/// and still has to read the journals and baselines release 1.1 wrote for Roblox.
/// </summary>
public sealed class NvidiaMultiGameTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-nv-multi", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EveryGameGetsTheRobloxLadderValueForValue()
    {
        foreach (var target in GameDriverTargets.All)
            foreach (var profile in Enum.GetValues<GamePerformanceProfile>())
                NvidiaGameProfileCatalog.ForGame(target, profile).Should().Equal(NvidiaGameProfileCatalog.ForRoblox(profile),
                    "{0} {1} writes the same driver rows the pinned Roblox set does", target.Game, profile);
    }

    [Fact]
    public void OperationIdsAreKeyedOnTheGameAndRobloxKeepsItsReleaseOneId()
    {
        new NvidiaDrsProfileOperation(GamePerformanceProfile.UltraPotato, GameDriverTargets.Require("Roblox")).Descriptor.Id
            .Should().Be("nvidia.drs.roblox.ultrapotato");
        new NvidiaDrsProfileOperation(GamePerformanceProfile.MegaFps, GameDriverTargets.Require("GTA V")).Descriptor.Id
            .Should().Be("nvidia.drs.gta-v.megafps");
        new NvidiaDrsProfileOperation(GamePerformanceProfile.BalancedFps, GameDriverTargets.Require("Fortnite")).Descriptor.Name
            .Should().Contain("Fortnite").And.Contain("Balanced FPS");
    }

    [Fact]
    public void TheOwnedProfileIsNamedAfterTheGame()
    {
        NvidiaDrsProfileOperation.OwnedProfileNameFor(GameDriverTargets.Require("Roblox")).Should().Be("66mods Roblox",
            "release 1.1 created profiles under this name and rollback has to find them");
        NvidiaDrsProfileOperation.OwnedProfileNameFor(GameDriverTargets.Require("Valorant")).Should().Be("66mods Valorant");
    }

    [Fact]
    public void ASchemaOneSnapshotFromReleaseOneStillReadsAsOneExecutable()
    {
        const string journal = """
            {"SchemaVersion":1,"Executable":"RobloxPlayerBeta.exe","ProfileCreatedByUs":true,"ProfileName":"66mods Roblox",
             "Settings":[{"SettingId":1,"Name":"a","Existed":true,"Value":7}]}
            """;

        var snapshot = JsonSerializer.Deserialize<NvidiaDrsSnapshot>(journal)!;

        snapshot.AllTargets.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new NvidiaExecutableSnapshot("RobloxPlayerBeta.exe", true, [new(1, "a", true, 7)]));
    }

    [Fact]
    public void ASchemaOneJournalMatchesTheSchemaTwoStateReadBackAfterItsRestore()
    {
        // Release 1.1 wrote this into the journal; after 2.0 restores it, ReadCurrentValueAsync describes
        // the same driver state in schema 2. Comparing the two as text would call every such restore a
        // failure and leave the transaction "interrupted" on every launch.
        const string journal = """
            {"SchemaVersion":1,"Executable":"RobloxPlayerBeta.exe","ProfileCreatedByUs":true,"ProfileName":"66mods Roblox",
             "Settings":[{"SettingId":1,"Name":"a","Existed":false,"Value":0}]}
            """;
        const string reRead = """
            {"SchemaVersion":2,"Executable":"RobloxPlayerBeta.exe","ProfileCreatedByUs":true,"ProfileName":"66mods Roblox",
             "Settings":[{"SettingId":1,"Name":"a","Existed":false,"Value":0}],
             "Targets":[{"Executable":"RobloxPlayerBeta.exe","ProfileCreatedByUs":true,"Settings":[{"SettingId":1,"Name":"a","Existed":false,"Value":0}]}]}
            """;
        var operation = new NvidiaDrsProfileOperation(GamePerformanceProfile.UltraPotato, GameDriverTargets.Require("Roblox"));

        operation.RestoredMatches(journal, reRead).Should().BeTrue();
        operation.RestoredMatches(journal, reRead.Replace("\"Value\":0}]}]", "\"Value\":5}]}]")).Should().BeFalse("a different value is a different state");
        operation.RestoredMatches(journal, "not json").Should().BeFalse();
    }

    [Fact]
    public void ASchemaTwoSnapshotRoundTripsEveryExecutable()
    {
        var snapshot = new NvidiaDrsSnapshot(2, "GTA5.exe", false, "66mods GTA V", [])
        {
            Targets =
            [
                new("GTA5.exe", false, [new(1, "a", true, 1)]),
                new("GTA5_Enhanced.exe", true, [new(1, "a", false, 0)])
            ]
        };

        var restored = JsonSerializer.Deserialize<NvidiaDrsSnapshot>(JsonSerializer.Serialize(snapshot))!;

        restored.AllTargets.Should().HaveCount(2);
        restored.AllTargets[1].ProfileCreatedByUs.Should().BeTrue();
    }

    [Fact]
    public void BaselineMerge_KeepsTheFirstValuePerExecutableAndAddsNewExecutables()
    {
        var store = new NvidiaBaselineStore(Path.Combine(root, "gta.json"));
        store.Merge(Snapshot(("GTA5.exe", true, 0x11)));

        store.Merge(Snapshot(("GTA5.exe", true, 0x99), ("GTA5_Enhanced.exe", false, 0)));

        var merged = store.Load()!;
        merged.AllTargets.Should().HaveCount(2);
        merged.AllTargets.Single(x => x.Executable == "GTA5.exe").Settings.Single().Value.Should().Be(0x11);
        merged.AllTargets.Single(x => x.Executable == "GTA5_Enhanced.exe").Settings.Single().Existed.Should().BeFalse();
    }

    [Fact]
    public void BaselineFilesAreKeptPerGameAndRobloxKeepsItsReleaseOneFileName()
    {
        NvidiaBaselineStore.DefaultPath(GameDriverTargets.Require("Roblox")).Should().EndWith(@"Nvidia\roblox-baseline.json");
        NvidiaBaselineStore.DefaultPath(GameDriverTargets.Require("GTA V")).Should().EndWith(@"Nvidia\gta-v-baseline.json");
    }

    [Fact]
    public void TheProviderIsNotAvailableWithoutAnNvidiaGpu()
    {
        var provider = new NvidiaDriverProfileProvider();
        var snapshot = new Domain.Models.SystemSnapshot(new("Windows 11", "10.0.26100", 26100), new("CPU", "AMD"),
            [new("Radeon RX 6600", "AMD", "1")], new(1), new(false, true, "Balanced"),
            new Dictionary<string, Domain.Models.DetectedGame>(), []);

        provider.IsAvailable(snapshot).Should().BeFalse();
        provider.IsWholeGpu.Should().BeFalse();
        provider.ScopeNote(GameDriverTargets.Require("GTA V")).Should().Contain("GTA5.exe and GTA5_Enhanced.exe");
    }

    private static NvidiaDrsSnapshot Snapshot(params (string Executable, bool Existed, uint Value)[] entries) =>
        new(2, entries[0].Executable, false, "66mods GTA V", [])
        {
            Targets = entries.Select(x => new NvidiaExecutableSnapshot(x.Executable, false,
                [new NvidiaSettingRestorePoint(1, "a", x.Existed, x.Value)])).ToArray()
        };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
