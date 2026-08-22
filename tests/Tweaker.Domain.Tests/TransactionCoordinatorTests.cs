using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.Domain.Tests;

public sealed class TransactionCoordinatorTests
{
    [Fact]
    public async Task ApplyAsync_VerificationFailure_RestoresOriginalAndContinues()
    {
        var store = new MemoryTransactionStore();
        var bad = new FakeOperation("bad", "old", verify: false);
        var good = new FakeOperation("good", "before", verify: true);
        var coordinator = new TransactionCoordinator(store);

        var record = await coordinator.ApplyAsync(
            [new(bad, "new"), new(good, "after")], EmptySnapshot(), CancellationToken.None);

        record.Results.Should().SatisfyRespectively(
            x => x.Status.Should().Be(TweakStatus.Restored),
            x => x.Status.Should().Be(TweakStatus.Applied));
        bad.RestoredValue.Should().Be("old");
        good.Current.Should().Be("after");
    }

    [Fact]
    public async Task RollbackAsync_RestoresAppliedOperationsInReverseOrder()
    {
        var order = new List<string>();
        var first = new FakeOperation("first", "one", true, order);
        var second = new FakeOperation("second", "two", true, order);
        var store = new MemoryTransactionStore();
        var coordinator = new TransactionCoordinator(store);
        var applied = await coordinator.ApplyAsync(
            [new(first, "1"), new(second, "2")], EmptySnapshot(), CancellationToken.None);

        order.Clear();
        await coordinator.RollbackAsync(applied.Id,
            new Dictionary<string, ITweakOperation> { ["first"] = first, ["second"] = second },
            CancellationToken.None);

        order.Should().Equal("second", "first");
    }

    [Fact]
    public async Task ApplyAsync_WhenCompensationFails_PreservesPendingRetryableJournal()
    {
        var store = new MemoryTransactionStore();
        var operation = new BrokenCompensationOperation();

        var record = await new TransactionCoordinator(store).ApplyAsync(
            [new(operation, "new")], EmptySnapshot(), CancellationToken.None);

        record.Status.Should().Be(TransactionStatus.PartiallyRolledBack);
        record.Results.Should().ContainSingle(x => x.Status == TweakStatus.Pending && x.Message.Contains("rollback failed"));
        (await store.LoadLatestIncompleteAsync(CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyPreparedAsync_UsesDurableEmptyJournalWithoutBeginningAgain()
    {
        var store = new MemoryTransactionStore();
        var coordinator = new TransactionCoordinator(store);
        var id = Guid.NewGuid();
        var operation = new FakeOperation("prepared", "before", true);
        await coordinator.PrepareAsync(id, CancellationToken.None);
        var result = await coordinator.ApplyPreparedAsync(id, [new(operation, "after")], EmptySnapshot(), CancellationToken.None);
        result.Status.Should().Be(TransactionStatus.Completed);
        operation.Current.Should().Be("after");
        store.BeginCount.Should().Be(1);
    }

    [Fact]
    public async Task ApplyAsync_MissingTaskReadRefusal_CreatesNoPendingRecovery()
    {
        var store = new MemoryTransactionStore();
        var operation = new MissingTaskReadOperation();

        var record = await new TransactionCoordinator(store).ApplyAsync(
            [new(operation, "disabled")], EmptySnapshot(), CancellationToken.None);

        record.Status.Should().Be(TransactionStatus.Completed);
        record.Results.Should().ContainSingle(result => result.Status == TweakStatus.Failed && !result.Verified);
        operation.ApplyCalled.Should().BeFalse();
        operation.RestoreCalled.Should().BeFalse();
        (await store.LoadLatestIncompleteAsync(CancellationToken.None)).Should().BeNull();
    }
    private sealed class MissingTaskReadOperation : ITweakOperation
    {
        public bool ApplyCalled { get; private set; }
        public bool RestoreCalled { get; private set; }
        public TweakDescriptor Descriptor { get; } = new("missing-task", "missing task", TweakCategory.Windows,
            ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => throw new InvalidOperationException("Scheduled task does not exist.");
        public Task ApplyAsync(string value, CancellationToken token) { ApplyCalled = true; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(false);
        public Task RestoreAsync(string? value, CancellationToken token) { RestoreCalled = true; return Task.CompletedTask; }
    }
    private sealed class BrokenCompensationOperation : ITweakOperation
    {
        public TweakDescriptor Descriptor { get; } = new("broken-compensation", "broken", TweakCategory.Windows,
            ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("old");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(false);
        public Task RestoreAsync(string? value, CancellationToken token) => throw new InvalidOperationException("restore broke");
    }
    private static SystemSnapshot EmptySnapshot() => new(
        new("Windows", "10", 1), new("CPU", "Vendor"), [], new(0),
        new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    private sealed class FakeOperation(
        string id, string? current, bool verify, List<string>? restoreOrder = null) : ITweakOperation
    {
        public string? Current { get; private set; } = current;
        public string? RestoredValue { get; private set; }
        public TweakDescriptor Descriptor { get; } = new(id, id, TweakCategory.Windows,
            ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult(Current);
        public Task ApplyAsync(string value, CancellationToken token) { Current = value; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(verify);
        public Task RestoreAsync(string? value, CancellationToken token)
        {
            Current = value; RestoredValue = value; restoreOrder?.Add(id); return Task.CompletedTask;
        }
    }

    private sealed class MemoryTransactionStore : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public int BeginCount { get; private set; }
        public Task BeginAsync(TransactionRecord record, CancellationToken token)
        {
            BeginCount++;
            records[record.Id] = record;
            return Task.CompletedTask;
        }
        public Task SaveAsync(TransactionRecord record, CancellationToken token) { records[record.Id] = record; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) =>
            Task.FromResult(records.Values.LastOrDefault(x => x.Status is TransactionStatus.InProgress or TransactionStatus.PartiallyRolledBack));
    }
}
