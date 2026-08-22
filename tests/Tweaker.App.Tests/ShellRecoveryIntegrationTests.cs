using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class ShellRecoveryIntegrationTests
{
    [Fact]
    public async Task InitializeAsync_ChecksForInterruptedTransactions()
    {
        var operation = new Operation();
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.InProgress,
            [new(operation.Descriptor.Id, "1", "0", TweakStatus.Applied, true, "Applied", DateTimeOffset.UtcNow)]);
        var store = new Store(record);
        var shell = new ShellViewModel(new Scanner(), [operation], new TransactionCoordinator(store),
            reduceMotionDefault: true, transactionStore: store);

        await shell.InitializeAsync(CancellationToken.None);

        shell.Recovery.Should().NotBeNull();
        shell.Recovery!.HasIncompleteTransaction.Should().BeTrue();
    }

    private sealed class Scanner : ISystemScanner
    {
        public Task<SystemSnapshot> ScanAsync(CancellationToken token) => Task.FromResult(new SystemSnapshot(
            new("Windows 11", "10", 26100), new("CPU", "AMD"), [new("GPU", "NVIDIA", "1")], new(16_000_000_000),
            new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []));
    }

    private sealed class Operation : ITweakOperation
    {
        public TweakDescriptor Descriptor { get; } = new("safe", "Safe", TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("1");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Store(TransactionRecord value) : ITransactionStore
    {
        public Task BeginAsync(TransactionRecord record, CancellationToken token) => Task.CompletedTask;
        public Task SaveAsync(TransactionRecord record, CancellationToken token) => Task.CompletedTask;
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult<TransactionRecord?>(value);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(value);
    }
}
