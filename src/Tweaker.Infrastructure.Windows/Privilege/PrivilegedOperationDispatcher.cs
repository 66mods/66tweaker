

using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Privilege;
using Tweaker.Domain.Services;

namespace Tweaker.Infrastructure.Windows.Privilege;

public sealed class PrivilegedOperationDispatcher
{
    public const string DefaultValueId = "default";
    private readonly ProtectedPlanStore planStore;
    private readonly SystemSnapshot snapshot;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITweakOperation>> catalog;

    public PrivilegedOperationDispatcher(ProtectedPlanStore planStore, SystemSnapshot snapshot,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITweakOperation>> catalog)
    {
        this.planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        this.catalog = ValidateCatalog(catalog);
    }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITweakOperation>> CreateCatalog(
        IEnumerable<ITweakOperation> compiledOperations)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, ITweakOperation>>(StringComparer.Ordinal);
        foreach (var operation in compiledOperations)
        {
            if (!operation.Descriptor.RequiresElevation) continue;
            if (operation is not IRequestedValueProvider)
                throw new InvalidOperationException($"Privileged operation {operation.Descriptor.Id} has no compiled catalog value.");
            if (!PrivilegedOperationRequest.IsCanonicalId(operation.Descriptor.Id) ||
                !result.TryAdd(operation.Descriptor.Id,
                    new Dictionary<string, ITweakOperation>(StringComparer.Ordinal) { [DefaultValueId] = operation }))
                throw new InvalidOperationException("The privileged operation catalog contains an invalid or duplicate ID.");
        }
        return result;
    }

    public IReadOnlyList<TweakDescriptor> Describe(IReadOnlyList<PrivilegedOperationRequest> operations) =>
        Resolve(operations).Select(x => x.Operation.Descriptor).ToArray();

    public async Task<TransactionRecord> DispatchAsync(PrivilegedPlan plan, CancellationToken cancellationToken)
    {
        await using var attempt = await planStore.AcquireAttemptAsync(plan.TransactionId, cancellationToken);
        return await DispatchWithinAttemptAsync(plan, cancellationToken);
    }

    private async Task<TransactionRecord> DispatchWithinAttemptAsync(PrivilegedPlan plan, CancellationToken cancellationToken)
    {
        var existing = await planStore.LoadResultWithinAttemptAsync(plan.TransactionId, cancellationToken);
        if (existing?.Status == TransactionStatus.Completed) return existing;
        if (existing?.Status == TransactionStatus.RolledBack)
            throw new InvalidOperationException("A fully rolled-back protected transaction cannot be applied again.");
        plan.ValidateShape();
        IReadOnlyList<TweakRequest> requests;
        try { requests = Resolve(plan.Operations); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            throw new InvalidDataException("The retained protected plan is incompatible with this recovery catalog; no state was deleted.", error);
        }

        var journalStore = new ProtectedWorkerTransactionStore(planStore, plan.TransactionId);
        var transaction = await new TransactionCoordinator(journalStore)
            .ApplyAsync(plan.TransactionId, requests, snapshot, cancellationToken);
        EnsureStrictSuccess(transaction, requests);
        await planStore.SaveResultWithinAttemptAsync(plan.TransactionId, transaction, CancellationToken.None);
        return transaction;
    }

    public async Task<TransactionRecord> RollbackAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using var attempt = await planStore.AcquireAttemptAsync(transactionId, cancellationToken);
        var existing = await planStore.LoadResultWithinAttemptAsync(transactionId, cancellationToken);
        if (existing?.Status == TransactionStatus.RolledBack) return existing;

        var plan = await planStore.LoadCompletedForRollbackAsync(transactionId, cancellationToken);
        var progress = await planStore.LoadProgressAsync(transactionId, cancellationToken);
        if (progress is null)
        {
            var noMutation = new TransactionRecord(transactionId, DateTimeOffset.UtcNow,
                TransactionStatus.RolledBack, []);
            await planStore.SaveResultWithinAttemptAsync(transactionId, noMutation, CancellationToken.None);
            return noMutation;
        }

        var operations = Resolve(plan.Operations).Select(x => x.Operation)
            .ToDictionary(x => x.Descriptor.Id, StringComparer.Ordinal);
        var journalStore = new ProtectedWorkerTransactionStore(planStore, transactionId);
        var rolledBack = await new TransactionCoordinator(journalStore)
            .RollbackAsync(transactionId, operations, cancellationToken);
        if (!IsStrictRollbackSuccess(rolledBack))
        {
            await planStore.MarkPartiallyRolledBackAsync(transactionId, CancellationToken.None);
            throw new InvalidOperationException("The protected exact rollback is incomplete; authenticated retry state was retained.");
        }
        await planStore.SaveResultWithinAttemptAsync(transactionId, rolledBack, CancellationToken.None);
        return rolledBack;
    }

    public async Task<TransactionRecord> ResumeAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using var attempt = await planStore.AcquireAttemptAsync(transactionId, cancellationToken);
        var existing = await planStore.LoadResultWithinAttemptAsync(transactionId, cancellationToken);
        if (existing?.Status == TransactionStatus.Completed) return existing;
        if (existing?.Status == TransactionStatus.RolledBack)
            throw new InvalidOperationException("A fully rolled-back protected transaction cannot be resumed.");

        var plan = await planStore.LoadForConfirmationAsync(transactionId, cancellationToken);
        var progress = await planStore.LoadProgressAsync(transactionId, cancellationToken);
        if (progress is null)
            return await DispatchWithinAttemptAsync(plan, cancellationToken);
        var requests = Resolve(plan.Operations);
        var operations = requests.Select(x => x.Operation).ToDictionary(x => x.Descriptor.Id, StringComparer.Ordinal);
        var store = new ProtectedWorkerTransactionStore(planStore, transactionId);
        var rolledBack = await new TransactionCoordinator(store).RollbackAsync(transactionId, operations, cancellationToken);
        if (!IsStrictRollbackSuccess(rolledBack))
        {
            await planStore.MarkPartiallyRolledBackAsync(transactionId, CancellationToken.None);
            throw new InvalidOperationException("Recovery could not first restore the exact protected snapshot; authenticated retry state was retained.");
        }
        await planStore.MarkRunningForResumeAsync(transactionId, CancellationToken.None);
        var resumed = await new TransactionCoordinator(store).ApplyAsync(transactionId, requests, snapshot, cancellationToken);
        EnsureStrictSuccess(resumed, requests);
        await planStore.SaveResultWithinAttemptAsync(transactionId, resumed, CancellationToken.None);
        return resumed;
    }

    private static bool IsStrictRollbackSuccess(TransactionRecord transaction) =>
        transaction.Status == TransactionStatus.RolledBack &&
        transaction.Results.All(x => x.Status is not (TweakStatus.Applied or TweakStatus.Pending) &&
            (x.Status != TweakStatus.Restored || x.Verified));

    private IReadOnlyList<TweakRequest> Resolve(IReadOnlyList<PrivilegedOperationRequest> operations)
    {
        if (operations is null || operations.Count is < 1 or > PrivilegedPlan.MaximumOperations)
            throw new InvalidDataException("The privileged operation draft is invalid.");
        var requests = new List<TweakRequest>(operations.Count);
        foreach (var request in operations)
        {
            request.Validate();
            if (!catalog.TryGetValue(request.OperationId, out var values) ||
                !values.TryGetValue(request.RequestedValueId, out var operation))
                throw new InvalidDataException($"Privileged operation/value pair '{request.OperationId}/{request.RequestedValueId}' is not compiled into this executable.");
            if (!operation.Descriptor.RequiresElevation || operation is not IRequestedValueProvider valueProvider)
                throw new InvalidDataException("The worker catalog contains an invalid privileged operation.");
            requests.Add(new(operation, valueProvider.RequestedValue));
        }
        return requests;
    }

    private static void EnsureStrictSuccess(TransactionRecord transaction, IReadOnlyList<TweakRequest> requests)
    {
        if (transaction.Status != TransactionStatus.Completed || transaction.Results.Count != requests.Count)
            throw new InvalidOperationException(
                "The scoped administrator transaction did not complete every requested operation." + FirstReason(transaction));
        for (var index = 0; index < requests.Count; index++)
        {
            var expectedStatus = requests[index].Operation is IReadOnlyTweakOperation
                ? TweakStatus.ReadOnlySucceeded : TweakStatus.Applied;
            var result = transaction.Results[index];
            if (!string.Equals(result.OperationId, requests[index].Operation.Descriptor.Id, StringComparison.Ordinal) ||
                result.Status != expectedStatus || !result.Verified)
                throw new InvalidOperationException(
                    "The scoped administrator transaction retained an unsuccessful result for recovery." + FirstReason(transaction));
        }
    }

    /// <summary>
    /// The first recorded failure reason. Without it the user sees only "did not complete every requested
    /// operation", which says nothing about which registry key or command actually refused.
    /// </summary>
    private static string FirstReason(TransactionRecord transaction)
    {
        var reason = transaction.Results
            .FirstOrDefault(x => !x.Verified || x.Status is TweakStatus.Failed or TweakStatus.Pending)?.Message;
        if (string.IsNullOrWhiteSpace(reason)) return string.Empty;
        var trimmed = reason.Length > 900 ? reason[..900] + "…" : reason;
        return $" {trimmed}";
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITweakOperation>> ValidateCatalog(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITweakOperation>> value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var result = new Dictionary<string, IReadOnlyDictionary<string, ITweakOperation>>(StringComparer.Ordinal);
        foreach (var (operationId, values) in value)
        {
            if (!PrivilegedOperationRequest.IsCanonicalId(operationId) || values is null || values.Count == 0)
                throw new ArgumentException("The privileged dispatcher catalog is invalid.", nameof(value));
            var copiedValues = new Dictionary<string, ITweakOperation>(StringComparer.Ordinal);
            foreach (var (valueId, operation) in values)
            {
                if (!PrivilegedOperationRequest.IsCanonicalId(valueId) || operation is null ||
                    !string.Equals(operation.Descriptor.Id, operationId, StringComparison.Ordinal) ||
                    !copiedValues.TryAdd(valueId, operation))
                    throw new ArgumentException("The privileged dispatcher catalog is invalid.", nameof(value));
            }
            if (!result.TryAdd(operationId, copiedValues))
                throw new ArgumentException("The privileged dispatcher catalog contains duplicate IDs.", nameof(value));
        }
        return result;
    }

    private sealed class ProtectedWorkerTransactionStore(ProtectedPlanStore store, Guid transactionId) : IPrivilegedTransactionStore
    {
        public Task BeginAsync(TransactionRecord record, CancellationToken cancellationToken) => SaveAsync(record, cancellationToken);
        public Task SaveAsync(TransactionRecord record, CancellationToken cancellationToken) =>
            store.SaveProgressAsync(transactionId, record, cancellationToken);
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
            id == transactionId ? store.LoadProgressAsync(transactionId, cancellationToken) : Task.FromResult<TransactionRecord?>(null);
        public async Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken)
        {
            var record = await store.LoadProgressAsync(transactionId, cancellationToken);
            return record?.Status is TransactionStatus.InProgress or TransactionStatus.PartiallyRolledBack ? record : null;
        }
    }
}
