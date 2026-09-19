using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.Domain.Tests;

/// <summary>
/// A snapshot written by an older release and the value read back after restoring it can differ as text
/// while describing the same state. An operation that says so is believed; one that does not is held to
/// the exact bytes, as before.
/// </summary>
public sealed class RestoreVerifyingOperationTests
{
    [Fact]
    public async Task RollbackAsync_TrustsTheOperationsOwnComparison()
    {
        var operation = new SchemaAwareOperation();
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.Completed,
            [new(operation.Descriptor.Id, "{\"v\":1,\"x\":7}", "changed", TweakStatus.Applied, true, "Applied", DateTimeOffset.UtcNow)]);
        var store = new Store(record);

        var result = await new TransactionCoordinator(store).RollbackAsync(record.Id,
            new Dictionary<string, ITweakOperation> { [operation.Descriptor.Id] = operation }, CancellationToken.None);

        result.Status.Should().Be(TransactionStatus.RolledBack);
        result.Results[0].Status.Should().Be(TweakStatus.Restored);
        result.Results[0].Verified.Should().BeTrue();
        operation.Compared.Should().Be(("{\"v\":1,\"x\":7}", "{\"v\":2,\"x\":7}"));
    }

    [Fact]
    public async Task RollbackAsync_StillFailsWhenTheOperationSaysTheStatesDiffer()
    {
        var operation = new SchemaAwareOperation { Matches = false };
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.Completed,
            [new(operation.Descriptor.Id, "{\"v\":1,\"x\":7}", "changed", TweakStatus.Applied, true, "Applied", DateTimeOffset.UtcNow)]);

        var result = await new TransactionCoordinator(new Store(record)).RollbackAsync(record.Id,
            new Dictionary<string, ITweakOperation> { [operation.Descriptor.Id] = operation }, CancellationToken.None);

        result.Status.Should().Be(TransactionStatus.PartiallyRolledBack);
        result.Results[0].Status.Should().Be(TweakStatus.Pending);
    }

    private sealed class SchemaAwareOperation : IRestoreVerifyingOperation
    {
        public bool Matches { get; init; } = true;
        public (string?, string?) Compared { get; private set; }
        public TweakDescriptor Descriptor { get; } = new("schema", "Schema", TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        // What a newer release reads back: the same state, written in its newer schema.
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("{\"v\":2,\"x\":7}");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
        public bool RestoredMatches(string? originalValue, string? restoredValue)
        {
            Compared = (originalValue, restoredValue);
            return Matches;
        }
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
