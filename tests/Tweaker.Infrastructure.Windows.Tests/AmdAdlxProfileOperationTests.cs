using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Gpu;
using Tweaker.Infrastructure.Windows.Gpu.Amd;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// The AMD layer against a fake driver. ADLX writes the whole GPU, so these also pin the one thing a
/// player has to be told before pressing Apply: that Undo and Reset restore the card, not one game.
/// </summary>
public sealed class AmdAdlxProfileOperationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-adlx", Guid.NewGuid().ToString("N"));
    private static readonly GameDriverTarget Fortnite = GameDriverTargets.Require("Fortnite");

    [Fact]
    public void UltraPotato_PreviewsEverySupportedRow()
    {
        var preview = Operation(new FakeAdlxFactory(), GamePerformanceProfile.UltraPotato, Fortnite).Describe();

        preview.Applied.Should().Contain("Radeon Anti-Lag: On").And.Contain("Radeon Chill: Off")
            .And.Contain("Wait for Vertical Refresh: Always off").And.Contain("Radeon Boost: Off");
        preview.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void ASettingTheGpuDoesNotSupportIsSkippedWithAReason()
    {
        var factory = new FakeAdlxFactory();
        factory.Store.Unsupported.Add(AdlxSetting.RadeonSuperResolution);

        var preview = Operation(factory, GamePerformanceProfile.UltraPotato, Fortnite).Describe();

        preview.Skipped.Should().ContainSingle(x => x.Contains("Radeon Super Resolution") && x.Contains("does not offer"));
    }

    [Fact]
    public async Task Apply_WritesTheWholeGpuAndVerifies()
    {
        var factory = new FakeAdlxFactory();
        var operation = Operation(factory, GamePerformanceProfile.UltraPotato, Fortnite);
        await operation.ReadCurrentValueAsync(CancellationToken.None);

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        factory.Store.Values[(0, AdlxSetting.Chill)].Should().Be(AdlxValues.Off);
        factory.Store.Values[(0, AdlxSetting.WaitForVerticalRefresh)].Should().Be(AdlxValues.WaitForVerticalRefreshAlwaysOff);
        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Undo_RestoresTheCardToWhatItHeldBefore()
    {
        var factory = new FakeAdlxFactory();
        factory.Store.Values[(0, AdlxSetting.Chill)] = AdlxValues.On;
        factory.Store.Values[(0, AdlxSetting.TessellationMode)] = AdlxValues.TessellationUseAppSettings;
        var operation = Operation(factory, GamePerformanceProfile.UltraPotato, Fortnite);
        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        factory.Store.Values[(0, AdlxSetting.Chill)].Should().Be(AdlxValues.Off);

        await operation.RestoreAsync(snapshot, CancellationToken.None);

        factory.Store.Values[(0, AdlxSetting.Chill)].Should().Be(AdlxValues.On);
        factory.Store.Values[(0, AdlxSetting.TessellationMode)].Should().Be(AdlxValues.TessellationUseAppSettings);
    }

    [Fact]
    public async Task ResetFromTheBaseline_RestoresTheFirstCapturedStateAcrossProfiles()
    {
        var factory = new FakeAdlxFactory();
        factory.Store.Values[(0, AdlxSetting.AntiLag)] = AdlxValues.Off;
        var baseline = new DriverBaselineFile(Path.Combine(root, "fortnite.json"));
        var competitive = new AmdAdlxProfileOperation(GamePerformanceProfile.Competitive, Fortnite, factory, baseline);
        await competitive.ReadCurrentValueAsync(CancellationToken.None);
        await competitive.ApplyAsync(competitive.RequestedValue, CancellationToken.None);
        var potato = new AmdAdlxProfileOperation(GamePerformanceProfile.UltraPotato, Fortnite, factory, baseline);
        await potato.ReadCurrentValueAsync(CancellationToken.None);
        await potato.ApplyAsync(potato.RequestedValue, CancellationToken.None);

        var reset = new AmdDriverProfileReset(Fortnite, factory, baseline);
        reset.PendingCount.Should().BeGreaterThan(0);
        var restored = await reset.ResetAsync(CancellationToken.None);

        restored.Should().BeGreaterThan(0);
        factory.Store.Values[(0, AdlxSetting.AntiLag)].Should().Be(AdlxValues.Off, "the state before the first apply wins");
        reset.HasBaseline.Should().BeFalse();
    }

    [Fact]
    public async Task TheBaselineBelongsToTheCardSoASecondGameCannotOverwriteTheOwnersValues()
    {
        // ADLX writes the whole GPU. If each game kept its own baseline, Roblox's capture would record the
        // Ultra Potato values Fortnite had already put on the card as Roblox's "before".
        var factory = new FakeAdlxFactory();
        factory.Store.Values[(0, AdlxSetting.AntiLag)] = AdlxValues.Off;
        var card = new DriverBaselineFile(Path.Combine(root, "gpu.json"));
        var fortnite = new AmdAdlxProfileOperation(GamePerformanceProfile.UltraPotato, Fortnite, factory, card);
        await fortnite.ReadCurrentValueAsync(CancellationToken.None);
        await fortnite.ApplyAsync(fortnite.RequestedValue, CancellationToken.None);
        var roblox = new AmdAdlxProfileOperation(GamePerformanceProfile.BalancedFps, GameDriverTargets.Require("Roblox"), factory, card);
        await roblox.ReadCurrentValueAsync(CancellationToken.None);
        await roblox.ApplyAsync(roblox.RequestedValue, CancellationToken.None);

        var reset = new AmdDriverProfileReset(GameDriverTargets.Require("Roblox"), factory, card);
        reset.HasBaseline.Should().BeTrue("the card was written, whichever game did it");
        await reset.ResetAsync(CancellationToken.None);

        factory.Store.Values[(0, AdlxSetting.AntiLag)].Should().Be(AdlxValues.Off, "the value before Fortnite's apply is the owner's");
    }

    [Fact]
    public async Task ASettingTheDriverWillNotReadBackDoesNotFailTheVerification()
    {
        var factory = new FakeAdlxFactory();
        factory.Store.Unreadable.Add(AdlxSetting.RadeonSuperResolution);
        var operation = Operation(factory, GamePerformanceProfile.UltraPotato, Fortnite);
        await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue(
            "the capture already marked it unrestorable; failing here would roll every other layer back and leave it written");
    }

    [Fact]
    public void ABaselineThatCannotBeWrittenRefusesLoudly()
    {
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var file = new DriverBaselineFile(Path.Combine(blocker, "gpu.json"));

        var act = () => file.Save("{}");

        act.Should().Throw<IOException>().WithMessage("*Could not record the driver settings*");
    }

    [Fact]
    public void TheProviderSaysItWritesTheWholeGpu()
    {
        var provider = new AmdDriverProfileProvider(new FakeAdlxFactory(), _ => new DriverBaselineFile(Path.Combine(root, "x.json")));

        provider.IsWholeGpu.Should().BeTrue();
        provider.ScopeNote(Fortnite).Should().Contain("every game on this GPU").And.Contain("whole card");
        provider.IsAvailable(Snapshot("AMD")).Should().BeTrue();
        provider.IsAvailable(Snapshot("NVIDIA")).Should().BeFalse();
    }

    [Fact]
    public void TheProviderIsUnavailableWithoutTheDriverLibrary()
    {
        var provider = new AmdDriverProfileProvider(new FakeAdlxFactory { IsAvailable = false },
            _ => new DriverBaselineFile(Path.Combine(root, "x.json")));

        provider.IsAvailable(Snapshot("AMD")).Should().BeFalse();
    }

    [Fact]
    public void OperationIdsAreKeyedOnVendorGameAndProfile() =>
        Operation(new FakeAdlxFactory(), GamePerformanceProfile.MegaFps, GameDriverTargets.Require("GTA V")).Descriptor.Id
            .Should().Be("amd.adlx.gta-v.megafps");

    private AmdAdlxProfileOperation Operation(FakeAdlxFactory factory, GamePerformanceProfile profile, GameDriverTarget target) =>
        new(profile, target, factory, new DriverBaselineFile(Path.Combine(root, $"{target.Slug}.json")));

    private static SystemSnapshot Snapshot(string vendor) => new(new("Windows 11", "10.0.26100", 26100), new("CPU", "AMD"),
        [new("GPU", vendor, "1")], new(1), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

internal sealed class FakeAdlxFactory : IAdlxDriverFactory
{
    public bool IsAvailable { get; set; } = true;
    public FakeAdlxStore Store { get; } = new();
    public IAdlxDriver Open() => new FakeAdlxDriver(Store);
}

internal sealed class FakeAdlxStore
{
    public List<AdlxGpu> Gpus { get; } = [new(0, "Radeon RX 6600")];
    public HashSet<AdlxSetting> Unsupported { get; } = [];
    /// <summary>Supported, written, but never readable: some ADLX features answer the write and not the read.</summary>
    public HashSet<AdlxSetting> Unreadable { get; } = [];
    public Dictionary<(int Gpu, AdlxSetting Setting), int> Values { get; } = new();
    public List<(int Gpu, AdlxSetting Setting, int Value)> Writes { get; } = [];
}

internal sealed class FakeAdlxDriver(FakeAdlxStore store) : IAdlxDriver
{
    public IReadOnlyList<AdlxGpu> Gpus => store.Gpus;
    public bool IsSupported(AdlxGpu gpu, AdlxSetting setting) => !store.Unsupported.Contains(setting);
    public int? Read(AdlxGpu gpu, AdlxSetting setting) => store.Unreadable.Contains(setting) ? null
        : store.Values.TryGetValue((gpu.Index, setting), out var value) ? value : 0;
    public void Write(AdlxGpu gpu, AdlxSetting setting, int value)
    {
        store.Values[(gpu.Index, setting)] = value;
        store.Writes.Add((gpu.Index, setting, value));
    }
    public void Dispose() { }
}
