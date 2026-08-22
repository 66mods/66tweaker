using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.ViewModels;

public sealed class RecoveryViewModel : ObservableObject
{
    private readonly ITransactionStore store;
    private readonly TransactionCoordinator coordinator;
    private readonly IReadOnlyDictionary<string, ITweakOperation> operations;
    private TransactionRecord? incomplete;
    private bool hasIncompleteTransaction;
    private string message = "No interrupted session detected.";

    public RecoveryViewModel(ITransactionStore store, TransactionCoordinator coordinator, IReadOnlyDictionary<string, ITweakOperation> operations)
    {
        this.store = store;
        this.coordinator = coordinator;
        this.operations = operations;
        RollbackCommand = new AsyncCommand(RollbackAsync, error => Message = $"Recovery failed: {error.Message}");
    }
    public bool HasIncompleteTransaction { get => hasIncompleteTransaction; private set => Set(ref hasIncompleteTransaction, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public AsyncCommand RollbackCommand { get; }
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        incomplete = await store.LoadLatestIncompleteAsync(cancellationToken);
        HasIncompleteTransaction = incomplete is not null;
        Message = incomplete is null ? "No interrupted session detected." :
            $"Interrupted session from {incomplete.StartedAt.LocalDateTime:g}: {incomplete.Results.Count} recorded operation{(incomplete.Results.Count == 1 ? "" : "s")}. No changes were resumed automatically.";
    }
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (incomplete is null) { Message = "No interrupted session detected."; return; }
        var restored = await coordinator.RollbackAsync(incomplete.Id, operations, cancellationToken);
        HasIncompleteTransaction = false;
        incomplete = null;
        Message = restored.Status == TransactionStatus.RolledBack ? "Interrupted session restored." : "Restore completed with failures; review History.";
    }
}
