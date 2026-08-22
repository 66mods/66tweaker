using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.Domain.Tests;

public sealed class RollbackVerificationTests
{
    [Fact]
    public async Task RollbackAsync_RestoreDoesNotReachSnapshot_MarksPartialFailure()
    {
        var operation = new BrokenRestoreOperation();
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.Completed,
            [new(operation.Descriptor.Id, "original", "changed", TweakStatus.Applied, true, "Applied", DateTimeOffset.UtcNow)]);
        var store = new Store(record);

        var result = await new TransactionCoordinator(store).RollbackAsync(record.Id,
            new Dictionary<string, ITweakOperation> { [operation.Descriptor.Id] = operation }, CancellationToken.None);

        result.Status.Should().Be(TransactionStatus.PartiallyRolledBack);
        result.Results[0].Status.Should().Be(TweakStatus.Pending);
        result.Results[0].Message.Should().Contain("retry");
    }

    private sealed class BrokenRestoreOperation : ITweakOperation
    {
        public TweakDescriptor Descriptor { get; } = new("broken", "Broken", TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("changed");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Store(TransactionRecord record) : ITransactionStore
    {
        private TransactionRecord value = record;
        public Task BeginAsync(TransactionRecord next, CancellationToken token) { value = next; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord next, CancellationToken token) { value = next; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult<TransactionRecord?>(value);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
    }
}

