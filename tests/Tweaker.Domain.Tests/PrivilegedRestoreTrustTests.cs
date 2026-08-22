using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.Domain.Tests;

public sealed class PrivilegedRestoreTrustTests
{
    [Fact]
    public async Task UserWritableCompatibilityJournal_CannotDrivePrivilegedRestore()
    {
        var operation = new PrivilegedOperation();
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.Completed,
            [new(operation.Descriptor.Id, "original", "requested", TweakStatus.Applied, true, "Applied", DateTimeOffset.UtcNow)]);
        var store = new CompatibilityStore(record);

        var result = await new TransactionCoordinator(store).RollbackAsync(record.Id,
            new Dictionary<string, ITweakOperation> { [operation.Descriptor.Id] = operation }, CancellationToken.None);

        operation.RestoreCount.Should().Be(0);
        result.Status.Should().Be(TransactionStatus.PartiallyRolledBack);
        result.Results.Single().Message.Should().Contain("not protected");
    }

    private sealed class PrivilegedOperation : ITweakOperation
    {
        public int RestoreCount { get; private set; }
        public TweakDescriptor Descriptor { get; } = new("power.privileged", "Power", TweakCategory.Power,
            ImpactLevel.Medium, RiskLevel.Advanced, true, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("original");
        public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken) { RestoreCount++; return Task.CompletedTask; }
    }

    private sealed class CompatibilityStore(TransactionRecord record) : ITransactionStore
    {
        private TransactionRecord value = record;
        public Task BeginAsync(TransactionRecord item, CancellationToken cancellationToken) => SaveAsync(item, cancellationToken);
        public Task SaveAsync(TransactionRecord item, CancellationToken cancellationToken) { value = item; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<TransactionRecord?>(value);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken) => Task.FromResult<TransactionRecord?>(null);
    }
}
