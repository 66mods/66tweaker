





using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Tweaker.Domain.Models;
using Tweaker.Domain.Privilege;

namespace Tweaker.Infrastructure.Windows.Privilege;

public interface IProtectedPlanKeyProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedBytes);
}

public interface IProtectedPlanTransitionObserver
{
    void Reached(string transition, Guid transactionId);
}
public sealed class NullProtectedPlanTransitionObserver : IProtectedPlanTransitionObserver
{
    public static NullProtectedPlanTransitionObserver Instance { get; } = new();
    public void Reached(string transition, Guid transactionId) { }
}

public interface IProtectedPlanAccessControl
{
    void ProtectDirectory(string path, string initiatingIdentity);
    void ProtectFile(string path, string initiatingIdentity, bool initiatingUserCanWrite);
    void ValidateDirectory(string path, string initiatingIdentity);
    void ValidateFile(string path, string initiatingIdentity) { }
}

public sealed record ProtectedPlanStoreOptions(
    string RootPath,
    string InitiatingIdentity,
    string ExecutableIdentity,
    TimeProvider TimeProvider,
    IProtectedPlanKeyProtector KeyProtector,
    IProtectedPlanAccessControl AccessControl)
{
    public const string CurrentRecoveryIdentity = "66mods|66mods Tweaker|protected-catalog-v1";
    public string RecoveryIdentity { get; init; } = CurrentRecoveryIdentity;
    public IProtectedPlanTransitionObserver TransitionObserver { get; init; } = NullProtectedPlanTransitionObserver.Instance;
    public static ProtectedPlanStoreOptions ForCurrentProcess(
        string? rootPath = null,
        string? authenticatedInitiator = null,
        string? authenticatedExecutableIdentity = null)
    {
        var identity = authenticatedInitiator ?? WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The initiating Windows identity is unavailable.");
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        var canonicalExecutable = Path.GetFullPath(executable);
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(canonicalExecutable).FileVersion ?? "0.0.0.0";
        using var stream = new FileStream(canonicalExecutable, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var fileHash = SHA256.HashData(stream);
        var identityBytes = Encoding.UTF8.GetBytes($"{canonicalExecutable}\0{version}\0{Convert.ToHexString(fileHash)}");
        var executableIdentity = authenticatedExecutableIdentity
            ?? Convert.ToHexString(SHA256.HashData(identityBytes));
        var root = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "66mods Tweaker", "Transactions");
        return new(Path.GetFullPath(root), identity, executableIdentity, TimeProvider.System,
            new MachineDpapiKeyProtector(), new WindowsProtectedPlanAccessControl());
    }
}

public sealed class ProtectedPlanStore
{
    private const int MaximumPlanBytes = 64 * 1024;
    private const int MaximumJournalBytes = 1024 * 1024;
    private static readonly TimeSpan MaximumPlanAge = TimeSpan.FromMinutes(30);
    private readonly ProtectedPlanStoreOptions options;
    private readonly string root;

    public ProtectedPlanStore() : this(ProtectedPlanStoreOptions.ForCurrentProcess()) { }
    public ProtectedPlanStore(string rootPath) : this(ProtectedPlanStoreOptions.ForCurrentProcess(rootPath)) { }

    public ProtectedPlanStore(ProtectedPlanStoreOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        root = Path.GetFullPath(options.RootPath);
        if (string.IsNullOrWhiteSpace(options.InitiatingIdentity) || options.InitiatingIdentity.Length > 256 ||
            string.IsNullOrWhiteSpace(options.ExecutableIdentity) || options.ExecutableIdentity.Length > 256 ||
            string.IsNullOrWhiteSpace(options.RecoveryIdentity) || options.RecoveryIdentity.Length > 256)
            throw new ArgumentException("Protected plan identity metadata is invalid.", nameof(options));
    }

    public string RootPath => root;

    public Task<PrivilegedPlan> CreateAsync(
        IReadOnlyList<PrivilegedOperationRequest> operations,
        CancellationToken cancellationToken) =>
        CreateAsync(Guid.NewGuid(), operations, cancellationToken);

    public async Task<PrivilegedPlan> CreateAsync(
        Guid transactionId,
        IReadOnlyList<PrivilegedOperationRequest> operations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSealingAuthority();
        var unsigned = new PrivilegedPlan(transactionId, PrivilegedPlan.CurrentSchemaVersion,
            operations?.ToArray() ?? throw new ArgumentNullException(nameof(operations)), new string('0', 64));
        unsigned.ValidateShape();
        EnsureProtectedRoot();

        var createdUtc = options.TimeProvider.GetUtcNow();
        var envelope = new PlanEnvelope(unsigned.SchemaVersion, transactionId, createdUtc,
            options.InitiatingIdentity, options.ExecutableIdentity, options.RecoveryIdentity,
            ComputeCatalogIdentity(unsigned.Operations), PlanState.Pending, unsigned.Operations, string.Empty);
        var key = LoadOrCreateKey();
        try
        {
            envelope = envelope with { Integrity = ComputeIntegrity(key, WriteCanonicalPlan(envelope, includeIntegrity: false)) };
            var bytes = WriteCanonicalPlan(envelope, includeIntegrity: true);
            var destination = PathFor(transactionId, ".plan.json");
            await WriteCreateNewAsync(destination, bytes, cancellationToken);
            options.AccessControl.ProtectFile(destination, options.InitiatingIdentity, initiatingUserCanWrite: false);
            options.AccessControl.ValidateFile(destination, options.InitiatingIdentity);
            return new(transactionId, envelope.SchemaVersion, envelope.Operations, envelope.Integrity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<PrivilegedPlan> LoadAndValidateAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSealingAuthority();
        EnsureProtectedRoot();
        if (File.Exists(PathFor(transactionId, ".result.json")))
            throw new InvalidDataException("The privileged transaction is already complete and cannot be replayed.");

        var pending = PathFor(transactionId, ".plan.json");
        var running = PathFor(transactionId, ".running.json");
        if (File.Exists(running))
        {
            var recovered = ParsePlan(await ReadExclusiveAsync(running, MaximumPlanBytes, cancellationToken));
            ValidateEnvelope(recovered, transactionId, requireFreshApply: true, PlanState.Running);
            if (File.Exists(pending)) File.Delete(pending);
            return new(transactionId, recovered.SchemaVersion, recovered.Operations, recovered.Integrity);
        }

        var envelope = ParsePlan(await ReadExclusiveAsync(pending, MaximumPlanBytes, cancellationToken));
        ValidateEnvelope(envelope, transactionId, requireFreshApply: true, PlanState.Pending);
        var runningEnvelope = envelope with { State = PlanState.Running, Integrity = string.Empty };
        var key = LoadOrCreateKey();
        try
        {
            runningEnvelope = runningEnvelope with
            {
                Integrity = ComputeIntegrity(key, WriteCanonicalPlan(runningEnvelope, includeIntegrity: false))
            };
            await WriteCreateNewAsync(running, WriteCanonicalPlan(runningEnvelope, includeIntegrity: true), cancellationToken);
            options.AccessControl.ProtectFile(running, options.InitiatingIdentity, initiatingUserCanWrite: false);
            options.AccessControl.ValidateFile(running, options.InitiatingIdentity);
            options.TransitionObserver.Reached("running-authenticated", transactionId);
            File.Delete(pending);
            options.TransitionObserver.Reached("draft-consumed", transactionId);
            return new(transactionId, runningEnvelope.SchemaVersion, runningEnvelope.Operations, runningEnvelope.Integrity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task SaveResultAsync(Guid transactionId, TransactionRecord transaction, CancellationToken cancellationToken)
    {
        await using var attempt = await AcquireAttemptAsync(transactionId, cancellationToken);
        await SaveResultWithinAttemptAsync(transactionId, transaction, cancellationToken);
    }

    internal async Task SaveResultWithinAttemptAsync(Guid transactionId, TransactionRecord transaction, CancellationToken cancellationToken)
    {
        if (transaction.Id != transactionId)
            throw new InvalidDataException("The privileged result transaction ID does not match its plan.");
        EnsureProtectedRoot();
        var plan = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        if (plan.State == PlanState.Pending)
            throw new InvalidDataException("An unclaimed privileged plan cannot publish a result.");

        var resultPath = PathFor(transactionId, ".result.json");
        if (File.Exists(resultPath))
        {
            var existing = ParseAndValidateRecord(
                await ReadExclusiveAsync(resultPath, MaximumJournalBytes, cancellationToken), transactionId, RecordKind.Result, plan);
            if (existing.Status != TransactionStatus.Completed || transaction.Status != TransactionStatus.RolledBack)
                throw new InvalidDataException("The privileged transaction result transition is invalid.");
        }

        var bytes = CreateBoundRecordBytes(plan, RecordKind.Result, transaction);
        options.TransitionObserver.Reached("before-result-publication", transactionId);
        await WriteAtomicAsync(resultPath, bytes, cancellationToken);
        options.TransitionObserver.Reached("result-authenticated", transactionId);
        if (transaction.Status is not (TransactionStatus.Completed or TransactionStatus.RolledBack))
            throw new InvalidDataException("Only a strict terminal transaction may be published as a result.");
        await ReconcileTerminalResultAsync(plan, transaction, CancellationToken.None);
        options.TransitionObserver.Reached("terminal-cleanup-complete", transactionId);
    }

    public Task SaveResultAsync(PrivilegedPlan plan, TransactionRecord transaction, CancellationToken cancellationToken) =>
        SaveResultAsync(plan.TransactionId, transaction, cancellationToken);

    public async Task<TransactionRecord?> LoadResultAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using var attempt = await AcquireAttemptAsync(transactionId, cancellationToken);
        return await LoadResultWithinAttemptAsync(transactionId, cancellationToken);
    }

    internal async Task<TransactionRecord?> LoadResultWithinAttemptAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        EnsureProtectedRoot();
        var path = PathFor(transactionId, ".result.json");
        if (!File.Exists(path)) return null;
        var plan = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        var result = ParseAndValidateRecord(await ReadExclusiveAsync(path, MaximumJournalBytes, cancellationToken),
            transactionId, RecordKind.Result, plan);
        await ReconcileTerminalResultAsync(plan, result, CancellationToken.None);
        return result;
    }

    public Task DeleteAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureProtectedRoot();
        if (File.Exists(PathFor(transactionId, ".running.json")) ||
            File.Exists(PathFor(transactionId, ".result.json")) ||
            File.Exists(PathFor(transactionId, ".journal.json")))
            throw new InvalidOperationException("Started privileged transactions are retained for recovery and audit.");
        var pending = PathFor(transactionId, ".plan.json");
        if (File.Exists(pending)) File.Delete(pending);
        return Task.CompletedTask;
    }

    public async Task SaveProgressAsync(Guid transactionId, TransactionRecord transaction, CancellationToken cancellationToken)
    {
        if (transaction.Id != transactionId)
            throw new InvalidDataException("The privileged journal transaction ID does not match its plan.");
        var plan = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        if (plan.State == PlanState.Pending)
            throw new InvalidDataException("An unclaimed privileged plan cannot write a recovery journal.");
        await WriteAtomicAsync(PathFor(transactionId, ".journal.json"),
            CreateBoundRecordBytes(plan, RecordKind.Journal, transaction), cancellationToken);
    }

    public async Task<TransactionRecord?> LoadProgressAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var path = PathFor(transactionId, ".journal.json");
        if (!File.Exists(path)) return null;
        var plan = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        return ParseAndValidateRecord(await ReadExclusiveAsync(path, MaximumJournalBytes, cancellationToken),
            transactionId, RecordKind.Journal, plan);
    }

    public async Task<PrivilegedPlan> LoadForConfirmationAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        EnsureSealingAuthority();
        EnsureProtectedRoot();
        var envelope = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        if (envelope.State == PlanState.Pending)
            throw new InvalidDataException("An unclaimed Apply draft is not a recovery transaction.");
        return ToPlan(envelope);
    }

    public async Task<PrivilegedPlan> LoadCompletedForRollbackAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        EnsureSealingAuthority();
        EnsureProtectedRoot();
        var plan = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        if (plan.State == PlanState.Pending)
            throw new InvalidDataException("An unclaimed Apply draft cannot be rolled back.");

        var journalPath = PathFor(transactionId, ".journal.json");
        if (!File.Exists(journalPath))
        {
            var resultPath = PathFor(transactionId, ".result.json");
            if (!File.Exists(resultPath))
                return ToPlan(plan);
            var result = ParseAndValidateRecord(
                await ReadExclusiveAsync(resultPath, MaximumJournalBytes, cancellationToken),
                transactionId, RecordKind.Result, plan);
            if (result.Status == TransactionStatus.RolledBack)
                return ToPlan(plan);
            if (result.Status != TransactionStatus.Completed)
                throw new InvalidDataException("Only a completed protected transaction can begin terminal rollback.");
            await WriteAtomicAsync(journalPath, CreateBoundRecordBytes(plan, RecordKind.Journal, result), cancellationToken);
            options.TransitionObserver.Reached("rollback-journal-authenticated", transactionId);
        }

        if (plan.State is PlanState.Completed or PlanState.PartiallyRolledBack)
            await TransitionPlanStateAsync(plan, PlanState.RollbackRunning, CancellationToken.None);
        return ToPlan(plan);
    }

    public async Task MarkPartiallyRolledBackAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var plan = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        await TransitionPlanStateAsync(plan, PlanState.PartiallyRolledBack, CancellationToken.None);
        options.TransitionObserver.Reached("partial-rollback-retained", transactionId);
    }

    internal async Task<IAsyncDisposable> AcquireAttemptAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        EnsureSealingAuthority();
        EnsureProtectedRoot();
        var path = PathFor(transactionId, ".attempt.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existed = File.Exists(path);
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                try
                {
                    ValidateFinalHandle(stream, path);
                    if (!existed) options.AccessControl.ProtectFile(path, options.InitiatingIdentity, initiatingUserCanWrite: false);
                    options.AccessControl.ValidateFile(path, options.InitiatingIdentity);
                    var attemptId = Guid.NewGuid();
                    var bytes = CreateAttemptClaimBytes(transactionId, attemptId);
                    stream.SetLength(0);
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                    stream.Position = 0;
                    options.TransitionObserver.Reached("attempt-claimed", transactionId);
                    return stream;
                }
                catch { await stream.DisposeAsync(); throw; }
            }
            catch (IOException error) when (IsSharingViolation(error))
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }

    private static bool IsSharingViolation(IOException error) =>
        (error.HResult & 0xffff) is 32 or 33;

    internal async Task MarkRunningForResumeAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var plan = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        if (plan.State == PlanState.RollbackRunning)
            await TransitionPlanStateAsync(plan, PlanState.Running, CancellationToken.None);
    }

    private async Task ReconcileTerminalResultAsync(PlanEnvelope plan, TransactionRecord result, CancellationToken cancellationToken)
    {
        PlanState? target = result.Status switch
        {
            TransactionStatus.Completed when plan.State is PlanState.Running or PlanState.Completed => PlanState.Completed,
            TransactionStatus.RolledBack when plan.State is PlanState.Running or PlanState.RollbackRunning or
                PlanState.PartiallyRolledBack or PlanState.RolledBack => PlanState.RolledBack,
            TransactionStatus.Completed when plan.State is PlanState.RollbackRunning or PlanState.PartiallyRolledBack => null,
            _ => throw new InvalidDataException("The authenticated terminal result contradicts the retained plan state.")
        };
        if (target is null) return;
        await TransitionPlanStateAsync(plan, target.Value, cancellationToken);
        var journal = PathFor(plan.TransactionId, ".journal.json");
        if (File.Exists(journal)) File.Delete(journal);
    }

    private byte[] CreateAttemptClaimBytes(Guid transactionId, Guid attemptId)
    {
        var material = Encoding.UTF8.GetBytes($"attempt-v1\n{transactionId:N}\n{attemptId:N}\n" +
            $"{options.InitiatingIdentity}\n{options.RecoveryIdentity}\n");
        var key = LoadOrCreateKey();
        try
        {
            var integrity = Convert.ToHexString(HMACSHA256.HashData(key, material));
            return [.. material, .. Encoding.ASCII.GetBytes(integrity + "\n")];
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task<PlanEnvelope> LoadAuthenticatedPlanEnvelopeAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var running = PathFor(transactionId, ".running.json");
        if (!File.Exists(running))
            throw new InvalidDataException("The protected transaction has no authenticated retained plan.");
        var envelope = ParsePlan(await ReadExclusiveAsync(running, MaximumPlanBytes, cancellationToken));
        ValidateEnvelope(envelope, transactionId, requireFreshApply: false,
            PlanState.Running, PlanState.RollbackRunning, PlanState.PartiallyRolledBack,
            PlanState.Completed, PlanState.RolledBack);
        return envelope;
    }

    private async Task TransitionPlanStateAsync(PlanEnvelope envelope, PlanState state, CancellationToken cancellationToken)
    {
        if (envelope.State == state) return;
        var changed = envelope with { State = state, Integrity = string.Empty };
        var key = LoadOrCreateKey();
        try
        {
            changed = changed with { Integrity = ComputeIntegrity(key, WriteCanonicalPlan(changed, includeIntegrity: false)) };
            await WriteAtomicAsync(PathFor(envelope.TransactionId, ".running.json"),
                WriteCanonicalPlan(changed, includeIntegrity: true), cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private void ValidateEnvelope(PlanEnvelope envelope, Guid expectedId, bool requireFreshApply, params PlanState[] expectedStates)
    {
        var plan = new PrivilegedPlan(envelope.TransactionId, envelope.SchemaVersion, envelope.Operations, envelope.Integrity);
        plan.ValidateShape();
        if (envelope.TransactionId != expectedId)
            throw new InvalidDataException("The privileged plan was substituted across transaction IDs.");
        if (!expectedStates.Contains(envelope.State))
            throw new InvalidDataException("The privileged plan is in the wrong state.");
        if (!string.Equals(envelope.InitiatingIdentity, options.InitiatingIdentity, StringComparison.Ordinal) ||
            !string.Equals(envelope.RecoveryIdentity, options.RecoveryIdentity, StringComparison.Ordinal))
            throw new InvalidDataException("The privileged plan recovery identity does not match this worker or initiator.");
        if (!string.Equals(envelope.CatalogIdentity, ComputeCatalogIdentity(envelope.Operations), StringComparison.Ordinal))
            throw new InvalidDataException("The privileged plan catalog identity is invalid.");
        var now = options.TimeProvider.GetUtcNow();
        if (envelope.CreatedUtc > now.AddMinutes(1))
            throw new InvalidDataException("The privileged plan timestamp is in the future.");
        if (requireFreshApply &&
            (!string.Equals(envelope.ExecutableIdentity, options.ExecutableIdentity, StringComparison.Ordinal) ||
             now - envelope.CreatedUtc > MaximumPlanAge))
            throw new InvalidDataException("The privileged Apply draft is stale or belongs to a different executable.");

        var key = LoadOrCreateKey();
        try
        {
            var expected = ComputeIntegrity(key, WriteCanonicalPlan(envelope, includeIntegrity: false));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(envelope.Integrity)))
                throw new InvalidDataException("The privileged plan failed keyed integrity validation.");
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("The privileged plan integrity value is invalid.", error);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static PrivilegedPlan ToPlan(PlanEnvelope envelope) =>
        new(envelope.TransactionId, envelope.SchemaVersion, envelope.Operations, envelope.Integrity);

    private void RejectExistingTerminalOrRunningState(Guid transactionId)
    {
        if (File.Exists(PathFor(transactionId, ".result.json")))
            throw new InvalidDataException("The privileged transaction is already complete and cannot be replayed.");
        if (File.Exists(PathFor(transactionId, ".running.json")) || File.Exists(PathFor(transactionId, ".journal.json")))
            throw new InvalidDataException("The privileged transaction has already started; its protected journal was retained for recovery.");
    }

    private byte[] LoadOrCreateKey()
    {
        var path = Path.Combine(root, ".integrity-key");
        EnsureContained(path);
        if (!File.Exists(path))
        {
            var plaintext = RandomNumberGenerator.GetBytes(32);
            try
            {
                var protectedBytes = options.KeyProtector.Protect(plaintext);
                try
                {
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        4096, FileOptions.WriteThrough);
                    ValidateFinalHandle(stream, path);
                    stream.Write(protectedBytes);
                    stream.Flush(flushToDisk: true);
                    options.AccessControl.ProtectFile(path, options.InitiatingIdentity, initiatingUserCanWrite: false);
                    options.AccessControl.ValidateFile(path, options.InitiatingIdentity);
                }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        RejectReparse(path);
        options.AccessControl.ValidateFile(path, options.InitiatingIdentity);
        var protectedKey = ReadExclusive(path, 4096);
        var key = options.KeyProtector.Unprotect(protectedKey);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("The protected integrity key is invalid.");
        }
        return key;
    }

    private void EnsureSealingAuthority()
    {
        if (options.AccessControl is not WindowsProtectedPlanAccessControl) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Only the elevated scoped worker may seal or open protected transactions.");
    }

    private void EnsureProtectedRoot()
    {
        var parent = Directory.GetParent(root)?.FullName
            ?? throw new InvalidOperationException("The protected transaction root has no parent.");
        EnsureExactProtectedDirectory(parent);
        EnsureExactProtectedDirectory(root);
    }

    private void EnsureExactProtectedDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            RejectReparse(path);
            try
            {
                options.AccessControl.ValidateDirectory(path, options.InitiatingIdentity);
                return;
            }
            catch (InvalidDataException)
            {
                var quarantine = path + ".untrusted-" + Guid.NewGuid().ToString("N");
                Directory.Move(path, quarantine);
            }
        }
        Directory.CreateDirectory(path);
        RejectReparse(path);
        options.AccessControl.ProtectDirectory(path, options.InitiatingIdentity);
        options.AccessControl.ValidateDirectory(path, options.InitiatingIdentity);
    }

    public async Task<IReadOnlyList<TransactionRecord>> LoadRecentAsync(int limit, CancellationToken cancellationToken)
    {
        EnsureSealingAuthority();
        EnsureProtectedRoot();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var records = new List<TransactionRecord>();
        foreach (var path in Directory.EnumerateFiles(root, "*.running.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryIdFromPath(path, ".running.json", out var id)) continue;
            await using var attempt = await AcquireAttemptAsync(id, cancellationToken);
            var plan = ParsePlan(await ReadExclusiveAsync(path, MaximumPlanBytes, cancellationToken));
            if (!string.Equals(plan.InitiatingIdentity, options.InitiatingIdentity, StringComparison.Ordinal)) continue;
            ValidateEnvelope(plan, id, requireFreshApply: false, PlanState.Running, PlanState.RollbackRunning,
                PlanState.PartiallyRolledBack, PlanState.Completed, PlanState.RolledBack);
            var resultPath = PathFor(id, ".result.json");
            TransactionRecord? result = null;
            if (File.Exists(resultPath))
            {
                result = ParseAndValidateRecord(await ReadExclusiveAsync(resultPath, MaximumJournalBytes, cancellationToken),
                    id, RecordKind.Result, plan);
                await ReconcileTerminalResultAsync(plan, result, CancellationToken.None);
                plan = await LoadAuthenticatedPlanEnvelopeAsync(id, cancellationToken);
            }
            var journalPath = PathFor(id, ".journal.json");
            if (File.Exists(journalPath) && plan.State is PlanState.RollbackRunning or PlanState.PartiallyRolledBack)
                records.Add(ParseAndValidateRecord(await ReadExclusiveAsync(journalPath, MaximumJournalBytes, cancellationToken),
                    id, RecordKind.Journal, plan));
            else if (result is not null) records.Add(result);
            else records.Add(new(id, plan.CreatedUtc, plan.State == PlanState.PartiallyRolledBack
                ? TransactionStatus.PartiallyRolledBack : TransactionStatus.InProgress, []));
        }
        return records.OrderByDescending(x => x.StartedAt).Take(limit).ToArray();
    }

    private static bool TryIdFromPath(string path, string suffix, out Guid id)
    {
        id = Guid.Empty;
        var name = Path.GetFileName(path);
        return name.Length == 32 + suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal) &&
            Guid.TryParseExact(name[..32], "N", out id) && id != Guid.Empty;
    }

    public async Task<TransactionRecord> LoadForRecoveryAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        EnsureSealingAuthority();
        EnsureProtectedRoot();
        _ = await LoadAuthenticatedPlanEnvelopeAsync(transactionId, cancellationToken);
        return await LoadProgressAsync(transactionId, cancellationToken)
            ?? throw new InvalidDataException("The protected transaction has no recoverable journal.");
    }

    private string PathFor(Guid id, string suffix)
    {
        if (id == Guid.Empty) throw new InvalidDataException("The privileged transaction ID is invalid.");
        var path = Path.GetFullPath(Path.Combine(root, id.ToString("N") + suffix));
        EnsureContained(path);
        return path;
    }

    private void EnsureContained(string path)
    {
        var canonical = Path.GetFullPath(path);
        if (!canonical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The protected transaction path escaped its root.");
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Reparse points are not permitted in protected transaction paths.");
    }

    private async Task<byte[]> ReadExclusiveAsync(string path, int limit, CancellationToken cancellationToken)
    {
        EnsureContained(path);
        RejectReparse(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ValidateFinalHandle(stream, path);
        if (stream.Length is < 1 || stream.Length > limit)
            throw new InvalidDataException("The protected transaction file size is invalid.");
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);

        return bytes;
    }

    private byte[] ReadExclusive(string path, int limit)
    {
        EnsureContained(path);
        RejectReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
            4096, FileOptions.SequentialScan);
        ValidateFinalHandle(stream, path);
        if (stream.Length is < 1 || stream.Length > limit)
            throw new InvalidDataException("The protected transaction file size is invalid.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static async Task WriteCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        ValidateFinalHandle(stream, path);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private async Task WriteAtomicAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        EnsureContained(destination);
        var temporary = Path.Combine(root, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        EnsureContained(temporary);
        try
        {
            await WriteCreateNewAsync(temporary, bytes, cancellationToken);
            options.AccessControl.ProtectFile(temporary, options.InitiatingIdentity, initiatingUserCanWrite: false);
            options.AccessControl.ValidateFile(temporary, options.InitiatingIdentity);
            RejectReparse(temporary);
            File.Move(temporary, destination, overwrite: true);
            options.AccessControl.ValidateFile(destination, options.InitiatingIdentity);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateFinalHandle(FileStream stream, string expectedPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "The final protected transaction handle path could not be resolved.");
        var actual = buffer.ToString();
        actual = actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + actual[8..]
            : actual.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? actual[4..] : actual;
        if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The opened protected transaction handle escaped its canonical path.");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    private static string ComputeIntegrity(byte[] key, byte[] canonicalBytes) =>
        Convert.ToHexString(HMACSHA256.HashData(key, canonicalBytes));
    private static string ComputeCatalogIdentity(IReadOnlyList<PrivilegedOperationRequest> operations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var operation in operations)
            {
                writer.WriteStartObject();
                writer.WriteString("operationId", operation.OperationId);
                writer.WriteString("requestedValueId", operation.RequestedValueId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string ComputePlanIdentity(PlanEnvelope envelope)
    {
        var material = Encoding.UTF8.GetBytes(
            $"plan-identity\n{envelope.SchemaVersion}\n{envelope.TransactionId:N}\n{envelope.CreatedUtc.UtcDateTime:O}\n" +
            $"{envelope.InitiatingIdentity}\n{envelope.ExecutableIdentity}\n{envelope.RecoveryIdentity}\n{envelope.CatalogIdentity}\n");
        return Convert.ToHexString(SHA256.HashData(material));
    }

    private static string ComputeRecordIntegrity(byte[] key, RecordEnvelope envelope, byte[] transactionBytes)
    {
        var prefix = Encoding.UTF8.GetBytes(
            $"{envelope.RecordKind}\n{envelope.State}\n{envelope.SchemaVersion}\n{envelope.TransactionId:N}\n" +
            $"{envelope.InitiatingIdentity}\n{envelope.ExecutableIdentity}\n{envelope.RecoveryIdentity}\n" +
            $"{envelope.CatalogIdentity}\n{envelope.PlanIdentity}\n");
        var input = new byte[prefix.Length + transactionBytes.Length];
        prefix.CopyTo(input, 0);
        transactionBytes.CopyTo(input, prefix.Length);
        return Convert.ToHexString(HMACSHA256.HashData(key, input));
    }

    private static byte[] WriteCanonicalPlan(PlanEnvelope envelope, bool includeIntegrity)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", envelope.SchemaVersion);
            writer.WriteString("transactionId", envelope.TransactionId.ToString("N"));
            writer.WriteString("createdUtc", envelope.CreatedUtc.UtcDateTime.ToString("O"));
            writer.WriteString("initiatingIdentity", envelope.InitiatingIdentity);
            writer.WriteString("executableIdentity", envelope.ExecutableIdentity);
            writer.WriteString("recoveryIdentity", envelope.RecoveryIdentity);
            writer.WriteString("catalogIdentity", envelope.CatalogIdentity);
            writer.WriteString("state", StateText(envelope.State));
            writer.WritePropertyName("operations");
            writer.WriteStartArray();
            foreach (var operation in envelope.Operations)
            {
                writer.WriteStartObject();
                writer.WriteString("operationId", operation.OperationId);
                writer.WriteString("requestedValueId", operation.RequestedValueId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (includeIntegrity) writer.WriteString("integrity", envelope.Integrity);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static PlanEnvelope ParsePlan(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var value = document.RootElement;
            RequireObjectWithExactProperties(value, "schemaVersion", "transactionId", "createdUtc",
                "initiatingIdentity", "executableIdentity", "recoveryIdentity", "catalogIdentity",
                "state", "operations", "integrity");
            var schema = value.GetProperty("schemaVersion").GetInt32();
            var idText = value.GetProperty("transactionId").GetString();
            if (idText is null || idText.Length != 32 || !Guid.TryParseExact(idText, "N", out var id) ||
                !string.Equals(idText, id.ToString("N"), StringComparison.Ordinal))
                throw new InvalidDataException("The privileged transaction ID is not canonical.");
            var createdText = value.GetProperty("createdUtc").GetString();
            if (!DateTimeOffset.TryParseExact(createdText, "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var createdUtc))
                throw new InvalidDataException("The privileged plan timestamp is invalid.");
            var state = value.GetProperty("state").GetString() switch
            {
                "pending" => PlanState.Pending,
                "running" => PlanState.Running,
                "rollback-running" => PlanState.RollbackRunning,
                "partially-rolled-back" => PlanState.PartiallyRolledBack,
                "completed" => PlanState.Completed,
                "rolled-back" => PlanState.RolledBack,
                _ => throw new InvalidDataException("The privileged plan state is invalid.")
            };
            var array = value.GetProperty("operations");
            if (array.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The privileged operations must be an array.");
            var operations = new List<PrivilegedOperationRequest>();
            foreach (var item in array.EnumerateArray())
            {
                RequireObjectWithExactProperties(item, "operationId", "requestedValueId");
                operations.Add(new(RequiredBoundedString(item, "operationId", 128),
                    RequiredBoundedString(item, "requestedValueId", 128)));
                if (operations.Count > PrivilegedPlan.MaximumOperations)
                    throw new InvalidDataException("The privileged plan has too many operations.");
            }
            return new(schema, id, createdUtc, RequiredBoundedString(value, "initiatingIdentity", 256),
                RequiredBoundedString(value, "executableIdentity", 256),
                RequiredBoundedString(value, "recoveryIdentity", 256),
                RequiredBoundedString(value, "catalogIdentity", 64), state, operations,
                RequiredBoundedString(value, "integrity", 64));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The privileged plan JSON is invalid.", error);
        }
    }

    private byte[] CreateBoundRecordBytes(PlanEnvelope plan, RecordKind kind, TransactionRecord transaction)
    {
        var transactionBytes = WriteCanonicalTransaction(transaction);
        if (transactionBytes.Length > MaximumJournalBytes)
            throw new InvalidDataException("The privileged transaction record is too large.");
        var envelope = new RecordEnvelope(PrivilegedPlan.CurrentSchemaVersion, transaction.Id,
            plan.InitiatingIdentity, plan.ExecutableIdentity, plan.RecoveryIdentity, kind,
            TransactionStateText(transaction.Status), plan.CatalogIdentity, ComputePlanIdentity(plan), transaction, string.Empty);
        var key = LoadOrCreateKey();
        try
        {
            envelope = envelope with { Integrity = ComputeRecordIntegrity(key, envelope, transactionBytes) };
            return WriteCanonicalRecord(envelope, transactionBytes);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] WriteCanonicalRecord(RecordEnvelope envelope, byte[] transactionBytes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", envelope.SchemaVersion);
            writer.WriteString("transactionId", envelope.TransactionId.ToString("N"));
            writer.WriteString("initiatingIdentity", envelope.InitiatingIdentity);
            writer.WriteString("executableIdentity", envelope.ExecutableIdentity);
            writer.WriteString("recoveryIdentity", envelope.RecoveryIdentity);
            writer.WriteString("recordKind", envelope.RecordKind == RecordKind.Result ? "result" : "journal");
            writer.WriteString("state", envelope.State);
            writer.WriteString("catalogIdentity", envelope.CatalogIdentity);
            writer.WriteString("planIdentity", envelope.PlanIdentity);
            writer.WritePropertyName("transaction");
            writer.WriteRawValue(transactionBytes, skipInputValidation: false);
            writer.WriteString("integrity", envelope.Integrity);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private TransactionRecord ParseAndValidateRecord(byte[] bytes, Guid expectedId, RecordKind expectedKind, PlanEnvelope plan)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            var value = document.RootElement;
            RequireObjectWithExactProperties(value, "schemaVersion", "transactionId", "initiatingIdentity",
                "executableIdentity", "recoveryIdentity", "recordKind", "state", "catalogIdentity",
                "planIdentity", "transaction", "integrity");
            if (value.GetProperty("schemaVersion").GetInt32() != PrivilegedPlan.CurrentSchemaVersion)
                throw new InvalidDataException("The privileged record schema is unsupported.");
            var idText = value.GetProperty("transactionId").GetString();
            if (idText is null || !Guid.TryParseExact(idText, "N", out var id) || id != expectedId ||
                !string.Equals(idText, id.ToString("N"), StringComparison.Ordinal))
                throw new InvalidDataException("The privileged record transaction ID is invalid.");
            var kind = RequiredBoundedString(value, "recordKind", 16) switch
            {
                "result" => RecordKind.Result,
                "journal" => RecordKind.Journal,
                _ => throw new InvalidDataException("The privileged record kind is invalid.")
            };
            if (kind != expectedKind) throw new InvalidDataException("The privileged record kind was substituted.");
            var transaction = ParseTransaction(value.GetProperty("transaction"));
            if (transaction.Id != expectedId) throw new InvalidDataException("The privileged record was substituted across transaction IDs.");
            var envelope = new RecordEnvelope(PrivilegedPlan.CurrentSchemaVersion, id,
                RequiredBoundedString(value, "initiatingIdentity", 256),
                RequiredBoundedString(value, "executableIdentity", 256),
                RequiredBoundedString(value, "recoveryIdentity", 256), kind,
                RequiredBoundedString(value, "state", 32),
                RequiredBoundedString(value, "catalogIdentity", 64),
                RequiredBoundedString(value, "planIdentity", 64), transaction,
                RequiredBoundedString(value, "integrity", 64));
            if (!string.Equals(envelope.InitiatingIdentity, options.InitiatingIdentity, StringComparison.Ordinal) ||
                !string.Equals(envelope.InitiatingIdentity, plan.InitiatingIdentity, StringComparison.Ordinal) ||
                !string.Equals(envelope.ExecutableIdentity, plan.ExecutableIdentity, StringComparison.Ordinal) ||
                !string.Equals(envelope.RecoveryIdentity, options.RecoveryIdentity, StringComparison.Ordinal) ||
                !string.Equals(envelope.RecoveryIdentity, plan.RecoveryIdentity, StringComparison.Ordinal) ||
                !string.Equals(envelope.CatalogIdentity, plan.CatalogIdentity, StringComparison.Ordinal) ||
                !string.Equals(envelope.PlanIdentity, ComputePlanIdentity(plan), StringComparison.Ordinal) ||
                !string.Equals(envelope.State, TransactionStateText(transaction.Status), StringComparison.Ordinal))
                throw new InvalidDataException("The privileged record identity or state binding is invalid.");
            var transactionBytes = WriteCanonicalTransaction(transaction);
            var key = LoadOrCreateKey();
            try
            {
                var expected = ComputeRecordIntegrity(key, envelope, transactionBytes);
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(envelope.Integrity)))
                    throw new InvalidDataException("The privileged record failed keyed integrity validation.");
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            return transaction;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The privileged record JSON is invalid.", error);
        }
    }

    private static string StateText(PlanState state) => state switch
    {
        PlanState.Pending => "pending",
        PlanState.Running => "running",
        PlanState.RollbackRunning => "rollback-running",
        PlanState.PartiallyRolledBack => "partially-rolled-back",
        PlanState.Completed => "completed",
        PlanState.RolledBack => "rolled-back",
        _ => throw new InvalidDataException("The privileged plan state is invalid.")
    };

    private static string TransactionStateText(TransactionStatus state) => state switch
    {
        TransactionStatus.InProgress => "in-progress",
        TransactionStatus.Completed => "completed",
        TransactionStatus.RolledBack => "rolled-back",
        TransactionStatus.PartiallyRolledBack => "partially-rolled-back",
        _ => throw new InvalidDataException("The protected transaction state is invalid.")
    };

    private const int MaximumResults = 256;

    // A bundle profile stores one snapshot holding every registry value it will touch. Full Legacy needs
    // roughly 21 KB on a typical PC and grows with the number of network adapters and display class keys,
    // so a 16 KB ceiling rejected every profile except Safe. Still bounded, and far below MaximumJournalBytes.
    private const int MaximumValueLength = 64 * 1024;
    private const int MaximumMessageLength = 4096;

    private static byte[] WriteCanonicalTransaction(TransactionRecord transaction)
    {
        ValidateTransactionSemantics(transaction);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", transaction.Id.ToString("D"));
            writer.WriteString("startedAt", transaction.StartedAt.ToString("O"));
            writer.WriteNumber("status", (int)transaction.Status);
            writer.WritePropertyName("results"); writer.WriteStartArray();
            foreach (var result in transaction.Results)
            {
                writer.WriteStartObject();
                writer.WriteString("operationId", result.OperationId);
                if (result.OriginalValue is null) writer.WriteNull("originalValue"); else writer.WriteString("originalValue", result.OriginalValue);
                writer.WriteString("requestedValue", result.RequestedValue);
                writer.WriteNumber("status", (int)result.Status);
                writer.WriteBoolean("verified", result.Verified);
                writer.WriteString("message", result.Message);
                writer.WriteString("timestamp", result.Timestamp.ToString("O"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static TransactionRecord ParseTransaction(JsonElement element)
    {
        RequireObjectWithExactProperties(element, "id", "startedAt", "status", "results");
        var idText = RequiredBoundedString(element, "id", 36);
        if (!Guid.TryParseExact(idText, "D", out var id) || id == Guid.Empty ||
            !string.Equals(idText, id.ToString("D"), StringComparison.Ordinal))
            throw new InvalidDataException("The protected transaction ID is not canonical.");
        var started = ParseCanonicalTimestamp(element, "startedAt");
        var statusValue = element.GetProperty("status").GetInt32();
        if (!Enum.IsDefined(typeof(TransactionStatus), statusValue))
            throw new InvalidDataException("The protected transaction status is invalid.");
        var array = element.GetProperty("results");
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Protected results must be an array.");
        var results = new List<TweakResult>();
        foreach (var item in array.EnumerateArray())
        {
            RequireObjectWithExactProperties(item, "operationId", "originalValue", "requestedValue", "status", "verified", "message", "timestamp");
            var resultStatusValue = item.GetProperty("status").GetInt32();
            if (!Enum.IsDefined(typeof(TweakStatus), resultStatusValue))
                throw new InvalidDataException("A protected result status is invalid.");
            string? original = null;
            var originalElement = item.GetProperty("originalValue");
            if (originalElement.ValueKind == JsonValueKind.String)
            {
                original = originalElement.GetString();
                if (original is null || original.Length > MaximumValueLength || original.IndexOf('\0') >= 0)
                    throw new InvalidDataException("A protected original value is invalid.");
            }
            else if (originalElement.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("A protected original value must be text or null.");
            results.Add(new(
                RequiredBoundedString(item, "operationId", 128),
                original,
                RequiredBoundedString(item, "requestedValue", MaximumValueLength),
                (TweakStatus)resultStatusValue,
                item.GetProperty("verified").GetBoolean(),
                RequiredBoundedString(item, "message", MaximumMessageLength),
                ParseCanonicalTimestamp(item, "timestamp")));
            if (results.Count > MaximumResults) throw new InvalidDataException("The protected transaction has too many results.");
        }
        var transaction = new TransactionRecord(id, started, (TransactionStatus)statusValue, results);
        ValidateTransactionSemantics(transaction);
        return transaction;
    }

    private static DateTimeOffset ParseCanonicalTimestamp(JsonElement element, string name)
    {
        var text = RequiredBoundedString(element, name, 40);
        if (!DateTimeOffset.TryParseExact(text, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var value) ||
            !string.Equals(text, value.ToString("O"), StringComparison.Ordinal))
            throw new InvalidDataException("A protected timestamp is not canonical.");
        return value;
    }

    private static void ValidateTransactionSemantics(TransactionRecord transaction)
    {
        if (transaction.Id == Guid.Empty || !Enum.IsDefined(transaction.Status) ||
            transaction.Results is null || transaction.Results.Count > MaximumResults)
            throw new InvalidDataException("The protected transaction shape is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in transaction.Results)
        {
            if (!PrivilegedOperationRequest.IsCanonicalId(result.OperationId) ||
                !seen.Add(result.OperationId) ||
                result.RequestedValue is null || result.RequestedValue.Length > MaximumValueLength ||
                result.RequestedValue.IndexOf('\0') >= 0 ||
                result.OriginalValue?.Length > MaximumValueLength ||
                result.Message is null || result.Message.Length is < 1 or > MaximumMessageLength ||
                result.Message.IndexOf('\0') >= 0 || !Enum.IsDefined(result.Status) ||
                result.Timestamp < transaction.StartedAt)
                throw new InvalidDataException("A protected transaction result is invalid.");
            var mustVerify = result.Status is TweakStatus.Applied or TweakStatus.ReadOnlySucceeded or TweakStatus.Restored;
            if (result.Verified != mustVerify)
                throw new InvalidDataException("A protected result verification flag contradicts its status.");
        }
        if (transaction.Status == TransactionStatus.Completed &&
            transaction.Results.Any(x => x.Status == TweakStatus.Pending))

            throw new InvalidDataException("A completed protected transaction cannot contain pending work.");
    }


    private static void RequireObjectWithExactProperties(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A protected JSON object was expected.");

        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("Protected JSON contains an unknown or duplicate property.");
        }
        if (seen.Count != required.Length)
            throw new InvalidDataException("Protected JSON is missing required data.");
    }

    private static string RequiredBoundedString(JsonElement element, string name, int maximumLength)
    {
        if (element.GetProperty(name).ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Protected JSON property {name} must be text.");
        var value = element.GetProperty(name).GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength || value.IndexOf('\0') >= 0)
            throw new InvalidDataException($"Protected JSON property {name} is invalid.");
        return value;
    }

    private static readonly JsonSerializerOptions StrictSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false
    };

    private enum PlanState { Pending, Running, RollbackRunning, PartiallyRolledBack, Completed, RolledBack }
    private enum RecordKind { Journal, Result }
    private sealed record PlanEnvelope(int SchemaVersion, Guid TransactionId, DateTimeOffset CreatedUtc,
        string InitiatingIdentity, string ExecutableIdentity, string RecoveryIdentity, string CatalogIdentity,
        PlanState State, IReadOnlyList<PrivilegedOperationRequest> Operations, string Integrity);
    private sealed record RecordEnvelope(int SchemaVersion, Guid TransactionId, string InitiatingIdentity,
        string ExecutableIdentity, string RecoveryIdentity, RecordKind RecordKind, string State,
        string CatalogIdentity, string PlanIdentity, TransactionRecord Transaction, string Integrity);
}

public sealed class MachineDpapiKeyProtector : IProtectedPlanKeyProtector
{
    private const int LocalMachine = 0x4;

    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(byte[] protectedBytes) => Transform(protectedBytes, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI is required for the protected transaction key.");
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        Marshal.Copy(input, 0, inputPointer, input.Length);
        var inputBlob = new DataBlob(input.Length, inputPointer);
        try
        {
            var success = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, LocalMachine, out var output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class WindowsProtectedPlanAccessControl : IProtectedPlanAccessControl
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);

    public void ProtectDirectory(string path, string initiatingIdentity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(Administrators, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(System, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public void ProtectFile(string path, string initiatingIdentity, bool initiatingUserCanWrite)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new FileSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(System, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public void ValidateDirectory(string path, string initiatingIdentity)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateOwner(security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);
        if (!security.AreAccessRulesProtected)
            throw new InvalidDataException("The protected directory inherits access rules.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2 || rules.Any(rule => rule.IsInherited ||
            rule.AccessControlType != AccessControlType.Allow ||
            rule.FileSystemRights != FileSystemRights.FullControl ||
            rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) ||
            rule.PropagationFlags != PropagationFlags.None ||
            rule.IdentityReference is not SecurityIdentifier sid ||
            (sid != Administrators && sid != System)) ||
            rules.Select(x => ((SecurityIdentifier)x.IdentityReference).Value)
                .Distinct(StringComparer.Ordinal).Count() != 2)
            throw new InvalidDataException("The protected directory ACL is not the exact administrator/SYSTEM policy.");
    }

    public void ValidateFile(string path, string initiatingIdentity)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateOwner(security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);
        if (!security.AreAccessRulesProtected)
            throw new InvalidDataException("The protected file inherits access rules.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2 || rules.Any(rule => rule.IsInherited ||
            rule.AccessControlType != AccessControlType.Allow ||
            rule.FileSystemRights != FileSystemRights.FullControl ||
            rule.InheritanceFlags != InheritanceFlags.None ||
            rule.IdentityReference is not SecurityIdentifier sid ||
            (sid != Administrators && sid != System)) ||
            rules.Select(x => ((SecurityIdentifier)x.IdentityReference).Value)
                .Distinct(StringComparer.Ordinal).Count() != 2)
            throw new InvalidDataException("The protected file ACL is not the exact administrator/SYSTEM policy.");
    }

    private static void ValidateOwner(SecurityIdentifier? owner)
    {
        if (owner is null || (owner != Administrators && owner != System))
            throw new InvalidDataException("The protected object owner is not Administrators or SYSTEM.");
    }
}
