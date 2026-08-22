using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class RecoveryViewModelTests
{
    [Fact]
    public async Task CheckAsync_IncompleteTransaction_ExposesDetailsWithoutMutation()
    {
        var store = new Store(new TransactionRecord(Guid.NewGuid(), DateTimeOffset.Parse("2026-08-13T10:00:00Z"), TransactionStatus.InProgress,
            [new("safe", "1", "0", TweakStatus.Applied, true, "Applied", DateTimeOffset.UtcNow)]));
        var vm = new RecoveryViewModel(store, new TransactionCoordinator(store), new Dictionary<string, ITweakOperation>());
        await vm.CheckAsync(CancellationToken.None);
        vm.HasIncompleteTransaction.Should().BeTrue();
        vm.Message.Should().Contain("1 recorded operation");
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task RollbackAsync_UsesRecordedOperationAndClearsPrompt()
    {
        var operation = new Operation();
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.InProgress,
            [new(operation.Descriptor.Id, "1", "0", TweakStatus.Applied, true, "Applied", DateTimeOffset.UtcNow)]);
        var store = new Store(record);
        var vm = new RecoveryViewModel(store, new TransactionCoordinator(store), new Dictionary<string, ITweakOperation> { [operation.Descriptor.Id] = operation });
        await vm.CheckAsync(CancellationToken.None);
        await vm.RollbackAsync(CancellationToken.None);
        operation.Current.Should().Be("1");
        vm.HasIncompleteTransaction.Should().BeFalse();
    }

    private sealed class Operation : ITweakOperation
    {
        public string Current { get; private set; } = "0";
        public TweakDescriptor Descriptor { get; } = new("safe", "Safe", TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string value, CancellationToken token) { Current = value; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) { Current = value!; return Task.CompletedTask; }
    }
    private sealed class Store(TransactionRecord? record) : ITransactionStore
    {
        private TransactionRecord? value = record;
        public int SaveCount { get; private set; }
        public Task BeginAsync(TransactionRecord next, CancellationToken token) { value = next; SaveCount++; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord next, CancellationToken token) { value = next; SaveCount++; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(value);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult(value?.Status == TransactionStatus.InProgress ? value : null);
    }
}
