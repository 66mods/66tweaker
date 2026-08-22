using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

/// <summary>
/// The Games tab had this defect and the owner hit it: apply one profile, apply another, press Undo, and
/// the PC lands on the first profile instead of its original state. The Optimization tab shared the flaw.
/// </summary>
public sealed class OptimizationUndoRewindTests
{
    [Fact]
    public async Task Undo_AfterTwoProfiles_RestoresTheOriginalValueNotTheIntermediateOne()
    {
        var operation = new RecordingOperation("original");
        var view = Build(operation);

        view.SelectedProfile = "Custom";
        SelectOnly(view, operation);
        await view.ApplyCommand.ExecuteAsync();
        operation.Current.Should().Be("applied");

        SelectOnly(view, operation);
        await view.ApplyCommand.ExecuteAsync();

        await view.UndoCommand.ExecuteAsync();

        operation.Current.Should().Be("original", "Undo must rewind every apply, not only the newest");
    }

    [Fact]
    public async Task Undo_ReportsHowManyProfilesItRewound()
    {
        var operation = new RecordingOperation("original");
        var view = Build(operation);
        view.SelectedProfile = "Custom";
        SelectOnly(view, operation);
        await view.ApplyCommand.ExecuteAsync();
        SelectOnly(view, operation);
        await view.ApplyCommand.ExecuteAsync();

        await view.UndoCommand.ExecuteAsync();

        view.LastResult.Should().Contain("2 applied profile(s)");
    }

    [Fact]
    public async Task Undo_RunTwiceReportsThereIsNothingLeft()
    {
        var operation = new RecordingOperation("original");
        var view = Build(operation);
        view.SelectedProfile = "Custom";
        SelectOnly(view, operation);
        await view.ApplyCommand.ExecuteAsync();
        await view.UndoCommand.ExecuteAsync();

        await view.UndoCommand.ExecuteAsync();

        view.LastResult.Should().Contain("no session to restore");
        operation.Current.Should().Be("original");
    }

    private static void SelectOnly(OptimizationViewModel view, ITweakOperation operation)
    {
        foreach (var item in view.Items) item.IsSelected = item.Operation == operation;
    }

    private static OptimizationViewModel Build(ITweakOperation operation)
    {
        var store = new MemoryStore();
        var snapshot = new SystemSnapshot(new("Windows 10 Pro 22H2", "10.0.19045", 19045),
            new("CPU", "AMD"), [], new(16_000_000_000), new(false, true, "Balanced"),
            new Dictionary<string, DetectedGame>(), []);
        var view = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(store), snapshot);
        view.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        return view;
    }

    /// <summary>A local operation whose value can be inspected, so the test can see where Undo landed.</summary>
    private sealed class RecordingOperation : ITweakOperation, IRequestedValueProvider
    {
        private readonly string initial;
        public RecordingOperation(string initial) => Current = this.initial = initial;
        public string Current { get; private set; }
        public TweakDescriptor Descriptor { get; } = new("test.op", "Test operation", TweakCategory.Windows,
            ImpactLevel.Low, RiskLevel.Safe, RequiresElevation: false, RequiresRestart: false);
        public string RequestedValue => "applied";
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Current);
        public Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
        {
            Current = requestedValue;
            return Task.CompletedTask;
        }
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken) =>
            Task.FromResult(Current == requestedValue);
        public Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
        {
            Current = originalValue ?? initial;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStore : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord record, CancellationToken cancellationToken) =>
            SaveAsync(record, cancellationToken);
        public Task SaveAsync(TransactionRecord record, CancellationToken cancellationToken)
        {
            records[record.Id] = record;
            return Task.CompletedTask;
        }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(records.Values.FirstOrDefault(x => x.Status == TransactionStatus.InProgress));
    }
}
