using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.App.Tests;

/// <summary>
/// The view-model facts the 2.0 screens are drawn from: the card meta line and meter, the run button's
/// count, the Home chips and the Games slider.
/// </summary>
public sealed class ShellKitViewModelTests
{
    private static OptimizationViewModel BuildOptimization() =>
        OptimizationViewModel.CreateForTests(
            LegacyBundleOperation.CreateCategories(new FixedProcessRunner()),
            new TransactionCoordinator(new InMemoryStore()), Snapshot());

    [Fact]
    public async Task ACardSaysItsCountAndTheOneFactThatMatters()
    {
        var vm = BuildOptimization();
        await vm.LoadAsync(CancellationToken.None);
        var byId = vm.Categories.ToDictionary(x => x.Category.Id);

        byId["input"].Meta.Should().Be("48 · instant, fully reversible");
        byId["power"].Meta.Should().MatchRegex(@"^\d+ · restart$");
        byId["debloat"].Meta.Should().MatchRegex(@"^\d+ · \d+ cannot be undone$");
        byId["debloat"].PillText.Should().Be("Risky");
        byId["debloat"].PillKind.Should().Be("Risky");
        byId["debloat"].ImpactSegments.Should().Be(5, "a risky group always reads full");
        byId["input"].ImpactSegments.Should().Be(2);
        byId["ram"].ImpactSegments.Should().Be(1);
        byId["gpu"].ImpactSegments.Should().Be(5);
        byId["input"].PillText.Should().Be("Ready");

        byId["input"].State = CategoryRunState.Applied;
        byId["input"].PillText.Should().Be("Applied");
        byId["input"].PillKind.Should().Be("Applied");
    }

    [Fact]
    public async Task TheRunButtonCountsOnlyTheTickedGroups()
    {
        var vm = BuildOptimization();
        await vm.LoadAsync(CancellationToken.None);

        vm.SelectedCategories.Should().HaveCount(6, "every group but the risky one starts ticked");
        vm.SelectedRequiresRestart.Should().BeTrue();
        var all = vm.SelectedChangeCount;
        vm.ClearCategoriesCommand.Execute(null);

        vm.SelectedChangeCount.Should().Be(0);
        vm.RunLabel.Should().Be("0 changes");
        vm.SelectedSummary.Should().Be("Nothing selected");
        vm.SelectedCategories.Should().BeEmpty();
        vm.SelectedRequiresRestart.Should().BeFalse();

        vm.Categories.Single(x => x.Category.Id == "input").IsSelected = true;
        vm.SelectedChangeCount.Should().Be(48);
        vm.RunLabel.Should().Be("48 changes");
        vm.SelectedSummary.Should().Be("1 selected · 48 changes");
        vm.SelectedCategories.Select(x => x.Category.Id).Should().Equal("input");
        vm.SelectedRequiresRestart.Should().BeFalse("Mouse & Keyboard is instant");

        vm.SelectRecommendedCommand.Execute(null);
        vm.SelectedChangeCount.Should().Be(all);
        vm.Categories.Single(x => x.IsExperimental).IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task SelectRecommendedSkipsGroupsAlreadyAppliedThisSession()
    {
        var vm = BuildOptimization();
        await vm.LoadAsync(CancellationToken.None);
        var power = vm.Categories.Single(x => x.Category.Id == "power");
        power.State = CategoryRunState.Applied;

        vm.SelectRecommendedCommand.Execute(null);

        power.IsSelected.Should().BeFalse();
        vm.SelectedCategories.Should().NotContain(power);
    }

    [Fact]
    public async Task RunningWithNothingTickedIsAWarningNotAWorkerLaunch()
    {
        var vm = BuildOptimization();
        await vm.LoadAsync(CancellationToken.None);
        vm.ClearCategoriesCommand.Execute(null);

        await vm.RunSelectedCommand.ExecuteAsync();

        vm.Progress.OutcomeKind.Should().Be("Warning");
        vm.LastResult.Should().Be("No groups selected.");
    }

    [Fact]
    public void TheUndoButtonNamesItselfAndTheTimeOfTheLastRun()
    {
        // Composed here because a StringFormat on Button.Content is silently ignored by WPF.
        LastOptimizationSummary.None.UndoLabel.Should().Be("Undo last run");
        new LastOptimizationSummary(true, "Applied", "Success", "Today, 3:12 PM", 4, 0).UndoLabel
            .Should().Be("Undo last run · Today, 3:12 PM");
    }

    [Fact]
    public void TheScoreDetailReadsAsTheLineUnderTheNumber()
    {
        var vm = BuildOptimization();

        vm.ScoreDetail.Should().Be("not measured yet");
        vm.MeasuredCount.Should().Be(0);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060 Ti", "RTX 3060 Ti")]
    [InlineData("AMD Radeon RX 6600", "RX 6600")]
    [InlineData("Intel(R) Arc(TM) A750 Graphics", "Arc A750")]
    [InlineData("Intel(R) Core(TM) i5-9400F CPU @ 2.90GHz", "Core i5-9400F")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", "Ryzen 7 5800X 8-Core")]
    public void HardwareChipsDropTheMarketingPrefix(string full, string chip) =>
        HomeViewModel.ShortHardwareName(full).Should().Be(chip);

    [Fact]
    public void TheHomeChipsComeFromTheSnapshot()
    {
        var home = new HomeViewModel(new NoScanner());

        home.LoadSnapshot(Snapshot());

        home.GpuChip.Should().Be("RTX 3060 Ti");
        home.CpuChip.Should().Be("Ryzen 7 5800X");
        home.WindowsChip.Should().Be("Win 11");
    }

    [Fact]
    public void TheSliderIndexAndTheProfileAgreeBothWays()
    {
        var vm = new GameProfilesViewModel(Snapshot(), new TransactionCoordinator(new InMemoryStore()), []);

        vm.SelectedProfileIndex.Should().Be(0);
        vm.SelectedProfileName.Should().Be("Balanced");

        vm.SelectedProfileIndex = 3;
        vm.SelectedProfile.Should().Be(GamePerformanceProfile.UltraPotato);
        vm.SelectedProfileName.Should().Be("Ultra Potato");
        vm.SelectedProfileDescription.Should().Be("Maximum frames, worst image · Fortnite");
        vm.SelectedFacts.ImagePercent.Should().Be(25);
        vm.SelectedFacts.FramesLabel.Should().Be("+70%");

        vm.SelectedProfileIndex = 9;
        vm.SelectedProfileIndex.Should().Be(3, "the slider has four stops");
        vm.SelectedProfile = GamePerformanceProfile.Competitive;
        vm.SelectedProfileIndex.Should().Be(1);
    }

    [Fact]
    public void TheVendorNoteNamesTheGpuWhenNoDriverCanBeWritten()
    {
        var vm = new GameProfilesViewModel(Snapshot(gpuVendor: "AMD", gpuName: "Radeon RX 6600"),
            new TransactionCoordinator(new InMemoryStore()), []);

        vm.VendorNote.Should().Contain("AMD").And.Contain("client");
    }

    private static SystemSnapshot Snapshot(string gpuVendor = "NVIDIA", string gpuName = "NVIDIA GeForce RTX 3060 Ti") =>
        new(new("Windows 11 Pro", "10.0.26100", 26100), new("AMD Ryzen 7 5800X", "AMD"),
            [new(gpuName, gpuVendor, "1.0")], new(32_000_000_000), new(false, true, "Balanced"),
            new Dictionary<string, DetectedGame>(), []);

    private sealed class NoScanner : Tweaker.Domain.Abstractions.ISystemScanner
    {
        public Task<SystemSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot());
    }

    private sealed class InMemoryStore : Tweaker.Domain.Abstractions.ITransactionStore
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
