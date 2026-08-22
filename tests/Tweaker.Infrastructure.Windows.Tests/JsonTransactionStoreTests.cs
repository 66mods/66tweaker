using FluentAssertions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Storage;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class JsonTransactionStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsAcrossStoreInstances()
    {
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.Parse("2026-08-13T10:00:00Z"),
            TransactionStatus.InProgress,
            [new("safe.test", null, "0", TweakStatus.Pending, false, "Snapshot saved", DateTimeOffset.UtcNow)]);
        await new JsonTransactionStore(root).BeginAsync(record, CancellationToken.None);

        var loaded = await new JsonTransactionStore(root).LoadAsync(record.Id, CancellationToken.None);

        loaded.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task LatestIncomplete_IgnoresCompletedTransactions()
    {
        var store = new JsonTransactionStore(root);
        var completed = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.Parse("2026-08-13T11:00:00Z"), TransactionStatus.Completed, []);
        var incomplete = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.Parse("2026-08-13T10:00:00Z"), TransactionStatus.InProgress, []);
        await store.BeginAsync(completed, CancellationToken.None);
        await store.BeginAsync(incomplete, CancellationToken.None);

        (await store.LoadLatestIncompleteAsync(CancellationToken.None))!.Id.Should().Be(incomplete.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
