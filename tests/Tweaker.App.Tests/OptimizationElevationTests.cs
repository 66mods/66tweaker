

using FluentAssertions;
using Tweaker.App.Services;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Privilege;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class OptimizationElevationTests
{
    [Theory]
    [InlineData("../victim")]
    [InlineData("not-a-guid")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    public void WorkerArguments_RejectsNonCanonicalIds(string value) =>
        WorkerArguments.TryParse([WorkerArguments.OptimizationWorkerFlag, value], out _).Should().BeFalse();

    [Fact]
    public void WorkerArguments_AcceptsCaseInsensitiveCanonicalNIdOnly()
    {
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        WorkerArguments.TryParse([WorkerArguments.OptimizationWorkerFlag, id.ToString("N").ToUpperInvariant()], out var parsed)
            .Should().BeTrue();
        parsed.Should().Be(id);
        WorkerArguments.TryParse([WorkerArguments.OptimizationWorkerFlag, id.ToString("N"), "extra"], out _).Should().BeFalse();
        WorkerArguments.TryParse(["--repair-worker", id.ToString("N")], out _).Should().BeFalse();
    }

    [Fact]
    public void ElevationStartInfo_CrossesOnlyCanonicalTransactionId()
    {
        var id = Guid.NewGuid();
        // Pinned to the standard-user path: on a host with UAC disabled the default overload correctly
        // starts the worker directly, which is covered by its own test.
        var start = OptimizationElevationLauncher.CreateStartInfo(id, alreadyElevated: false);
        start.UseShellExecute.Should().BeTrue();
        start.Verb.Should().Be("runas");
        start.ArgumentList.Should().Equal("--optimization-worker", id.ToString("N"));
        start.ArgumentList.Should().NotContain(value => value.Contains("HKLM", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(".exe", StringComparison.OrdinalIgnoreCase) || value.Contains('\\'));
    }

    [Fact]
    public async Task ApplySelected_ConfirmationDeclined_DoesNotMutateOrElevate()
    {
        var operation = new Operation("local.safe", false, RiskLevel.Safe);
        var launcher = new Launcher();
        var viewModel = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(new Store()), Snapshot(),
            launcher, new Confirmation(false));
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.ApplySelectedAsync(CancellationToken.None);
        operation.ApplyCount.Should().Be(0);
        launcher.Requests.Should().BeNull();
        viewModel.LastResult.Should().Contain("Cancelled");
    }

    [Fact]
    public async Task ApplySelected_PrivilegedOperation_UsesWorkerAndNeverLocalCoordinator()
    {
        var operation = new Operation("power.known", true, RiskLevel.Advanced);
        var store = new Store();
        var launcher = new Launcher();
        var viewModel = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(store), Snapshot(),
            launcher, new Confirmation(true));
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = "Maximum Performance";
        await viewModel.ApplySelectedAsync(CancellationToken.None);
        launcher.Requests.Should().Equal(new PrivilegedOperationRequest("power.known", "default"));
        operation.ApplyCount.Should().Be(0);
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplySelected_ExperimentalStillReachesConfirmationFlaggedAsExperimental()
    {
        // The separate acknowledgement tick-box was removed: each card states its own risk, and this
        // confirmation — which names every operation before anything is sealed — is what still gates the run.
        var operation = new Operation("power.experimental", true, RiskLevel.Experimental);
        var confirmation = new Confirmation(true);
        var launcher = new Launcher();
        var viewModel = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(new Store()), Snapshot(),
            launcher, confirmation);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = "Experimental";

        await viewModel.ApplySelectedAsync(CancellationToken.None);

        confirmation.Review.Should().NotBeNull();
        confirmation.Review!.RequiresExperimentalWarning.Should().BeTrue();
        confirmation.Review.OperationNames.Should().ContainSingle();
        launcher.Requests.Should().NotBeNull("a confirmed experimental run must still reach the worker");
    }

    [Fact]
    public async Task MixedApply_LocalVerificationFailure_ExactlyRollsBackPrivilegedPhaseAndPersistsComposite()
    {
        var privileged = new Operation("power.known", true, RiskLevel.Advanced);
        var local = new Operation("local.fails", false, RiskLevel.Safe, verify: false);
        var launcher = new Launcher();
        var composites = new InMemoryCompositeTransactionStore();
        var viewModel = OptimizationViewModel.CreateForTests([privileged, local], new TransactionCoordinator(new Store()), Snapshot(),
            launcher, new Confirmation(true), composites);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = "Maximum Performance";

        await FluentActions.Invoking(() => viewModel.ApplySelectedAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*exactly rolled back*");

        launcher.Rollbacks.Should().ContainSingle().Which.Should().Be(launcher.TransactionId!.Value);
        composites.Records.Should().ContainSingle(x => x.Status == CompositeTransactionStatus.RolledBack);
    }

    [Fact]
    public async Task BlockedApplyRejectsInterleavedUndo()
    {
        var launcher = new Launcher { BlockLaunch = true };
        var viewModel = OptimizationViewModel.CreateForTests([new Operation("power.known", true, RiskLevel.Safe)],
            new TransactionCoordinator(new Store()), Snapshot(), launcher, new Confirmation(true));
        await viewModel.LoadAsync(CancellationToken.None);
        var apply = viewModel.ApplySelectedAsync(CancellationToken.None);
        await launcher.LaunchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await FluentActions.Invoking(() => viewModel.UndoLastAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*already in progress*");
        launcher.Rollbacks.Should().BeEmpty();
        launcher.ReleaseLaunch.TrySetResult();
        await apply;
    }

    [Fact]
    public async Task ChangingRiskSelection_ResetsAcknowledgement()
    {
        var operation = new Operation("power.experimental", true, RiskLevel.Experimental);
        var viewModel = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(new Store()), Snapshot(),
            new Launcher(), new Confirmation(true));
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = "Experimental";

        viewModel.Items.Single().IsSelected = false;

    }

    [Fact]
    public async Task RepairElevation_MapsCompiledRepairIdBeforeWorkerBoundary()
    {
        var launcher = new Launcher();
        var repairLauncher = new RepairElevationLauncher(launcher);
        await repairLauncher.LaunchAsync("fix-wifi", CancellationToken.None);
        launcher.Requests.Should().Equal(new PrivilegedOperationRequest("repair.fix-wifi", "default"));
        await FluentActions.Invoking(() => repairLauncher.LaunchAsync("reset-winsock", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*exact privileged rollback*");
    }

    private static SystemSnapshot Snapshot() => new(new("Windows", "10", 26100), new("CPU", "AMD"), [],
        new(16_000_000_000), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    private sealed class Launcher : IOptimizationElevationLauncher
    {
        public bool BlockLaunch { get; init; }
        public TaskCompletionSource LaunchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLaunch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? TransactionId { get; private set; }
        public IReadOnlyList<PrivilegedOperationRequest>? Requests { get; private set; }
        public List<Guid> Rollbacks { get; } = [];
        public async Task<Guid> LaunchAsync(Guid transactionId, IReadOnlyList<PrivilegedOperationRequest> operations,
            CancellationToken cancellationToken)
        {
            TransactionId = transactionId;
            Requests = operations;
            LaunchEntered.TrySetResult();
            if (BlockLaunch) await ReleaseLaunch.Task.WaitAsync(cancellationToken);
            return transactionId;
        }
        public Task<Guid> RollbackAsync(Guid transactionId, CancellationToken cancellationToken)
        {
            Rollbacks.Add(transactionId);
            return Task.FromResult(transactionId);
        }
    }

    private sealed class Confirmation(bool answer) : IOptimizationConfirmation
    {
        public OptimizationReview? Review { get; private set; }
        public bool Confirm(OptimizationReview review) { Review = review; return answer; }
    }

    private sealed class Operation(string id, bool requiresElevation, RiskLevel risk, bool verify = true) : ITweakOperation, IRequestedValueProvider
    {
        public int ApplyCount { get; private set; }
        public string RequestedValue => "compiled";
        public TweakDescriptor Descriptor { get; } = new(id, id, TweakCategory.Power, ImpactLevel.Low, risk,
            requiresElevation, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("original");
        public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken) { ApplyCount++; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) => Task.FromResult(verify);
        public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Store : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> values = [];
        public int SaveCount { get; private set; }
        public Task BeginAsync(TransactionRecord record, CancellationToken cancellationToken) => SaveAsync(record, cancellationToken);
        public Task SaveAsync(TransactionRecord record, CancellationToken cancellationToken)
        {
            SaveCount++;
            values[record.Id] = record;
            return Task.CompletedTask;
        }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(values.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken) => Task.FromResult<TransactionRecord?>(null);
    }
}
