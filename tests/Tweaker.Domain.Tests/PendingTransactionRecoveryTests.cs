using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.Domain.Tests;

public sealed class PendingTransactionRecoveryTests
{
    [Fact]
    public async Task RollbackAsync_PendingJournalEntry_RestoresBecauseMutationOutcomeIsUnknown()
    {
        var operation = new Operation { Current = "changed" };
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.InProgress,
            [new(operation.Descriptor.Id, "original", "changed", TweakStatus.Pending, false, "Snapshot saved", DateTimeOffset.UtcNow)]);
        var store = new Store(record);

        var restored = await new TransactionCoordinator(store).RollbackAsync(record.Id,
            new Dictionary<string, ITweakOperation> { [operation.Descriptor.Id] = operation }, CancellationToken.None);

        operation.Current.Should().Be("original");
        restored.Results[0].Status.Should().Be(TweakStatus.Restored);
    }

    private sealed class Operation : ITweakOperation
    {
        public string Current { get; set; } = "original";
        public TweakDescriptor Descriptor { get; } = new("pending", "Pending", TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string value, CancellationToken token) { Current = value; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(Current == value);
        public Task RestoreAsync(string? value, CancellationToken token) { Current = value!; return Task.CompletedTask; }
    }

    private sealed class Store(TransactionRecord record) : ITransactionStore
    {
        private TransactionRecord value = record;
        public Task BeginAsync(TransactionRecord next, CancellationToken token) { value = next; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord next, CancellationToken token) { value = next; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult<TransactionRecord?>(value);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(value);
    }
}
