
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class OptimizationViewModelTests
{
    [Fact]
    public async Task LoadAsync_ExposesExactPreviewMetadata()
    {
        var operation = new FakeOperation("1");
        var vm = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(new MemoryStore()), Snapshot());

        await vm.LoadAsync(CancellationToken.None);

        vm.Items.Should().ContainSingle();
        vm.Items[0].CurrentValue.Should().Be("Enabled");
        vm.Items[0].NewValue.Should().Be("Disabled");
        vm.Items[0].Risk.Should().Be("Safe");
    }

    [Fact]
    public async Task ApplyAndUndo_RestoresExactOriginal()
    {
        var operation = new FakeOperation("1");
        var vm = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(new MemoryStore()), Snapshot());
        await vm.LoadAsync(CancellationToken.None);

        await vm.ApplySelectedAsync(CancellationToken.None);
        operation.Current.Should().Be("0");
        vm.LastResult.Should().Contain("Applied");
        await vm.UndoLastAsync(CancellationToken.None);

        operation.Current.Should().Be("1");
        vm.LastResult.Should().Contain("Restored");
    }

    private static SystemSnapshot Snapshot() => new(new("Windows", "10", 26100), new("CPU", "AMD"), [],
        new(16_000_000_000), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    private sealed class FakeOperation(string value) : ITweakOperation
    {
        public string Current { get; private set; } = value;
        public TweakDescriptor Descriptor { get; } = new("test", "Disable consumer suggestions", TweakCategory.Privacy,
            ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string requested, CancellationToken token) { Current = requested; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string requested, CancellationToken token) => Task.FromResult(Current == requested);
        public Task RestoreAsync(string? original, CancellationToken token) { Current = original!; return Task.CompletedTask; }
    }

    private sealed class MemoryStore : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord record, CancellationToken token) => SaveAsync(record, token);
        public Task SaveAsync(TransactionRecord record, CancellationToken token) { records[record.Id] = record; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
    }
}
