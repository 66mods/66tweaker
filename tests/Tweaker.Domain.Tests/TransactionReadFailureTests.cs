using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.Domain.Tests;

public sealed class TransactionReadFailureTests
{
    [Fact]
    public async Task ApplyAsync_CurrentValueReadFails_RecordsFailureWithoutMutatingOrCrashing()
    {
        var operation = new ReadFailureOperation();
        var store = new Store();
        var coordinator = new TransactionCoordinator(store);

        var transaction = await coordinator.ApplyAsync([new(operation, "1")], Snapshot(), CancellationToken.None);

        transaction.Results.Should().ContainSingle();
        transaction.Results[0].Status.Should().Be(TweakStatus.Failed);
        transaction.Results[0].Message.Should().Contain("cannot read");
        operation.ApplyCount.Should().Be(0);
    }

    private static SystemSnapshot Snapshot() => new(new("Windows", "10", 26100), new("CPU", "AMD"), [],
        new(1), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    private sealed class ReadFailureOperation : ITweakOperation
    {
        public int ApplyCount { get; private set; }
        public TweakDescriptor Descriptor { get; } = new("read-failure", "Read failure", TweakCategory.Windows,
            ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => throw new IOException("cannot read current value");
        public Task ApplyAsync(string value, CancellationToken token) { ApplyCount++; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Store : ITransactionStore
    {
        private TransactionRecord? record;
        public Task BeginAsync(TransactionRecord value, CancellationToken token) { record = value; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord value, CancellationToken token) { record = value; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(record);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
    }
}
