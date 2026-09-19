using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Gpu;
using Tweaker.Infrastructure.Windows.Gpu.Intel;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// The Intel layer against a fake driver: what it previews, what it writes, what it verifies and what
/// it puts back. The real driver is only reachable on an Intel GPU; the logic around it is not.
/// </summary>
public sealed class IntelIgclProfileOperationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-igcl", Guid.NewGuid().ToString("N"));
    private static readonly GameDriverTarget Fortnite = GameDriverTargets.Require("Fortnite");
    private static readonly GameDriverTarget GtaV = GameDriverTargets.Require("GTA V");

    [Fact]
    public void UltraPotato_PreviewsEveryRowTheDriverSupports()
    {
        var factory = new FakeIgclFactory();

        var preview = Operation(factory, GamePerformanceProfile.UltraPotato, Fortnite).Describe();

        preview.Applied.Should().Contain("Low latency mode: On").And.Contain("MSAA: Disabled")
            .And.Contain("Texture filtering quality: Performance").And.Contain("Frame limit: Off");
        preview.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void AFeatureTheDriverDoesNotListIsSkippedWithAReason()
    {
        var factory = new FakeIgclFactory();
        factory.Store.RemoveFeature(Ctl3DFeature.VrrWindowedBlt);

        var preview = Operation(factory, GamePerformanceProfile.UltraPotato, Fortnite).Describe();

        preview.Skipped.Should().ContainSingle(x => x.Contains("Variable refresh") && x.Contains("does not expose"));
        preview.Applied.Should().NotContain(x => x.Contains("Variable refresh"));
    }

    [Fact]
    public void AGlobalOnlyFeatureIsNeverWrittenPerGame()
    {
        var factory = new FakeIgclFactory();
        factory.Store.MakeGlobalOnly(Ctl3DFeature.LowLatency);

        var preview = Operation(factory, GamePerformanceProfile.BalancedFps, Fortnite).Describe();

        preview.Skipped.Should().ContainSingle(x => x.Contains("Low latency") && x.Contains("globally"));
    }

    [Fact]
    public void AnEnumeratorTheDriverDoesNotOfferIsSkipped()
    {
        var factory = new FakeIgclFactory();
        // The driver enumerates only Performance (0) and Quality (2); Balanced (1) is missing.
        factory.Store.Caps[0] = factory.Store.Caps[0].Select(x => x.Feature == Ctl3DFeature.TextureFilteringQuality
            ? x with { SupportedEnumTypes = (1ul << 0) | (1ul << 2) } : x).ToList();

        var preview = Operation(factory, GamePerformanceProfile.BalancedFps, Fortnite).Describe();

        preview.Skipped.Should().ContainSingle(x => x.Contains("Texture filtering quality: Balanced"));
    }

    [Fact]
    public async Task Apply_WritesEveryApplicableRowForEveryExecutableOnEveryAdapter()
    {
        var factory = new FakeIgclFactory();
        factory.Store.AddAdapter("Iris Xe");
        var operation = Operation(factory, GamePerformanceProfile.UltraPotato, GtaV);
        await operation.ReadCurrentValueAsync(CancellationToken.None);

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        var rows = IntelGameProfileCatalog.ForGame(GtaV, GamePerformanceProfile.UltraPotato).Count;
        factory.Store.Writes.Should().HaveCount(rows * 2 * 2, "rows × two executables × two adapters");
        factory.Store.Writes.Should().Contain(x => x.Application == "GTA5_Enhanced.exe" && x.Adapter == 1);
        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Verify_FailsWhenTheDriverDidNotKeepAValue()
    {
        var factory = new FakeIgclFactory();
        var operation = Operation(factory, GamePerformanceProfile.MegaFps, Fortnite);
        await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        factory.Store.Values[(0, Ctl3DFeature.Msaa, "fortniteclient-win64-shipping.exe")] = new(true, CtlValues.MsaaAppChoice);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_TreatsAnUnreadableFeatureAsUnverifiableNotAsWrong()
    {
        var factory = new FakeIgclFactory();
        factory.Store.Unreadable.Add(Ctl3DFeature.Cmaa);
        var operation = Operation(factory, GamePerformanceProfile.UltraPotato, Fortnite);
        await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Restore_PutsBackWhatWasReadAndLeavesTheUnreadableAlone()
    {
        var factory = new FakeIgclFactory();
        factory.Store.Values[(0, Ctl3DFeature.Anisotropic, "fortniteclient-win64-shipping.exe")] = new(true, 16);
        factory.Store.Unreadable.Add(Ctl3DFeature.Cmaa);
        var operation = Operation(factory, GamePerformanceProfile.UltraPotato, Fortnite);
        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        factory.Store.Writes.Clear();

        await operation.RestoreAsync(snapshot, CancellationToken.None);

        factory.Store.Values[(0, Ctl3DFeature.Anisotropic, "fortniteclient-win64-shipping.exe")].Raw.Should().Be(16);
        factory.Store.Writes.Should().NotContain(x => x.Feature == Ctl3DFeature.Cmaa,
            "a value the driver refused to read has no prior state to invent");
    }

    [Fact]
    public async Task Apply_RefusesARequestFromAnotherOperation()
    {
        var operation = Operation(new FakeIgclFactory(), GamePerformanceProfile.UltraPotato, Fortnite);

        await FluentActions.Awaiting(() => operation.ApplyAsync("intel.megafps.v1", CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task TheBaselineKeepsTheFirstCapturedValueAcrossProfiles()
    {
        var factory = new FakeIgclFactory();
        var baseline = new DriverBaselineFile(Path.Combine(root, "fortnite.json"));
        var key = (0, Ctl3DFeature.TextureFilteringQuality, "fortniteclient-win64-shipping.exe");
        factory.Store.Values[key] = new(true, CtlValues.TextureFilteringBalanced);

        var first = new IntelIgclProfileOperation(GamePerformanceProfile.Competitive, Fortnite, factory, baseline);
        await first.ReadCurrentValueAsync(CancellationToken.None);
        await first.ApplyAsync(first.RequestedValue, CancellationToken.None);
        var second = new IntelIgclProfileOperation(GamePerformanceProfile.UltraPotato, Fortnite, factory, baseline);
        await second.ReadCurrentValueAsync(CancellationToken.None);
        await second.ApplyAsync(second.RequestedValue, CancellationToken.None);

        var reset = new IntelDriverProfileReset(Fortnite, factory, baseline);
        reset.HasBaseline.Should().BeTrue();
        await reset.ResetAsync(CancellationToken.None);

        factory.Store.Values[key].Raw.Should().Be(CtlValues.TextureFilteringBalanced, "the value before the first apply wins");
        reset.HasBaseline.Should().BeFalse("a reset forgets the baseline once the driver accepted the write");
    }

    [Fact]
    public void TheProviderIsUnavailableWithoutTheDriverOrWithoutAnIntelGpu()
    {
        var factory = new FakeIgclFactory { IsAvailable = false };
        var provider = new IntelDriverProfileProvider(factory, _ => new DriverBaselineFile(Path.Combine(root, "x.json")));

        provider.IsAvailable(Snapshot("Intel")).Should().BeFalse("ControlLib.dll is missing");
        factory.IsAvailable = true;
        provider.IsAvailable(Snapshot("NVIDIA")).Should().BeFalse("there is no Intel GPU");
        provider.IsAvailable(Snapshot("Intel")).Should().BeTrue();
        provider.IsWholeGpu.Should().BeFalse();
        provider.Vendor.Should().Be("Intel");
    }

    [Fact]
    public void OperationIdsAreKeyedOnVendorGameAndProfile() =>
        Operation(new FakeIgclFactory(), GamePerformanceProfile.UltraPotato, GtaV).Descriptor.Id.Should().Be("intel.igcl.gta-v.ultrapotato");

    private IntelIgclProfileOperation Operation(FakeIgclFactory factory, GamePerformanceProfile profile, GameDriverTarget target) =>
        new(profile, target, factory, new DriverBaselineFile(Path.Combine(root, $"{target.Slug}.json")));

    private static SystemSnapshot Snapshot(string vendor) => new(new("Windows 11", "10.0.26100", 26100), new("CPU", "Intel"),
        [new("GPU", vendor, "1")], new(1), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

internal sealed class FakeIgclFactory : IIgclDriverFactory
{
    public bool IsAvailable { get; set; } = true;
    public FakeIgclStore Store { get; } = new();
    public IIgclDriver Open() => new FakeIgclDriver(Store);
}

internal sealed record IgclWrite(int Adapter, Ctl3DFeature Feature, string Application, IgclFeatureValue Value);

internal sealed class FakeIgclStore
{
    public List<IgclAdapter> Adapters { get; } = [new(0, "Arc A750")];
    public Dictionary<int, List<IgclFeatureCapability>> Caps { get; } = new();
    public Dictionary<(int Adapter, Ctl3DFeature Feature, string Application), IgclFeatureValue> Values { get; } = new();
    public HashSet<Ctl3DFeature> Unreadable { get; } = [];
    public List<IgclWrite> Writes { get; } = [];

    public FakeIgclStore() => Caps[0] = FullCapabilities();

    public void AddAdapter(string name)
    {
        Adapters.Add(new(Adapters.Count, name));
        Caps[Adapters.Count - 1] = FullCapabilities();
    }

    public void RemoveFeature(Ctl3DFeature feature)
    {
        foreach (var key in Caps.Keys.ToArray()) Caps[key] = Caps[key].Where(x => x.Feature != feature).ToList();
    }

    public void MakeGlobalOnly(Ctl3DFeature feature)
    {
        foreach (var key in Caps.Keys.ToArray())
            Caps[key] = Caps[key].Select(x => x.Feature == feature ? x with { PerAppSupport = false } : x).ToList();
    }

    private static List<IgclFeatureCapability> FullCapabilities() =>
        IntelGameProfileCatalog.ForGame(GameDriverTargets.Require("Fortnite"), GamePerformanceProfile.UltraPotato)
            .Select(x => new IgclFeatureCapability(x.Feature, x.ValueType, true, ulong.MaxValue, 0, 500, 0, 500))
            .ToList();
}

internal sealed class FakeIgclDriver(FakeIgclStore store) : IIgclDriver
{
    public IReadOnlyList<IgclAdapter> Adapters => store.Adapters;

    public IReadOnlyList<IgclFeatureCapability> Capabilities(IgclAdapter adapter) => store.Caps[adapter.Index];

    public IgclFeatureValue? Read(IgclAdapter adapter, Ctl3DFeature feature, CtlPropertyValueType type, string application)
    {
        if (store.Unreadable.Contains(feature)) return null;
        return store.Values.TryGetValue((adapter.Index, feature, application.ToLowerInvariant()), out var value)
            ? value
            : new IgclFeatureValue(false, 0);
    }

    public void Write(IgclAdapter adapter, Ctl3DFeature feature, CtlPropertyValueType type, string application, IgclFeatureValue value)
    {
        store.Values[(adapter.Index, feature, application.ToLowerInvariant())] = value;
        store.Writes.Add(new(adapter.Index, feature, application, value));
    }

    public void Dispose() { }
}
