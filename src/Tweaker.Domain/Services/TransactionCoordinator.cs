using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.Domain.Services;

public sealed class TransactionCoordinator(ITransactionStore store)
{
    public Task<TransactionRecord> ApplyAsync(
        IReadOnlyList<TweakRequest> requests,
        SystemSnapshot snapshot,
        CancellationToken cancellationToken) =>
        ApplyAsync(TransactionRecord.Start(), requests, snapshot, begin: true, cancellationToken);

    public Task<TransactionRecord> ApplyAsync(
        Guid transactionId,
        IReadOnlyList<TweakRequest> requests,
        SystemSnapshot snapshot,
        CancellationToken cancellationToken) =>
        ApplyAsync(TransactionRecord.Start(transactionId), requests, snapshot, begin: true, cancellationToken);

    public Task PrepareAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("A transaction ID is required.", nameof(transactionId));
        return store.BeginAsync(TransactionRecord.Start(transactionId), cancellationToken);
    }

    public Task<TransactionRecord> ApplyPreparedAsync(
        Guid transactionId,
        IReadOnlyList<TweakRequest> requests,
        SystemSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("A transaction ID is required.", nameof(transactionId));
        return ApplyAsync(TransactionRecord.Start(transactionId), requests, snapshot, begin: false, cancellationToken);
    }

    public Task<TransactionRecord?> LoadAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("A transaction ID is required.", nameof(transactionId));
        return store.LoadAsync(transactionId, cancellationToken);
    }

    private async Task<TransactionRecord> ApplyAsync(
        TransactionRecord transaction,
        IReadOnlyList<TweakRequest> requests,
        SystemSnapshot snapshot,
        bool begin,
        CancellationToken cancellationToken)
    {
        if (begin) await store.BeginAsync(transaction, cancellationToken);
        else
        {
            var prepared = await store.LoadAsync(transaction.Id, cancellationToken)
                ?? throw new InvalidOperationException("The prepared transaction journal is missing.");
            if (prepared.Status != TransactionStatus.InProgress || prepared.Results.Count != 0)
                throw new InvalidDataException("The prepared transaction journal is not an empty in-progress record.");
            transaction = prepared;
        }

        var rollbackFailed = false;
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.Operation.IsSupported(snapshot))
            {
                transaction = Add(transaction, new(request.Operation.Descriptor.Id, null,
                    request.RequestedValue, TweakStatus.Skipped, false, "Not supported", DateTimeOffset.UtcNow));
                await store.SaveAsync(transaction, cancellationToken);
                continue;
            }

            string? original = null;
            var mutationAttempted = false;
            try
            {
                original = await request.Operation.ReadCurrentValueAsync(cancellationToken);
                transaction = Add(transaction, new(request.Operation.Descriptor.Id, original,
                    request.RequestedValue, TweakStatus.Pending, false, "Snapshot saved", DateTimeOffset.UtcNow));
                await store.SaveAsync(transaction, cancellationToken);
                mutationAttempted = true;
                await request.Operation.ApplyAsync(request.RequestedValue, cancellationToken);
                var verified = await request.Operation.VerifyAsync(request.RequestedValue, cancellationToken);
                if (!verified) throw new InvalidOperationException("Verification failed");

                transaction = ReplaceLast(transaction, request.Operation is IReadOnlyTweakOperation
                    ? TweakStatus.ReadOnlySucceeded : TweakStatus.Applied, true,
                    request.Operation is IReadOnlyTweakOperation ? "Read-only verification succeeded" : "Applied and verified");
                await store.SaveAsync(transaction, cancellationToken);
            }
            catch (Exception error)
            {
                if (!mutationAttempted)
                {
                    transaction = Add(transaction, new(request.Operation.Descriptor.Id, original, request.RequestedValue,
                        TweakStatus.Failed, false, error.Message, DateTimeOffset.UtcNow));
                }
                else
                {
                    try
                    {
                        await request.Operation.RestoreAsync(original, CancellationToken.None);
                        var restoredValue = await request.Operation.ReadCurrentValueAsync(CancellationToken.None);
                        if (!string.Equals(restoredValue, original, StringComparison.Ordinal))
                            throw new InvalidOperationException("rollback verification failed");
                        transaction = ReplaceLast(transaction, TweakStatus.Restored, true,
                            $"Apply failed and the snapshot was restored: {error.Message}");
                    }
                    catch (Exception rollbackError)
                    {
                        transaction = ReplaceLast(transaction, TweakStatus.Pending, false,
                            $"Apply failed: {error.Message}; rollback failed: {rollbackError.Message}");
                        rollbackFailed = true;
                    }
                }
                await store.SaveAsync(transaction, CancellationToken.None);
                if (rollbackFailed) break;
            }
        }

        transaction = transaction with
        {
            Status = rollbackFailed ? TransactionStatus.PartiallyRolledBack : TransactionStatus.Completed
        };
        await store.SaveAsync(transaction, CancellationToken.None);
        return transaction;
    }

    public async Task<TransactionRecord> RollbackAsync(
        Guid id,
        IReadOnlyDictionary<string, ITweakOperation> operations,
        CancellationToken cancellationToken)
    {
        var transaction = await store.LoadAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Transaction {id:N} was not found");
        var results = transaction.Results.ToList();
        var failed = false;

        for (var index = results.Count - 1; index >= 0; index--)
        {
            if (results[index].Status is not (TweakStatus.Applied or TweakStatus.Pending)) continue;
            if (!operations.TryGetValue(results[index].OperationId, out var operation))
            {
                failed = true;
                results[index] = results[index] with { Status = TweakStatus.Pending, Verified = false, Message = "Restore implementation unavailable; retry is preserved" };
                continue;
            }
            if (operation.Descriptor.RequiresElevation && store is not IPrivilegedTransactionStore)
            {
                failed = true;
                results[index] = results[index] with
                {
                    Status = TweakStatus.Pending,
                    Verified = false,
                    Message = "Privileged restore refused: this compatibility journal is not protected. Open Repair Center to migrate or re-run the scoped administrator operation."
                };
                continue;
            }
            try
            {
                await operation.RestoreAsync(results[index].OriginalValue, cancellationToken);
                var restoredValue = await operation.ReadCurrentValueAsync(cancellationToken);
                if (!string.Equals(restoredValue, results[index].OriginalValue, StringComparison.Ordinal))
                    throw new InvalidOperationException("Restore verification failed");
                results[index] = results[index] with { Status = TweakStatus.Restored, Verified = true, Message = "Restored and verified" };
            }
            catch (Exception error)
            {
                failed = true;
                results[index] = results[index] with { Status = TweakStatus.Pending, Verified = false, Message = $"Restore failed; retry is preserved: {error.Message}" };
            }
        }

        transaction = transaction with
        {
            Results = results,
            Status = failed ? TransactionStatus.PartiallyRolledBack : TransactionStatus.RolledBack
        };
        await store.SaveAsync(transaction, CancellationToken.None);
        return transaction;
    }

    private static TransactionRecord Add(TransactionRecord record, TweakResult result) =>
        record with { Results = [.. record.Results, result] };

    private static TransactionRecord ReplaceLast(
        TransactionRecord record, TweakStatus status, bool verified, string message)
    {
        var results = record.Results.ToArray();
        results[^1] = results[^1] with { Status = status, Verified = verified, Message = message };
        return record with { Results = results };
    }
}
