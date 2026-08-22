using System.Text.Json;

namespace Tweaker.App.Services;

public enum CompositeTransactionStatus
{
    PrivilegedPending, LocalPending, Completed, LocalRollbackPending, NeedsLocalRecovery,
    PrivilegedRollbackPending, RolledBack, NeedsProtectedRecovery, LocalNotStarted
}

public sealed record CompositeTransactionRecord(Guid Id, DateTimeOffset StartedUtc,
    CompositeTransactionStatus Status, Guid? PrivilegedTransactionId, Guid? LocalTransactionId,
    string Message, long Revision = 0);

public interface ICompositeTransactionStore
{
    Task CreateAsync(CompositeTransactionRecord record, CancellationToken cancellationToken);
    Task<CompositeTransactionRecord> TransitionAsync(CompositeTransactionRecord expected,
        CompositeTransactionRecord next, CancellationToken cancellationToken);
    Task<CompositeTransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CompositeTransactionRecord>> ListIncompleteAsync(int limit, CancellationToken cancellationToken);
}

public sealed class InMemoryCompositeTransactionStore : ICompositeTransactionStore
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, CompositeTransactionRecord> records = [];
    public IReadOnlyCollection<CompositeTransactionRecord> Records
    {
        get { lock (sync) return records.Values.ToArray(); }
    }
    public Task CreateAsync(CompositeTransactionRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompositeRecordCodec.Validate(record);
        if (record.Revision != 0) throw new InvalidDataException("A new composite must start at revision zero.");
        lock (sync) if (!records.TryAdd(record.Id, record))
            throw new InvalidOperationException("The composite transaction already exists.");
        return Task.CompletedTask;
    }
    public Task<CompositeTransactionRecord> TransitionAsync(CompositeTransactionRecord expected,
        CompositeTransactionRecord next, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompositeRecordCodec.ValidateTransition(expected, next);
        lock (sync)
        {
            if (!records.TryGetValue(expected.Id, out var current) || current != expected)
                throw new InvalidOperationException("The composite changed before this expected-state transition.");
            records[expected.Id] = next;
        }
        return Task.FromResult(next);
    }
    public Task<CompositeTransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty) throw new ArgumentException("A composite transaction ID is required.", nameof(id));
        lock (sync) return Task.FromResult(records.GetValueOrDefault(id));
    }
    public Task<IReadOnlyList<CompositeTransactionRecord>> ListIncompleteAsync(int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (sync) return Task.FromResult<IReadOnlyList<CompositeTransactionRecord>>(records.Values
            .Where(x => x.Status is not (CompositeTransactionStatus.Completed or CompositeTransactionStatus.RolledBack))
            .OrderByDescending(x => x.StartedUtc).Take(limit).ToArray());
    }
}

public sealed class JsonCompositeTransactionStore : ICompositeTransactionStore
{
    private const int MaximumBytes = 32 * 1024;
    private const int MaximumFiles = 1000;
    private readonly string root;
    public JsonCompositeTransactionStore(string rootPath) =>
        root = Path.GetFullPath(rootPath ?? throw new ArgumentNullException(nameof(rootPath)));

    public async Task CreateAsync(CompositeTransactionRecord record, CancellationToken cancellationToken)
    {
        CompositeRecordCodec.Validate(record);
        if (record.Revision != 0) throw new InvalidDataException("A new composite must start at revision zero.");
        EnsureRoot();
        await WriteAtomicAsync(PathFor(record.Id), CompositeRecordCodec.Write(record), false, cancellationToken);
    }
    public async Task<CompositeTransactionRecord> TransitionAsync(CompositeTransactionRecord expected,
        CompositeTransactionRecord next, CancellationToken cancellationToken)
    {
        CompositeRecordCodec.ValidateTransition(expected, next);
        EnsureRoot();
        await using var claim = await AcquireClaimAsync(expected.Id, cancellationToken);
        var path = PathFor(expected.Id);
        if (!File.Exists(path)) throw new InvalidOperationException("The composite transaction does not exist.");
        var current = CompositeRecordCodec.Read(await ReadBoundedAsync(path, cancellationToken), expected.Id);
        if (current != expected)
            throw new InvalidOperationException("The composite changed before this expected-state transition.");
        await WriteAtomicAsync(path, CompositeRecordCodec.Write(next), true, cancellationToken);
        return next;
    }
    public async Task<CompositeTransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) throw new ArgumentException("A composite transaction ID is required.", nameof(id));
        EnsureRoot();
        await using var claim = await AcquireClaimAsync(id, cancellationToken);
        var path = PathFor(id);
        return !File.Exists(path) ? null : CompositeRecordCodec.Read(await ReadBoundedAsync(path, cancellationToken), id);
    }
    public async Task<IReadOnlyList<CompositeTransactionRecord>> ListIncompleteAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        EnsureRoot();
        var paths = Directory.EnumerateFiles(root, "*.composite.json").Take(MaximumFiles + 1).ToArray();
        if (paths.Length > MaximumFiles) throw new InvalidDataException("Too many composite records exist.");
        var result = new List<CompositeTransactionRecord>();
        foreach (var path in paths.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.Length != 32 + ".composite.json".Length || !Guid.TryParseExact(name[..32], "N", out var id) || id == Guid.Empty)
                throw new InvalidDataException("A composite filename is invalid.");
            var record = await LoadAsync(id, cancellationToken)
                ?? throw new InvalidDataException("A listed composite disappeared unexpectedly.");
            if (record.Status is not (CompositeTransactionStatus.Completed or CompositeTransactionStatus.RolledBack)) result.Add(record);
        }
        return result.OrderByDescending(x => x.StartedUtc).Take(limit).ToArray();
    }

    private async Task<FileStream> AcquireClaimAsync(Guid id, CancellationToken cancellationToken)
    {
        var path = LockPathFor(id);
        try
        {
            await using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await created.WriteAsync(new byte[] { 0x43 }, cancellationToken);
            await created.FlushAsync(cancellationToken);
            created.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path)) { }
        RejectReparse(path);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough); }
            catch (IOException error) when (IsSharingViolation(error))
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }
    private static bool IsSharingViolation(IOException error) =>
        (error.HResult & 0xffff) is 32 or 33;

    private async Task WriteAtomicAsync(string destination, byte[] bytes, bool overwrite, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(root, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        EnsureContained(temporary);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            RejectReparse(temporary);
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void EnsureRoot() { Directory.CreateDirectory(root); RejectReparse(root); }
    private string PathFor(Guid id)
    {
        var path = Path.GetFullPath(Path.Combine(root, id.ToString("N") + ".composite.json"));
        EnsureContained(path); return path;
    }
    private string LockPathFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A composite transaction ID is required.", nameof(id));
        var path = Path.GetFullPath(Path.Combine(root, id.ToString("N") + ".composite.lock"));
        EnsureContained(path); return path;
    }
    private void EnsureContained(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The composite path escaped its root.");
    }
    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Reparse points are not permitted in composite paths.");
    }
    private async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        EnsureContained(path); RejectReparse(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 2 or > MaximumBytes) throw new InvalidDataException("The composite record size is invalid.");
        var bytes = new byte[stream.Length]; await stream.ReadExactlyAsync(bytes, cancellationToken); return bytes;
    }
}

internal static class CompositeRecordCodec
{
    public static void Validate(CompositeTransactionRecord record)
    {
        if (record.Id == Guid.Empty || record.StartedUtc == default || !Enum.IsDefined(record.Status) || record.Revision < 0 ||
            record.Message is null or { Length: < 1 or > 4096 } || record.Message.IndexOf('\0') >= 0 ||
            record.PrivilegedTransactionId == Guid.Empty || record.LocalTransactionId == Guid.Empty)
            throw new InvalidDataException("The composite record is invalid.");
        if (record.Status is (CompositeTransactionStatus.LocalNotStarted or CompositeTransactionStatus.LocalPending or
            CompositeTransactionStatus.LocalRollbackPending or CompositeTransactionStatus.NeedsLocalRecovery) && record.LocalTransactionId is null)
            throw new InvalidDataException("Local state requires a local transaction ID.");
        if (record.Status is (CompositeTransactionStatus.PrivilegedPending or CompositeTransactionStatus.PrivilegedRollbackPending or
            CompositeTransactionStatus.NeedsProtectedRecovery) && record.PrivilegedTransactionId is null)
            throw new InvalidDataException("Protected state requires a protected transaction ID.");
        if (record.Status is (CompositeTransactionStatus.Completed or CompositeTransactionStatus.RolledBack) &&
            record.PrivilegedTransactionId is null && record.LocalTransactionId is null)
            throw new InvalidDataException("A terminal composite must identify a phase.");
    }
    public static void ValidateTransition(CompositeTransactionRecord expected, CompositeTransactionRecord next)
    {
        Validate(expected); Validate(next);
        if (next.Id != expected.Id || next.StartedUtc != expected.StartedUtc ||
            next.PrivilegedTransactionId != expected.PrivilegedTransactionId || next.LocalTransactionId != expected.LocalTransactionId ||
            next.Revision != expected.Revision + 1)
            throw new InvalidDataException("A composite transition changed immutable data or revision order.");
        if (!Allowed(expected.Status, next.Status))
            throw new InvalidOperationException($"Composite transition {expected.Status} -> {next.Status} is not allowed.");
    }
    private static bool Allowed(CompositeTransactionStatus from, CompositeTransactionStatus to) => (from, to) switch
    {
        (CompositeTransactionStatus.PrivilegedPending, CompositeTransactionStatus.LocalNotStarted) => true,
        (CompositeTransactionStatus.PrivilegedPending, CompositeTransactionStatus.Completed) => true,
        (CompositeTransactionStatus.PrivilegedPending, CompositeTransactionStatus.PrivilegedRollbackPending) => true,
        (CompositeTransactionStatus.LocalNotStarted, CompositeTransactionStatus.LocalPending) => true,
        (CompositeTransactionStatus.LocalNotStarted, CompositeTransactionStatus.PrivilegedRollbackPending) => true,
        (CompositeTransactionStatus.LocalNotStarted, CompositeTransactionStatus.NeedsLocalRecovery) => true,
        (CompositeTransactionStatus.LocalNotStarted, CompositeTransactionStatus.RolledBack) => true,
        (CompositeTransactionStatus.LocalPending, CompositeTransactionStatus.Completed) => true,
        (CompositeTransactionStatus.LocalPending, CompositeTransactionStatus.LocalRollbackPending) => true,
        (CompositeTransactionStatus.Completed, CompositeTransactionStatus.LocalRollbackPending) => true,
        (CompositeTransactionStatus.Completed, CompositeTransactionStatus.PrivilegedRollbackPending) => true,
        (CompositeTransactionStatus.LocalRollbackPending, CompositeTransactionStatus.NeedsLocalRecovery) => true,
        (CompositeTransactionStatus.LocalRollbackPending, CompositeTransactionStatus.PrivilegedRollbackPending) => true,
        (CompositeTransactionStatus.LocalRollbackPending, CompositeTransactionStatus.RolledBack) => true,
        (CompositeTransactionStatus.NeedsLocalRecovery, CompositeTransactionStatus.LocalRollbackPending) => true,
        (CompositeTransactionStatus.PrivilegedRollbackPending, CompositeTransactionStatus.NeedsProtectedRecovery) => true,
        (CompositeTransactionStatus.PrivilegedRollbackPending, CompositeTransactionStatus.RolledBack) => true,
        (CompositeTransactionStatus.NeedsProtectedRecovery, CompositeTransactionStatus.PrivilegedRollbackPending) => true,
        _ => false
    };

    public static byte[] Write(CompositeTransactionRecord record)
    {
        Validate(record);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", record.Id.ToString("D"));
            writer.WriteString("startedUtc", record.StartedUtc.ToString("O"));
            writer.WriteNumber("status", (int)record.Status);
            writer.WriteNumber("revision", record.Revision);
            if (record.PrivilegedTransactionId is { } privileged) writer.WriteString("privilegedTransactionId", privileged.ToString("D"));
            else writer.WriteNull("privilegedTransactionId");
            if (record.LocalTransactionId is { } local) writer.WriteString("localTransactionId", local.ToString("D"));
            else writer.WriteNull("localTransactionId");
            writer.WriteString("message", record.Message);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    public static CompositeTransactionRecord Read(byte[] bytes, Guid expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 6 });
            var value = document.RootElement;
            Exact(value, "id", "startedUtc", "status", "revision", "privilegedTransactionId", "localTransactionId", "message");
            var id = CanonicalGuid(value.GetProperty("id"), false)!.Value;
            if (id != expectedId) throw new InvalidDataException("The composite was substituted across IDs.");
            var startedText = RequiredString(value, "startedUtc", 40);
            if (!DateTimeOffset.TryParseExact(startedText, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var started) || startedText != started.ToString("O"))
                throw new InvalidDataException("The composite timestamp is not canonical.");
            var statusValue = value.GetProperty("status").GetInt32();
            if (!Enum.IsDefined(typeof(CompositeTransactionStatus), statusValue)) throw new InvalidDataException("Invalid composite status.");
            var record = new CompositeTransactionRecord(id, started, (CompositeTransactionStatus)statusValue,
                CanonicalGuid(value.GetProperty("privilegedTransactionId"), true),
                CanonicalGuid(value.GetProperty("localTransactionId"), true), RequiredString(value, "message", 4096),
                value.GetProperty("revision").GetInt64());
            Validate(record); return record;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("The composite transaction JSON is invalid.", error); }
    }
    private static void Exact(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A composite object was expected.");
        var allowed = new HashSet<string>(names, StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new InvalidDataException("Unknown or duplicate composite property.");
        if (seen.Count != names.Length) throw new InvalidDataException("Composite data is missing.");
    }
    private static string RequiredString(JsonElement value, string name, int maximum)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException("A composite string is invalid.");
        var text = property.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > maximum || text.IndexOf('\0') >= 0) throw new InvalidDataException("A composite string is invalid.");
        return text;
    }
    private static Guid? CanonicalGuid(JsonElement value, bool allowNull)
    {
        if (allowNull && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("A composite GUID is invalid.");
        var text = value.GetString();
        if (text is null || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw new InvalidDataException("A composite GUID is not canonical.");
        return id;
    }
}
