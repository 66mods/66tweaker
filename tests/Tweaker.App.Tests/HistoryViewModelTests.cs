using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.App.Tests;

public sealed class HistoryViewModelTests
{
    [Fact]
    public async Task LoadAsync_MapsTransactionsToReadableRows()
    {
        var transaction = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.Parse("2026-08-13T10:00:00Z"),
            TransactionStatus.PartiallyRolledBack,
            [new("safe", "1", "0", TweakStatus.Failed, false, "Verification failed", DateTimeOffset.UtcNow)]);
        var vm = new HistoryViewModel(new Store([transaction]));

        await vm.LoadAsync(CancellationToken.None);

        vm.Items.Should().ContainSingle();
        vm.Items[0].Status.Should().Be("Partially rolled back");
        vm.Items[0].Summary.Should().Contain("1 operation").And.Contain("1 failed");
    }

    private sealed class Store(IReadOnlyList<TransactionRecord> records) : ITransactionHistoryStore
    {
        public Task<IReadOnlyList<TransactionRecord>> LoadRecentAsync(int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<TransactionRecord>>(records.Take(limit).ToArray());
    }
}
