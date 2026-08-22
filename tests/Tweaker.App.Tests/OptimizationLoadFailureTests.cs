
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class OptimizationLoadFailureTests
{
    [Fact]
    public async Task LoadAsync_OnePreviewReadFails_KeepsTheRestOfTheControlCenterUsable()
    {
        var vm = OptimizationViewModel.CreateForTests([new FailingOperation()], new TransactionCoordinator(new Store()), Snapshot());

        await vm.LoadAsync(CancellationToken.None);

        vm.Items.Should().ContainSingle();
        vm.Items[0].IsAvailable.Should().BeFalse();
        vm.Items[0].IsSelected.Should().BeFalse();
        vm.Items[0].CurrentValue.Should().Contain("Unavailable");
    }

    private static SystemSnapshot Snapshot() => new(new("Windows", "10", 26100), new("CPU", "AMD"), [],
        new(1), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    private sealed class FailingOperation : ITweakOperation
    {
        public TweakDescriptor Descriptor { get; } = new("fail", "Fail", TweakCategory.Power, ImpactLevel.Low, RiskLevel.Safe, true, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => throw new IOException("tool unavailable");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(false);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Store : ITransactionStore
    {
        public Task BeginAsync(TransactionRecord value, CancellationToken token) => Task.CompletedTask;
        public Task SaveAsync(TransactionRecord value, CancellationToken token) => Task.CompletedTask;
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
    }
}
