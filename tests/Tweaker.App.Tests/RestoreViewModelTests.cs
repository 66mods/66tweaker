using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class RestoreViewModelTests
{
    [Fact]
    public async Task RestoreLatestAsync_RollsBackNewestCompletedTransactionAfterRestart()
    {
        var operation = new Operation { Current = "changed" };
        var older = Record(DateTimeOffset.UtcNow.AddHours(-1), TransactionStatus.Completed, operation);
        var latest = Record(DateTimeOffset.UtcNow, TransactionStatus.Completed, operation);
        var store = new Store([latest, older]);
        var vm = new RestoreViewModel(store, new TransactionCoordinator(store),
            new Dictionary<string, ITweakOperation> { [operation.Descriptor.Id] = operation });

        await vm.LoadAsync(CancellationToken.None);
        await vm.RestoreLatestAsync(CancellationToken.None);

        operation.Current.Should().Be("original");
        vm.Status.Should().Contain("Restored 1");
        store.LoadedId.Should().Be(latest.Id);
    }

    private static TransactionRecord Record(DateTimeOffset at, TransactionStatus status, ITweakOperation operation) =>
        new(Guid.NewGuid(), at, status,
            [new(operation.Descriptor.Id, "original", "changed", TweakStatus.Applied, true, "Applied", at)]);

    private sealed class Operation : ITweakOperation
    {
        public string Current { get; set; } = "original";
        public TweakDescriptor Descriptor { get; } = new("restore", "Restore", TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string value, CancellationToken token) { Current = value; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(Current == value);
        public Task RestoreAsync(string? value, CancellationToken token) { Current = value!; return Task.CompletedTask; }
    }

    private sealed class Store(IReadOnlyList<TransactionRecord> records) : ITransactionStore, ITransactionHistoryStore
    {
        private readonly Dictionary<Guid, TransactionRecord> values = records.ToDictionary(x => x.Id);
        public Guid? LoadedId { get; private set; }
        public Task BeginAsync(TransactionRecord value, CancellationToken token) { values[value.Id] = value; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord value, CancellationToken token) { values[value.Id] = value; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) { LoadedId = id; return Task.FromResult(values.GetValueOrDefault(id)); }
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
        public Task<IReadOnlyList<TransactionRecord>> LoadRecentAsync(int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<TransactionRecord>>(records.Take(limit).ToArray());
    }
}
