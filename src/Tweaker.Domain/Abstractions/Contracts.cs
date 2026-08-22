
using Tweaker.Domain.Models;

namespace Tweaker.Domain.Abstractions;

public interface ISystemScanner { Task<SystemSnapshot> ScanAsync(CancellationToken cancellationToken); }
public interface ITweakOperation
{
    TweakDescriptor Descriptor { get; }
    bool IsSupported(SystemSnapshot snapshot);
    Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken);
    Task ApplyAsync(string requestedValue, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken);
    Task RestoreAsync(string? originalValue, CancellationToken cancellationToken);
}
/// <summary>Marks an allowlisted operation that observes/verifies state but performs no mutation.</summary>
public interface IReadOnlyTweakOperation : ITweakOperation;

public interface ITransactionStore
{
    Task BeginAsync(TransactionRecord record, CancellationToken cancellationToken);
    Task SaveAsync(TransactionRecord record, CancellationToken cancellationToken);
    Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken);
    Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken);
}
/// <summary>Marker for an administrator/SYSTEM-owned transaction store authorized for elevated rollback.</summary>
public interface IPrivilegedTransactionStore : ITransactionStore;
public interface ITransactionHistoryStore
{
    Task<IReadOnlyList<TransactionRecord>> LoadRecentAsync(int limit, CancellationToken cancellationToken);
}
