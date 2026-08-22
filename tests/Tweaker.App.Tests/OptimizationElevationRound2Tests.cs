using FluentAssertions;
using Tweaker.App.Services;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Privilege;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class OptimizationElevationRound2Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-composite-r2", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LocalSuccessThenFailure_RollsBackAllLocalMutationsBeforeProtectedPhase()
    {
        var privileged = new Operation("power.known", requiresElevation: true);
        var first = new Operation("local.first");
        var second = new Operation("local.second") { VerifyResult = false };
        var launcher = new Launcher();
        var composites = new InMemoryCompositeTransactionStore();
        var viewModel = ViewModel([privileged, first, second], launcher, composites);
        await viewModel.LoadAsync(CancellationToken.None);

        await FluentActions.Invoking(() => viewModel.ApplySelectedAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*all completed mutations were exactly rolled back*");

        first.Current.Should().Be("original");
        first.RestoreCount.Should().Be(1);
        launcher.Rollbacks.Should().ContainSingle();
        composites.Records.Single().Status.Should().Be(CompositeTransactionStatus.RolledBack);
    }

    [Fact]
    public async Task LocalRollbackFailure_IsDurableAndProtectedRollbackDoesNotStart()
    {
        var privileged = new Operation("power.known", requiresElevation: true);
        var first = new Operation("local.first") { FailRestoreAfterSuccessfulApply = true };
        var second = new Operation("local.second") { VerifyResult = false };
        var launcher = new Launcher();
        var composites = new InMemoryCompositeTransactionStore();
        var viewModel = ViewModel([privileged, first, second], launcher, composites);
        await viewModel.LoadAsync(CancellationToken.None);

        await FluentActions.Invoking(() => viewModel.ApplySelectedAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Recover local transaction*");
        composites.Records.Single().Status.Should().Be(CompositeTransactionStatus.NeedsLocalRecovery);
        launcher.Rollbacks.Should().BeEmpty();
    }

    [Fact]
    public async Task ProtectedRollbackFailure_RetainsBothPhaseIdsForRestartRecovery()
    {
        var privileged = new Operation("power.known", requiresElevation: true);
        var failing = new Operation("local.fails") { VerifyResult = false };
        var launcher = new Launcher { FailRollback = true };
        var store = new JsonCompositeTransactionStore(root);
        var viewModel = ViewModel([privileged, failing], launcher, store);
        await viewModel.LoadAsync(CancellationToken.None);

        await FluentActions.Invoking(() => viewModel.ApplySelectedAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*protected recovery*");

        var afterRestart = new JsonCompositeTransactionStore(root);
        var incomplete = await afterRestart.ListIncompleteAsync(10, CancellationToken.None);
        incomplete.Should().ContainSingle();
        incomplete[0].Status.Should().Be(CompositeTransactionStatus.NeedsProtectedRecovery);
        incomplete[0].PrivilegedTransactionId.Should().Be(launcher.TransactionId);
        incomplete[0].LocalTransactionId.Should().NotBeNull();
        (await afterRestart.LoadAsync(incomplete[0].Id, CancellationToken.None)).Should().BeEquivalentTo(incomplete[0]);
    }

    [Fact]
    public async Task StartupSurfacesLatestIncompleteComposite()
    {
        var store = new InMemoryCompositeTransactionStore();
        var record = new CompositeTransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow,
            CompositeTransactionStatus.NeedsProtectedRecovery, Guid.NewGuid(), Guid.NewGuid(), "retry protected phase");
        await store.CreateAsync(record, CancellationToken.None);
        var viewModel = ViewModel([new Operation("local.safe")], new Launcher(), store);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.LastResult.Should().Contain(record.Id.ToString("N")).And.Contain("NeedsProtectedRecovery");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalBeginFailureOrCancellationAfterProtectedSuccess_CompensatesProtectedPhase(bool cancel)
    {
        var privileged = new Operation("power.known", requiresElevation: true);
        var local = new Operation("local.safe");
        var launcher = new Launcher();
        var composites = new InMemoryCompositeTransactionStore();
        var localStore = new Store { BeginError = cancel
            ? new OperationCanceledException("injected cancellation")
            : new IOException("injected write failure") };
        var viewModel = ViewModel([privileged, local], launcher, composites, localStore);
        await viewModel.LoadAsync(CancellationToken.None);
        await FluentActions.Invoking(() => viewModel.ApplySelectedAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*exactly rolled back*");
        local.Current.Should().Be("original");
        launcher.Rollbacks.Should().ContainSingle();
        composites.Records.Single().Status.Should().Be(CompositeTransactionStatus.RolledBack);
    }

    [Fact]
    public async Task CompositeExpectedRevisionRejectsStaleTransition()
    {
        var store = new InMemoryCompositeTransactionStore();
        var created = new CompositeTransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow,
            CompositeTransactionStatus.PrivilegedPending, Guid.NewGuid(), null, "pending");
        await store.CreateAsync(created, CancellationToken.None);
        await store.TransitionAsync(created, created with { Status = CompositeTransactionStatus.Completed,
            Message = "done", Revision = 1 }, CancellationToken.None);
        await FluentActions.Invoking(() => store.TransitionAsync(created, created with {
            Status = CompositeTransactionStatus.PrivilegedRollbackPending, Message = "stale", Revision = 1 }, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*expected-state*");
    }

    [Fact]
    public void CompositeCodec_RejectsUnknownAndDuplicateFields()
    {
        var record = new CompositeTransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow,
            CompositeTransactionStatus.NeedsProtectedRecovery, Guid.NewGuid(), Guid.NewGuid(), "retry");
        var bytes = CompositeRecordCodec.Write(record);
        var json = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"message\":", "\"unknown\":true,\"message\":");
        FluentActions.Invoking(() => CompositeRecordCodec.Read(System.Text.Encoding.UTF8.GetBytes(json), record.Id))
            .Should().Throw<InvalidDataException>();
        json = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"status\":", "\"status\":1,\"status\":");
        FluentActions.Invoking(() => CompositeRecordCodec.Read(System.Text.Encoding.UTF8.GetBytes(json), record.Id))
            .Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void SecurityConfirmationAndStatusStrings_AreLiteralAsciiAndShowExactRisks()
    {
        var descriptor = new TweakDescriptor("power.known", "Known power plan", TweakCategory.Power,
            ImpactLevel.High, RiskLevel.Experimental, true, true);
        App.FormatOperationLine(descriptor).Should().Be(
            "- Known power plan [power.known] - Experimental, High impact; restart required (never automatic)");
        var confirmation = App.BuildRecoveryConfirmation(PrivilegedWorkerAction.Resume, Guid.Empty, [descriptor]);
        confirmation.Should().Contain("power.known").And.Contain("Experimental").And.Contain("reapplies the exact original closed operations");
        confirmation.Should().NotContain("Р").And.NotContain("В·");
    }

    private static OptimizationViewModel ViewModel(IReadOnlyList<ITweakOperation> operations,
        IOptimizationElevationLauncher launcher, ICompositeTransactionStore composites, ITransactionStore? localStore = null) =>
        OptimizationViewModel.CreateForTests(operations, new TransactionCoordinator(localStore ?? new Store()), Snapshot(),
            launcher, new Confirmation(), composites);

    private static SystemSnapshot Snapshot() => new(new("Windows", "10", 26100), new("CPU", "AMD"), [],
        new(16_000_000_000), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class Confirmation : IOptimizationConfirmation
    {
        public bool Confirm(OptimizationReview review) => true;
    }
    private sealed class Launcher : IOptimizationElevationLauncher
    {
        public bool FailRollback { get; set; }
        public Guid? TransactionId { get; private set; }
        public List<Guid> Rollbacks { get; } = [];
        public Task<Guid> LaunchAsync(Guid transactionId, IReadOnlyList<PrivilegedOperationRequest> operations,
            CancellationToken cancellationToken)
        {
            TransactionId = transactionId;
            return Task.FromResult(transactionId);
        }
        public Task<Guid> RollbackAsync(Guid transactionId, CancellationToken cancellationToken)
        {
            Rollbacks.Add(transactionId);
            return FailRollback ? Task.FromException<Guid>(new InvalidOperationException("injected protected failure")) : Task.FromResult(transactionId);
        }
    }
    private sealed class Store : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Exception? BeginError { get; init; }
        public Task BeginAsync(TransactionRecord record, CancellationToken cancellationToken) =>
            BeginError is null ? SaveAsync(record, cancellationToken) : Task.FromException(BeginError);
        public Task SaveAsync(TransactionRecord record, CancellationToken cancellationToken)
        {
            records[record.Id] = record;
            return Task.CompletedTask;
        }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(records.Values.LastOrDefault(x => x.Status is TransactionStatus.InProgress or TransactionStatus.PartiallyRolledBack));
    }
    private sealed class Operation(string id, bool requiresElevation = false) : ITweakOperation, IRequestedValueProvider
    {
        public string Current { get; private set; } = "original";
        public int RestoreCount { get; private set; }
        public bool VerifyResult { get; set; } = true;
        public bool FailRestoreAfterSuccessfulApply { get; set; }
        public string RequestedValue => "compiled";
        public TweakDescriptor Descriptor { get; } = new(id, id, TweakCategory.Power, ImpactLevel.Low,
            RiskLevel.Safe, requiresElevation, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken) { Current = requestedValue; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
            Task.FromResult(VerifyResult && Current == requestedValue);
        public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
        {
            RestoreCount++;
            if (FailRestoreAfterSuccessfulApply && VerifyResult)
                throw new InvalidOperationException("injected local rollback failure");
            Current = originalValue!;
            return Task.CompletedTask;
        }
    }
}
