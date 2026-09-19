using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Gpu;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

/// <summary>
/// The page has to say what the driver half does on this PC.
///
/// A machine with no writable driver interface is told so, because the client settings are still written
/// and still help. An AMD machine with ADLX is told the opposite thing — that the driver layer reaches
/// every game — because ADLX has no per-game scope. Both are asserted on VendorNote, the one property the
/// page binds: an earlier attempt computed the note into a collection nothing was bound to.
/// </summary>
public sealed class DriverLayerNoteTests
{
    [Theory]
    [InlineData("AMD", "Radeon RX 6600")]
    [InlineData("Intel", "Intel Arc A750")]
    public void AGpuWithNoWritableInterfaceSaysSoAndNamesItself(string vendor, string model)
    {
        var vm = Build(vendor, model, providers: []);
        vm.SelectedGame = "Roblox";

        vm.VendorNote.Should().NotBeEmpty();
        vm.VendorNote.Should().Contain(vendor, "the player should see their own hardware named");
        vm.VendorNote.Should().Contain("client",
            "the note has to say what still happens, not only what does not");
    }

    [Fact]
    public void EveryGameHasADriverLayerNowSoTheNoteShowsForFortniteToo()
    {
        var vm = Build("AMD", "Radeon RX 6600", providers: []);
        vm.SelectedGame = "Fortnite";

        vm.VendorNote.Should().NotBeEmpty();
        vm.VendorNote.Should().Contain("Fortnite");
    }

    [Fact]
    public void AWholeGpuVendorSaysTheProfileReachesEveryGame()
    {
        var vm = Build("AMD", "Radeon RX 6600", providers: [new FakeProvider("AMD", wholeGpu: true)]);
        vm.SelectedGame = "Fortnite";

        vm.VendorNote.Should().NotBeEmpty();
        vm.VendorNote.Should().Contain("every game");
        vm.VendorNote.Should().NotContain("no driver interface");
    }

    [Fact]
    public void APerGameVendorNeedsNoNoteExceptMinecraftsJavawCaveat()
    {
        var vm = Build("NVIDIA", "RTX 3060 Ti", providers: [new FakeProvider("NVIDIA", wholeGpu: false)]);
        vm.SelectedGame = "Fortnite";
        vm.VendorNote.Should().StartWith("NVIDIA: per game").And.NotContain("javaw.exe");

        vm.SelectedGame = "Minecraft";
        vm.VendorNote.Should().Contain("javaw.exe");
    }

    [Fact]
    public void ThePageOpensOnAGameThatIsInstalledWhenThereIsOne()
    {
        var snapshot = new SystemSnapshot(new("Windows 11", "10.0.26100", 26100), new("CPU", "AMD"),
            [new("RTX 3060 Ti", "NVIDIA", "1.0")], new(8_000_000_000), new(false, true, "Balanced"),
            new Dictionary<string, DetectedGame> { ["Roblox"] = new("Roblox", true, null) }, []);

        var vm = new GameProfilesViewModel(snapshot, new TransactionCoordinator(new MemoryStore()), []);

        vm.SelectedGame.Should().Be("Roblox", "Fortnite is not here and Roblox is");
    }

    [Fact]
    public void ChangingTheSelectedGameReRaisesTheNote()
    {
        // The note is derived, so without a change notification it would be correct and invisible.
        var vm = Build("AMD", "Radeon RX 6600", providers: []);
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.SelectedGame = "Roblox";

        raised.Should().Contain(nameof(GameProfilesViewModel.VendorNote));
    }

    private static GameProfilesViewModel Build(string vendor, string model, IReadOnlyList<IGpuDriverProfileProvider> providers)
    {
        var snapshot = new SystemSnapshot(new("Windows 11", "10.0.26100", 26100), new("CPU", "AMD"),
            [new(model, vendor, "1.0")], new(8_000_000_000), new(false, true, "Balanced"),
            new Dictionary<string, DetectedGame>(), []);
        return new GameProfilesViewModel(snapshot, new TransactionCoordinator(new MemoryStore()), providers);
    }

    private sealed class FakeProvider(string vendor, bool wholeGpu) : IGpuDriverProfileProvider
    {
        public string Vendor => vendor;
        public bool IsWholeGpu => wholeGpu;
        public bool IsAvailable(SystemSnapshot snapshot) => true;
        public DriverProfilePreview Describe(GamePerformanceProfile profile, GameDriverTarget target) => new(["row"], []);
        public ITweakOperation CreateOperation(GamePerformanceProfile profile, GameDriverTarget target) => throw new NotSupportedException();
        public string ScopeNote(GameDriverTarget target) => wholeGpu ? "Applies to every game on this GPU." : "Per game.";
        public IDriverProfileBaseline Baseline(GameDriverTarget target) => new NoBaseline();
    }

    private sealed class NoBaseline : IDriverProfileBaseline
    {
        public bool HasBaseline => false;
        public int PendingCount => 0;
        public Task<int> ResetAsync(CancellationToken cancellationToken) => Task.FromResult(0);
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
}
