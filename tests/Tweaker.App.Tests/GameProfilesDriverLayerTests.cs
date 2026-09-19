using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

/// <summary>
/// A game profile is one transaction across every layer: the game's own file and each vendor's driver.
/// These drive the view model with a fake vendor so the composition is proven without a driver.
/// </summary>
public sealed class GameProfilesDriverLayerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-driver-vm", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Apply_WritesTheFileAndTheDriverInOneTransactionAndUndoRewindsBoth()
    {
        var (vm, provider, path, original) = Build("Fortnite");
        vm.SelectedProfile = GamePerformanceProfile.UltraPotato;

        await vm.ApplySelectedAsync(CancellationToken.None);

        vm.Status.Should().Contain("Fortnite setting(s)").And.Contain("NVIDIA setting(s)").And.Contain("verified");
        provider.Operations.Should().ContainSingle().Which.Applied.Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Contain("sg.ResolutionQuality=50");

        await vm.UndoAsync(CancellationToken.None);

        provider.Operations.Single().Applied.Should().BeFalse("rollback has to reach the driver half too");
        (await File.ReadAllTextAsync(path)).Should().Be(original);
    }

    [Fact]
    public async Task ThePreviewIsFilledForTheFirstSelectionWithoutAnyoneChangingIt()
    {
        // The page used to open on "Nothing to write on this PC" for an installed game, because only the
        // selection setters refreshed the preview and the first selection is made by the constructor.
        var (vm, _, _, _) = Build("Fortnite");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (vm.PreviewApplied.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        vm.SelectedGame.Should().Be("Fortnite");
        vm.PreviewApplied.Should().Contain(x => x.StartsWith("Fortnite · ")).And.Contain(x => x.StartsWith("NVIDIA · "));
        vm.PreviewSummary.Should().NotContain("Nothing to write");
    }

    [Fact]
    public async Task AGameThatIsNotInstalledPreviewsNothingEvenWithADriverLayer()
    {
        var (vm, _, _, _) = Build("Fortnite");
        vm.SelectedGame = "Valorant";

        await vm.RefreshPreviewAsync();

        vm.PreviewApplied.Should().BeEmpty("Apply would refuse; the preview must not promise driver rows for an absent game");
        vm.CanApplySelectedGame.Should().BeFalse();
    }

    [Fact]
    public async Task ThePreviewListsTheGameLinesFirstThenTheVendorLinesWithTheirName()
    {
        var (vm, _, _, _) = Build("Fortnite");
        vm.SelectedProfile = GamePerformanceProfile.UltraPotato;

        await vm.RefreshPreviewAsync();

        vm.PreviewApplied.Should().NotBeEmpty();
        vm.PreviewApplied.First().Should().StartWith("Fortnite · ");
        vm.PreviewApplied.Should().Contain("NVIDIA · Low latency mode: On");
        vm.PreviewSkipped.Should().Contain("NVIDIA · Image scaling: Off — not exposed by this driver");
        vm.PreviewCaption.Should().Contain("FortniteClient-Win64-Shipping.exe");
    }

    [Fact]
    public async Task ADetectedGameWithoutAConfigFileCanStillGetItsDriverProfile()
    {
        var (vm, provider, _, _) = Build("Fortnite", withConfig: false);

        vm.CanApplySelectedGame.Should().BeTrue("the driver layer is a layer of its own");
        await vm.ApplySelectedAsync(CancellationToken.None);

        provider.Operations.Should().ContainSingle().Which.Applied.Should().BeTrue();
        vm.Status.Should().Contain("NVIDIA setting(s)").And.NotContain("Fortnite setting(s)");
    }

    [Fact]
    public async Task ADriverThatRefusesTheWriteRollsTheFileBackToo()
    {
        var (vm, provider, path, original) = Build("Fortnite");
        provider.FailApply = true;

        await vm.ApplySelectedAsync(CancellationToken.None);

        vm.Progress.OutcomeKind.Should().Be("Warning");
        (await File.ReadAllTextAsync(path)).Should().Be(original, "one transaction: a failed driver write undoes the file");
    }

    [Fact]
    public async Task AWholeGpuVendorIsNamedAsSuchInThePreviewCaptionAndTheLines()
    {
        var (vm, _, _, _) = Build("Fortnite", vendor: "AMD", wholeGpu: true);
        vm.SelectedProfile = GamePerformanceProfile.UltraPotato;

        await vm.RefreshPreviewAsync();

        vm.PreviewApplied.Should().Contain(x => x.StartsWith("AMD (whole GPU) · "));
        vm.PreviewCaption.Should().Contain("whole GPU");
        vm.VendorNote.Should().StartWith("AMD: whole GPU");
    }

    [Fact]
    public async Task ResetUsesEveryVendorBaselineAndClearsTheUndoStack()
    {
        var (vm, provider, _, _) = Build("Fortnite");
        await vm.ApplySelectedAsync(CancellationToken.None);
        provider.BaselineCount = 3;
        vm.CanResetProfile.Should().BeTrue();

        await vm.ResetProfileCommand.ExecuteAsync();

        vm.Status.Should().Contain("3 setting(s) put back");
        provider.Resets.Should().Be(1);
        await vm.UndoAsync(CancellationToken.None);
        vm.Status.Should().Contain("No game profile session");
    }

    private (GameProfilesViewModel Vm, FakeProvider Provider, string Path, string Original) Build(string game,
        bool withConfig = true, string vendor = "NVIDIA", bool wholeGpu = false)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "GameUserSettings.ini");
        const string original = "ResolutionSizeX=1920\nResolutionSizeY=1080\nsg.ResolutionQuality=100\nsg.ShadowQuality=3\n";
        if (withConfig) File.WriteAllText(path, original);
        var snapshot = new SystemSnapshot(new("Windows 11", "10.0.26100", 26100), new("CPU", "AMD"),
            [new("GPU", vendor, "1")], new(16_000_000_000), new(false, true, "Balanced"),
            new Dictionary<string, DetectedGame> { [game] = new(game, true, withConfig ? path : null) }, []);
        var provider = new FakeProvider(vendor, wholeGpu);
        var vm = new GameProfilesViewModel(snapshot, new TransactionCoordinator(new MemoryStore()), [provider])
        {
            SelectedGame = game
        };
        return (vm, provider, path, original);
    }

    private sealed class FakeProvider(string vendor, bool wholeGpu) : IGpuDriverProfileProvider, IDriverProfileBaseline
    {
        public List<FakeOperation> Operations { get; } = [];
        public bool FailApply { get; set; }
        public int BaselineCount { get; set; }
        public int Resets { get; private set; }
        public string Vendor => vendor;
        public bool IsWholeGpu => wholeGpu;
        public bool IsAvailable(SystemSnapshot snapshot) => true;
        public DriverProfilePreview Describe(GamePerformanceProfile profile, GameDriverTarget target) =>
            new(["Low latency mode: On", "LOD bias: no textures"], ["Image scaling: Off — not exposed by this driver"]);
        public ITweakOperation CreateOperation(GamePerformanceProfile profile, GameDriverTarget target)
        {
            var operation = new FakeOperation(vendor, target, profile, FailApply);
            Operations.Add(operation);
            return operation;
        }
        public string ScopeNote(GameDriverTarget target) => wholeGpu ? "Applies to every game on this GPU." : "Per game.";
        public IDriverProfileBaseline Baseline(GameDriverTarget target) => this;
        public bool HasBaseline => BaselineCount > 0;
        public int PendingCount => BaselineCount;
        public Task<int> ResetAsync(CancellationToken cancellationToken)
        {
            Resets++;
            var count = BaselineCount;
            BaselineCount = 0;
            return Task.FromResult(count);
        }
    }

    private sealed class FakeOperation(string vendor, GameDriverTarget target, GamePerformanceProfile profile, bool fail)
        : ITweakOperation, IRequestedValueProvider
    {
        public bool Applied { get; private set; }
        public TweakDescriptor Descriptor { get; } = new($"fake.{vendor}.{target.Slug}.{profile}".ToLowerInvariant(),
            $"{vendor} fake", TweakCategory.Gpu, ImpactLevel.Medium, RiskLevel.Advanced, false, false);
        public string RequestedValue => $"fake.{profile}";
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("before");
        public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
        {
            if (fail) throw new InvalidOperationException("the driver refused the write");
            Applied = true;
            return Task.CompletedTask;
        }
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) => Task.FromResult(Applied);
        public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
        {
            originalValue.Should().Be("before");
            Applied = false;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStore : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord transaction, CancellationToken cancellationToken)
        {
            records[transaction.Id] = transaction;
            return Task.CompletedTask;
        }
        public Task SaveAsync(TransactionRecord transaction, CancellationToken cancellationToken)
        {
            records[transaction.Id] = transaction;
            return Task.CompletedTask;
        }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<TransactionRecord?>(null);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
