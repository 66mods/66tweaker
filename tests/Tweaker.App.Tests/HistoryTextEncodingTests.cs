using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.App.Tests;

public sealed class HistoryTextEncodingTests
{
    [Fact]
    public async Task LoadAsync_UsesAnUncorruptedMiddleDotSeparator()
    {
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.Completed,
            [new("safe", "1", "0", TweakStatus.Applied, false, "Applied", DateTimeOffset.UtcNow)]);
        var viewModel = new HistoryViewModel(new Store([record]));

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.Items.Should().ContainSingle();
        viewModel.Items[0].Summary.Should().Be("1 operation · verified");
    }

    private sealed class Store(IReadOnlyList<TransactionRecord> records) : ITransactionHistoryStore
    {
        public Task<IReadOnlyList<TransactionRecord>> LoadRecentAsync(int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<TransactionRecord>>(records.Take(limit).ToArray());
    }
}
