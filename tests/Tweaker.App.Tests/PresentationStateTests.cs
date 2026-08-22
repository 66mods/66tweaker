
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class PresentationStateTests
{
    [Fact]
    public void Shell_DefaultsToHomeAndAcceptsNavigationIndex()
    {
        var shell = CreateShell();
        shell.SelectedPageIndex.Should().Be(0);
        shell.SelectedPageIndex = 6;
        shell.SelectedPageIndex.Should().Be(6);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(10, 9)]
    public void SelectedPageIndex_ClampsToKnownPages(int requested, int expected)
    {
        var shell = CreateShell();

        shell.SelectedPageIndex = requested;

        shell.SelectedPageIndex.Should().Be(expected);
    }



    [Fact]
    public async Task UnsupportedOperation_RemainsVisibleDisabledWithCompatibilityReason()
    {
        var vm = CreateOptimization(new UnsupportedOperation());
        await vm.LoadAsync(CancellationToken.None);

        var item = vm.Items.Should().ContainSingle().Which;
        item.IsAvailable.Should().BeFalse();
        item.IsSelected = true;
        item.IsSelected.Should().BeFalse();
        var reason = typeof(TweakItemViewModel).GetProperty("AvailabilityReason");
        reason.Should().NotBeNull();
        ((string?)reason!.GetValue(item)).Should().Contain("not supported");
    }

    [Fact]
    public void NoDetectedGames_ExposesRescanNextStep()
    {
        var vm = new GameProfilesViewModel(NoGamesSnapshot(), new TransactionCoordinator(Store()));

        var hasDetectedGames = typeof(GameProfilesViewModel).GetProperty("HasDetectedGames");
        hasDetectedGames.Should().NotBeNull();
        ((bool)hasDetectedGames!.GetValue(vm)!).Should().BeFalse();
        var noGamesMessage = typeof(GameProfilesViewModel).GetProperty("NoGamesMessage");
        noGamesMessage.Should().NotBeNull();
        // Asserts the behaviour rather than the sentence: an empty state has to tell the user what to do
        // next, and pinning the exact wording only makes copy edits look like regressions.
        var message = (string?)noGamesMessage!.GetValue(vm);
        message.Should().NotBeNullOrWhiteSpace();
        message.Should().ContainAny("scan", "Scan");
        message.Should().Contain("Launch", "the fix is to launch a game once so Windows records its location");
    }

    [Fact]
    public void SelectedGame_GatesApplyBySelectedDetection()
    {
        var vm = new GameProfilesViewModel(Snapshot(), new TransactionCoordinator(Store()));
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        var canApplySelectedGame = typeof(GameProfilesViewModel).GetProperty("CanApplySelectedGame");
        canApplySelectedGame.Should().NotBeNull();
        ((bool)canApplySelectedGame!.GetValue(vm)!).Should().BeFalse();

        vm.SelectedGame = "Valorant";

        ((bool)canApplySelectedGame.GetValue(vm)!).Should().BeTrue();
        notifications.Should().Contain("CanApplySelectedGame");

        vm.SelectedGame = "Fortnite";

        ((bool)canApplySelectedGame.GetValue(vm)!).Should().BeFalse();
    }

    [Fact]
    public void Requirements_UseCleanVisibleSeparators()
    {
        var item = new TweakItemViewModel(new Operation("safe", RiskLevel.Safe), "0", "1");

        item.Requirements.Should().Be($"Windows {(char)0x00B7} Medium impact {(char)0x00B7} Safe");
    }

    [Fact]
    public async Task EmptyHistory_ExposesActionableEmptyState()
    {
        var vm = new HistoryViewModel(Store());
        await vm.LoadAsync(CancellationToken.None);
        vm.HasItems.Should().BeFalse();
        vm.EmptyMessage.Should().Be("No optimization sessions yet. Apply a profile to create a restorable snapshot.");
    }

    [Fact]
    public async Task EmptyRestoreHistory_DisablesLatestRestoreAction()
    {
        var vm = CreateRestore();
        await vm.LoadAsync(CancellationToken.None);
        vm.HasRestorableSession.Should().BeFalse();
    }

    [Fact]
    public void LegacySearch_FiltersBothMappingCollections()
    {
        var shell = CreateShell();
        shell.LegacySearchText = "ELAM";
        shell.FilteredLegacyAreas.Should().BeEmpty();
        shell.FilteredBlockedLegacy.Should().ContainSingle(x => x.Name == "Disable ELAM");
    }

    [Fact]
    public async Task ReloadedItems_NoLongerNotifyRiskAcknowledgementState()
    {
        var vm = CreateOptimization(new Operation("advanced", RiskLevel.Advanced));
        await vm.LoadAsync(CancellationToken.None);
        var removedItem = vm.Items.Single();
        await vm.LoadAsync(CancellationToken.None);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        removedItem.IsSelected = true;

        notifications.Should().NotContain(nameof(OptimizationViewModel.SelectedEffectCount));
    }
    [Fact]
    public void ReduceMotion_NotifiesOnlyWhenThePreferenceChanges()
    {
        var shell = CreateShell();
        var notifications = new List<string?>();
        shell.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        shell.ReduceMotion = !shell.ReduceMotion;
        shell.ReduceMotion = shell.ReduceMotion;

        notifications.Should().ContainSingle(nameof(ShellViewModel.ReduceMotion));
    }
    [Fact]
    public void LoadSnapshot_ExposesHardwareHeadline()
    {
        var vm = new HomeViewModel(new Scanner());
        vm.LoadSnapshot(Snapshot());
        vm.HardwareHeadline.Should().Be(vm.SystemSummary);
    }

    private static SystemSnapshot Snapshot() => new(
        new("Windows 11", "10", 26100), new("CPU", "AMD"),
        [new("GPU", "NVIDIA", "1")], new(16_000_000_000),
        new(false, true, "Balanced"),
        new Dictionary<string, DetectedGame>
        {
            ["Fortnite"] = new("Fortnite", false, null),
            ["Valorant"] = new("Valorant", true, @"C:\missing-test-config.ini")
        }, []);

    private static SystemSnapshot NoGamesSnapshot() => new(
        new("Windows 11", "10", 26100), new("CPU", "AMD"),
        [new("GPU", "NVIDIA", "1")], new(16_000_000_000),
        new(false, true, "Balanced"),
        new Dictionary<string, DetectedGame>
        {
            ["Fortnite"] = new("Fortnite", false, null),
            ["Valorant"] = new("Valorant", false, null),
            ["GTA V"] = new("GTA V", false, null),
            ["Minecraft"] = new("Minecraft", false, null),
            ["Roblox"] = new("Roblox", false, null)
        }, []);

    private static MemoryStore Store() => new();
    private static ShellViewModel CreateShell()
    {
        var store = Store();
        return new ShellViewModel(new Scanner(), [], new TransactionCoordinator(store));
    }
    private static OptimizationViewModel CreateOptimization(ITweakOperation operation)
    {
        var store = Store();
        return OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(store), Snapshot());
    }
    private static RestoreViewModel CreateRestore()
    {
        var store = Store();
        return new RestoreViewModel(store, new TransactionCoordinator(store), new Dictionary<string, ITweakOperation>());
    }

    private sealed class Scanner : ISystemScanner
    {
        public Task<SystemSnapshot> ScanAsync(CancellationToken token) => Task.FromResult(Snapshot());
    }
    private sealed class Operation(string id, RiskLevel risk) : ITweakOperation, IRequestedValueProvider
    {
        public TweakDescriptor Descriptor { get; } = new(id, id, TweakCategory.Windows, ImpactLevel.Medium, risk, false, false);
        public string RequestedValue => "1";
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("0");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class UnsupportedOperation : ITweakOperation, IRequestedValueProvider
    {
        public TweakDescriptor Descriptor { get; } = new("unsupported", "Unsupported operation", TweakCategory.Windows, ImpactLevel.Medium, RiskLevel.Safe, false, false);
        public string RequestedValue => "1";
        public bool IsSupported(SystemSnapshot snapshot) => false;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("0");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class MemoryStore : ITransactionStore, ITransactionHistoryStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord record, CancellationToken token) => SaveAsync(record, token);
        public Task SaveAsync(TransactionRecord record, CancellationToken token) { records[record.Id] = record; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
        public Task<IReadOnlyList<TransactionRecord>> LoadRecentAsync(int limit, CancellationToken token) => Task.FromResult<IReadOnlyList<TransactionRecord>>([]);
    }
}
