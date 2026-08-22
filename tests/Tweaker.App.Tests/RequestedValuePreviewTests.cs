
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class RequestedValuePreviewTests
{
    [Fact]
    public async Task LoadAsync_UsesOperationRequestedValueInsteadOfAssumingZero()
    {
        var operation = new RequestedOperation();
        var vm = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(new Store()), Snapshot());
        await vm.LoadAsync(CancellationToken.None);
        vm.Items.Single().NewValue.Should().Be("Enabled");
        vm.SelectedProfile = "Gaming";
        await vm.ApplySelectedAsync(CancellationToken.None);
        operation.Current.Should().Be("1");
    }

    private static SystemSnapshot Snapshot() => new(new("Windows", "10", 26100), new("CPU", "AMD"), [], new(1), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);
    private sealed class RequestedOperation : ITweakOperation, IRequestedValueProvider
    {
        public string Current { get; private set; } = "0";
        public string RequestedValue => "1";
        public TweakDescriptor Descriptor { get; } = new("game-mode", "Enable Game Mode", TweakCategory.Windows, ImpactLevel.Medium, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string value, CancellationToken token) { Current = value; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(Current == value);
        public Task RestoreAsync(string? value, CancellationToken token) { Current = value!; return Task.CompletedTask; }
    }
    private sealed class Store : ITransactionStore
    {
        private TransactionRecord? value;
        public Task BeginAsync(TransactionRecord record, CancellationToken token) { value = record; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord record, CancellationToken token) { value = record; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(value);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
    }
}
