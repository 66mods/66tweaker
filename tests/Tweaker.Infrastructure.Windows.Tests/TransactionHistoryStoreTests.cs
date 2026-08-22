using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Storage;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class TransactionHistoryStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadRecentAsync_ReturnsNewestFirstAndHonorsLimit()
    {
        var store = new JsonTransactionStore(root);
        await store.SaveAsync(RecordAt(10), CancellationToken.None);
        await store.SaveAsync(RecordAt(12), CancellationToken.None);
        await store.SaveAsync(RecordAt(11), CancellationToken.None);

        var history = await ((ITransactionHistoryStore)store).LoadRecentAsync(2, CancellationToken.None);

        history.Select(x => x.StartedAt.Hour).Should().Equal(12, 11);
    }

    [Fact]
    public async Task LoadRecentAsync_SkipsCorruptFiles()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "broken.json"), "not-json");
        var store = new JsonTransactionStore(root);
        await store.SaveAsync(RecordAt(10), CancellationToken.None);

        var history = await ((ITransactionHistoryStore)store).LoadRecentAsync(10, CancellationToken.None);

        history.Should().ContainSingle();
    }

    private static TransactionRecord RecordAt(int hour) => new(Guid.NewGuid(),
        new DateTimeOffset(2026, 8, 13, hour, 0, 0, TimeSpan.Zero), TransactionStatus.Completed, []);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
