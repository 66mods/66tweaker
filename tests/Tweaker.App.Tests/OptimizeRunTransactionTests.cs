using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;
using Tweaker.Domain.Privilege;

namespace Tweaker.App.Tests;

/// <summary>
/// One press of Optimize is one transaction: one review, one administrator prompt, one worker run. And the
/// groups the user ticked are theirs; a run must not rewrite them.
/// </summary>
public sealed class OptimizeRunTransactionTests
{
    [Fact]
    public async Task RunSelected_LaunchesTheWorkerOnceForEveryTickedGroup()
    {
        var launcher = new Launcher();
        var vm = Build(launcher, confirm: true);
        await vm.LoadAsync(CancellationToken.None);
        vm.ClearCategoriesCommand.Execute(null);
        var byId = vm.Categories.ToDictionary(x => x.Category.Id);
        byId["input"].IsSelected = true;
        byId["power"].IsSelected = true;
        byId["ram"].IsSelected = true;

        await vm.RunSelectedCommand.ExecuteAsync();

        launcher.Launches.Should().Be(1, "three groups, one prompt");
        launcher.LastRequestCount.Should().Be(3, "the one launch carries every ticked group, one bundle each");
        byId["input"].State.Should().Be(CategoryRunState.Applied);
        byId["power"].State.Should().Be(CategoryRunState.Applied);
        byId["ram"].State.Should().Be(CategoryRunState.Applied);
        vm.SelectedCategories.Should().BeEmpty("what was just applied comes off the button");
        vm.Progress.OutcomeKind.Should().Be("Success");
    }

    [Fact]
    public async Task ADeclinedReviewLeavesEveryGroupReadyAndTicked()
    {
        var launcher = new Launcher();
        var vm = Build(launcher, confirm: false);
        await vm.LoadAsync(CancellationToken.None);
        var ticked = vm.Categories.Where(x => x.IsSelected).ToArray();
        ticked.Should().HaveCountGreaterThan(1);

        await vm.RunSelectedCommand.ExecuteAsync();

        launcher.Launches.Should().Be(0);
        ticked.Should().OnlyContain(x => x.State == CategoryRunState.Ready && x.IsSelected);
        vm.SelectedCategories.Should().Equal(ticked, "the user's ticks are not the run's scratch state");
        vm.Progress.OutcomeKind.Should().Be("Warning");
    }

    [Fact]
    public async Task ASingleCardRunKeepsTheOtherTicksAndUnticksOnlyItself()
    {
        var launcher = new Launcher();
        var vm = Build(launcher, confirm: true);
        await vm.LoadAsync(CancellationToken.None);
        var byId = vm.Categories.ToDictionary(x => x.Category.Id);
        var before = vm.Categories.Where(x => x.IsSelected).ToArray();

        await byId["input"].RunCommand.ExecuteAsync();

        launcher.Launches.Should().Be(1);
        byId["input"].State.Should().Be(CategoryRunState.Applied);
        byId["input"].IsSelected.Should().BeFalse();
        vm.SelectedCategories.Should().Equal(before.Where(x => x != byId["input"]));
    }

    private static OptimizationViewModel Build(Launcher launcher, bool confirm) =>
        OptimizationViewModel.CreateForTests(LegacyBundleOperation.CreateCategories(new FixedProcessRunner()),
            new TransactionCoordinator(new MemoryStore()), Snapshot(), launcher, new Confirmation(confirm));

    private static SystemSnapshot Snapshot() => new(new("Windows 11", "10.0.26100", 26100), new("CPU", "AMD"),
        [new("GPU", "NVIDIA", "1")], new(16_000_000_000), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    private sealed class MemoryStore : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord record, CancellationToken token) { records[record.Id] = record; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord record, CancellationToken token) { records[record.Id] = record; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
    }

    private sealed class Confirmation(bool answer) : IOptimizationConfirmation
    {
        public bool Confirm(OptimizationReview review) => answer;
    }

    private sealed class Launcher : IOptimizationElevationLauncher
    {
        public int Launches { get; private set; }
        public int LastRequestCount { get; private set; }
        public Task<Guid> LaunchAsync(Guid transactionId, IReadOnlyList<PrivilegedOperationRequest> operations, CancellationToken cancellationToken)
        {
            Launches++;
            LastRequestCount = operations.Count;
            return Task.FromResult(transactionId);
        }
        public Task<Guid> RollbackAsync(Guid transactionId, CancellationToken cancellationToken) => Task.FromResult(transactionId);
    }
}
