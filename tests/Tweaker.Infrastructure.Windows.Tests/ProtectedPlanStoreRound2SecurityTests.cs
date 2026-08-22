using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Privilege;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class ProtectedPlanStoreRound2SecurityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-protected-r2", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedResult_RetainsAuthenticatedPlanAndSupportsRealRollback()
    {
        var operation = new Operation("power.known");
        var store = CreateStore("sid-a", "exe-a");
        var dispatcher = Dispatcher(store, operation);
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var plan = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);

        await dispatcher.DispatchAsync(plan, CancellationToken.None);
        File.Exists(Path.Combine(root, $"{plan.TransactionId:N}.running.json")).Should().BeTrue();

        var rolledBack = await dispatcher.RollbackAsync(plan.TransactionId, CancellationToken.None);
        rolledBack.Status.Should().Be(TransactionStatus.RolledBack);
        operation.Current.Should().Be("original");
        File.Exists(Path.Combine(root, $"{plan.TransactionId:N}.journal.json")).Should().BeFalse();
    }

    [Fact]
    public async Task IncompleteRollback_RetainsPartialJournalAndSecondAttemptSucceeds()
    {
        var operation = new Operation("power.known") { FailRestoreCount = 1 };
        var store = CreateStore("sid-a", "exe-a");
        var dispatcher = Dispatcher(store, operation);
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        await dispatcher.DispatchAsync(await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None), CancellationToken.None);

        await FluentActions.Invoking(() => dispatcher.RollbackAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*retry state was retained*");
        (await store.LoadProgressAsync(created.TransactionId, CancellationToken.None))!.Status
            .Should().Be(TransactionStatus.PartiallyRolledBack);
        (await store.LoadResultAsync(created.TransactionId, CancellationToken.None))!.Status
            .Should().Be(TransactionStatus.Completed);

        var retried = await dispatcher.RollbackAsync(created.TransactionId, CancellationToken.None);
        retried.Status.Should().Be(TransactionStatus.RolledBack);
        operation.Current.Should().Be("original");
    }

    [Fact]
    public async Task RollbackVerificationFailureRetainsPartialStateAndCanRetry()
    {
        var operation = new Operation("power.known") { FailRestoreVerificationCount = 1 };
        var store = CreateStore("sid-a", "exe-a");
        var dispatcher = Dispatcher(store, operation);
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        await dispatcher.DispatchAsync(await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None), CancellationToken.None);

        await FluentActions.Invoking(() => dispatcher.RollbackAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*retry state was retained*");
        (await store.LoadProgressAsync(created.TransactionId, CancellationToken.None))!.Status
            .Should().Be(TransactionStatus.PartiallyRolledBack);

        var retried = await dispatcher.RollbackAsync(created.TransactionId, CancellationToken.None);
        retried.Status.Should().Be(TransactionStatus.RolledBack);
        operation.Current.Should().Be("original");
    }

    [Fact]
    public async Task RecoverySurvivesThirtyMinutesAndCompatibleExecutableUpgradeButNewApplyDoesNot()
    {
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero));
        var operation = new Operation("power.known");
        var originalStore = CreateStore("sid-a", "exe-a", clock);
        var created = await originalStore.CreateAsync([new("power.known", "default")], CancellationToken.None);
        await Dispatcher(originalStore, operation).DispatchAsync(
            await originalStore.LoadAndValidateAsync(created.TransactionId, CancellationToken.None), CancellationToken.None);

        var staleDraft = await originalStore.CreateAsync([new("power.known", "default")], CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(1));
        var upgradedStore = CreateStore("sid-a", "exe-b", clock);
        await FluentActions.Invoking(() => upgradedStore.LoadAndValidateAsync(staleDraft.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*Apply draft*");

        var recovered = await Dispatcher(upgradedStore, operation).RollbackAsync(created.TransactionId, CancellationToken.None);
        recovered.Status.Should().Be(TransactionStatus.RolledBack);
    }

    [Fact]
    public async Task HistoryAndDirectLoadsAreIsolatedByInitiatorSid()
    {
        var operation = new Operation("power.known");
        var ownerStore = CreateStore("sid-a", "exe-a");
        var created = await ownerStore.CreateAsync([new("power.known", "default")], CancellationToken.None);
        await Dispatcher(ownerStore, operation).DispatchAsync(
            await ownerStore.LoadAndValidateAsync(created.TransactionId, CancellationToken.None), CancellationToken.None);

        var otherStore = CreateStore("sid-b", "exe-b");
        (await otherStore.LoadRecentAsync(25, CancellationToken.None)).Should().BeEmpty();
        await FluentActions.Invoking(() => otherStore.LoadResultAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>().WithMessage("*initiator*");
    }

    [Fact]
    public async Task ResultEnvelopeRejectsCrossTransactionSubstitution()
    {
        var store = CreateStore("sid-a", "exe-a");
        var first = await CompleteAsync(store, new Operation("power.first"));
        var second = await CompleteAsync(store, new Operation("power.second"));
        File.Copy(Path.Combine(root, $"{first:N}.result.json"),
            Path.Combine(root, $"{second:N}.result.json"), overwrite: true);

        await FluentActions.Invoking(() => store.LoadResultAsync(second, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task CrashBeforeTerminalPublicationLeavesJournalAndCanResumeAfterUpgrade()
    {
        var observer = new ThrowOnceObserver("before-result-publication");
        var operation = new Operation("power.known");
        var store = CreateStore("sid-a", "exe-a", observer: observer);
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var plan = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);

        await FluentActions.Invoking(() => Dispatcher(store, operation).DispatchAsync(plan, CancellationToken.None))
            .Should().ThrowAsync<InjectedTransitionException>();
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.result.json")).Should().BeFalse();
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.journal.json")).Should().BeTrue();

        var recoveredStore = CreateStore("sid-a", "exe-b");
        var resumed = await Dispatcher(recoveredStore, operation).ResumeAsync(created.TransactionId, CancellationToken.None);
        resumed.Status.Should().Be(TransactionStatus.Completed);
        operation.Current.Should().Be("compiled");
    }

    [Fact]
    public async Task CrashAfterTerminalPublicationLeavesRollbackRecoverable()
    {
        var observer = new ThrowOnceObserver("result-authenticated");
        var operation = new Operation("power.known");
        var store = CreateStore("sid-a", "exe-a", observer: observer);
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var plan = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);

        await FluentActions.Invoking(() => Dispatcher(store, operation).DispatchAsync(plan, CancellationToken.None))
            .Should().ThrowAsync<InjectedTransitionException>();
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.result.json")).Should().BeTrue();
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.journal.json")).Should().BeTrue();

        var recoveredStore = CreateStore("sid-a", "exe-b");
        var rolledBack = await Dispatcher(recoveredStore, operation).RollbackAsync(created.TransactionId, CancellationToken.None);
        rolledBack.Status.Should().Be(TransactionStatus.RolledBack);
    }

    [Fact]
    public async Task CompletedPublicationCrashIsFinalizedOnResumeAfterRestart()
    {
        var operation = new Operation("power.known");
        var store = CreateStore("sid-a", "exe-a", observer: new ThrowOnceObserver("result-authenticated"));
        var created = await store.CreateAsync([new("power.known", "default")], CancellationToken.None);
        var plan = await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None);
        await FluentActions.Invoking(() => Dispatcher(store, operation).DispatchAsync(plan, CancellationToken.None))
            .Should().ThrowAsync<InjectedTransitionException>();
        var recovered = CreateStore("sid-a", "exe-b");
        (await Dispatcher(recovered, operation).ResumeAsync(created.TransactionId, CancellationToken.None)).Status
            .Should().Be(TransactionStatus.Completed);
        operation.ApplyCount.Should().Be(1);
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.journal.json")).Should().BeFalse();
        (await recovered.LoadRecentAsync(10, CancellationToken.None)).Single().Status.Should().Be(TransactionStatus.Completed);
    }

    [Fact]
    public async Task RolledBackPublicationCrashIsFinalizedWithoutDuplicateRestore()
    {
        var operation = new Operation("power.known");
        var initial = CreateStore("sid-a", "exe-a");
        var created = await initial.CreateAsync([new("power.known", "default")], CancellationToken.None);
        await Dispatcher(initial, operation).DispatchAsync(await initial.LoadAndValidateAsync(created.TransactionId, CancellationToken.None), CancellationToken.None);
        var crashing = CreateStore("sid-a", "exe-a", observer: new ThrowOnceObserver("result-authenticated"));
        await FluentActions.Invoking(() => Dispatcher(crashing, operation).RollbackAsync(created.TransactionId, CancellationToken.None))
            .Should().ThrowAsync<InjectedTransitionException>();
        var recovered = CreateStore("sid-a", "exe-b");
        (await Dispatcher(recovered, operation).RollbackAsync(created.TransactionId, CancellationToken.None)).Status
            .Should().Be(TransactionStatus.RolledBack);
        operation.RestoreCount.Should().Be(1);
        File.Exists(Path.Combine(root, $"{created.TransactionId:N}.journal.json")).Should().BeFalse();
        (await recovered.LoadRecentAsync(10, CancellationToken.None)).Single().Status.Should().Be(TransactionStatus.RolledBack);
    }

    [Fact]
    public async Task ConcurrentRollbackAndResumeSerializeOneAttempt()
    {
        var operation = new Operation("power.known") { BlockRestore = true };
        var initial = CreateStore("sid-a", "exe-a");
        var created = await initial.CreateAsync([new("power.known", "default")], CancellationToken.None);
        await Dispatcher(initial, operation).DispatchAsync(await initial.LoadAndValidateAsync(created.TransactionId, CancellationToken.None), CancellationToken.None);
        var rollback = Dispatcher(CreateStore("sid-a", "exe-a"), operation).RollbackAsync(created.TransactionId, CancellationToken.None);
        await operation.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var resume = Dispatcher(CreateStore("sid-a", "exe-b"), operation).ResumeAsync(created.TransactionId, CancellationToken.None);
        await Task.Delay(100);
        resume.IsCompleted.Should().BeFalse();
        operation.ReleaseRestore.TrySetResult();
        (await rollback).Status.Should().Be(TransactionStatus.RolledBack);
        await FluentActions.Awaiting(() => resume).Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot be resumed*");
        operation.ApplyCount.Should().Be(1);
        operation.RestoreCount.Should().Be(1);
    }

    private async Task<Guid> CompleteAsync(ProtectedPlanStore store, Operation operation)
    {
        var created = await store.CreateAsync([new(operation.Descriptor.Id, "default")], CancellationToken.None);
        await Dispatcher(store, operation).DispatchAsync(
            await store.LoadAndValidateAsync(created.TransactionId, CancellationToken.None), CancellationToken.None);
        return created.TransactionId;
    }

    private ProtectedPlanStore CreateStore(string sid, string executable, TimeProvider? clock = null,
        IProtectedPlanTransitionObserver? observer = null) => new(new ProtectedPlanStoreOptions(root, sid, executable,
        clock ?? TimeProvider.System, new IdentityProtector(), new NoOpAccessControl())
        { TransitionObserver = observer ?? NullProtectedPlanTransitionObserver.Instance });

    private static PrivilegedOperationDispatcher Dispatcher(ProtectedPlanStore store, params Operation[] operations) =>
        new(store, Snapshot(), PrivilegedOperationDispatcher.CreateCatalog(operations));

    private static SystemSnapshot Snapshot() => new(new("Windows", "10", 26100), new("CPU", "AMD"), [],
        new(16_000_000_000), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class AdjustableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset current = value;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current += value;
    }

    private sealed class ThrowOnceObserver(string target) : IProtectedPlanTransitionObserver
    {
        private bool thrown;
        public void Reached(string transition, Guid transactionId)
        {
            if (!thrown && transition == target) { thrown = true; throw new InjectedTransitionException(); }
        }
    }
    private sealed class InjectedTransitionException : Exception;
    private sealed class IdentityProtector : IProtectedPlanKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
    }
    private sealed class NoOpAccessControl : IProtectedPlanAccessControl
    {
        public void ProtectDirectory(string path, string initiatingIdentity) { }
        public void ProtectFile(string path, string initiatingIdentity, bool initiatingUserCanWrite) { }
        public void ValidateDirectory(string path, string initiatingIdentity) { }
    }
    private sealed class Operation(string id) : ITweakOperation, IRequestedValueProvider
    {
        public string Current { get; private set; } = "original";
        public int ApplyCount { get; private set; }
        public int RestoreCount { get; private set; }
        public bool BlockRestore { get; init; }
        public TaskCompletionSource RestoreEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRestore { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FailRestoreCount { get; set; }
        public int FailRestoreVerificationCount { get; set; }
        public string RequestedValue => "compiled";
        public TweakDescriptor Descriptor { get; } = new(id, id, TweakCategory.Power, ImpactLevel.Medium,
            RiskLevel.Advanced, true, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
        {
            ApplyCount++;
            Current = requestedValue;
            return Task.CompletedTask;
        }
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
            Task.FromResult(Current == requestedValue);
        public async Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
        {
            RestoreCount++;
            RestoreEntered.TrySetResult();
            if (BlockRestore) await ReleaseRestore.Task.WaitAsync(cancellationToken);
            if (FailRestoreCount-- > 0) throw new InvalidOperationException("injected restore failure");
            Current = FailRestoreVerificationCount-- > 0 ? "wrong-restored-value" : originalValue!;
        }
    }
}
