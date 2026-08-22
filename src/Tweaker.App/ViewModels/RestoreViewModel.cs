using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.ViewModels;

public sealed class RestoreViewModel : ObservableObject
{
    private readonly ITransactionHistoryStore history;
    private readonly TransactionCoordinator coordinator;
    private readonly IReadOnlyDictionary<string, ITweakOperation> operations;
    private TransactionRecord? latest;
    private string status = "Checking local transaction history…";

    public RestoreViewModel(ITransactionHistoryStore history, TransactionCoordinator coordinator,
        IReadOnlyDictionary<string, ITweakOperation> operations)
    {
        this.history = history;
        this.coordinator = coordinator;
        this.operations = operations;
        RestoreLatestCommand = new AsyncCommand(RestoreLatestAsync, error => Status = $"Restore failed: {error.Message}");
    }

    public AsyncCommand RestoreLatestCommand { get; }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool HasRestorableSession => latest is not null;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        latest = (await history.LoadRecentAsync(50, cancellationToken))
            .FirstOrDefault(x => (x.Status == TransactionStatus.Completed || x.Status == TransactionStatus.PartiallyRolledBack) &&
                x.Results.Any(result => result.Status is TweakStatus.Applied or TweakStatus.Pending));
        Status = latest is null
            ? "No completed transaction is available to restore."
            : $"Latest restorable session: {latest.StartedAt.LocalDateTime:g} · {latest.Results.Count} recorded operations.";
        RaisePropertyChanged(nameof(HasRestorableSession));
    }

    public async Task RestoreLatestAsync(CancellationToken cancellationToken)
    {
        if (latest is null) { Status = "No completed transaction is available to restore."; return; }
        var transaction = await coordinator.RollbackAsync(latest.Id, operations, cancellationToken);
        var restored = transaction.Results.Count(x => x.Status == TweakStatus.Restored);
        Status = transaction.Status == TransactionStatus.RolledBack
            ? $"Restored {restored} recorded operations."
            : $"Restored {restored} operations with failures; review History.";
        latest = null;
        RaisePropertyChanged(nameof(HasRestorableSession));
    }
}
