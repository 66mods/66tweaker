using System.Text.Json;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Storage;

/// <summary>
/// Compatibility/history store for non-privileged transactions. Its user-writable contents are
/// deliberately not an IPrivilegedTransactionStore and therefore cannot authorize elevated restore.
/// </summary>
public sealed class JsonTransactionStore : ITransactionStore, ITransactionHistoryStore
{
    private const int MaximumJournalBytes = 1024 * 1024;
    private readonly string rootPath;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false
    };

    public JsonTransactionStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("A transaction root is required.", nameof(rootPath));
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public Task BeginAsync(TransactionRecord record, CancellationToken cancellationToken) => SaveAsync(record, cancellationToken);

    public async Task SaveAsync(TransactionRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rootPath);
        RejectReparse(rootPath);
        var destination = PathFor(record.Id);
        var temporary = Path.Combine(rootPath, $".{record.Id:N}.{Guid.NewGuid():N}.tmp");
        EnsureContained(temporary);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumJournalBytes)
                    throw new InvalidDataException("The compatibility transaction journal is too large.");
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        return await ReadAsync(path, cancellationToken);
    }

    public async Task<IReadOnlyList<TransactionRecord>> LoadRecentAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0 || !Directory.Exists(rootPath)) return [];
        RejectReparse(rootPath);
        var records = new List<TransactionRecord>();
        foreach (var path in Directory.EnumerateFiles(rootPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureContained(path);
                var record = await ReadAsync(path, cancellationToken);
                if (record is not null && string.Equals(Path.GetFileName(path), $"{record.Id:N}.json", StringComparison.Ordinal))
                    records.Add(record);
            }
            catch (JsonException) { }
            catch (InvalidDataException) { }
            catch (IOException) { }
        }
        return records.OrderByDescending(x => x.StartedAt).Take(limit).ToArray();
    }

    public async Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken)
    {
        var records = await LoadRecentAsync(int.MaxValue, cancellationToken);
        return records.FirstOrDefault(record =>
            record.Status is TransactionStatus.InProgress or TransactionStatus.PartiallyRolledBack);
    }

    private async Task<TransactionRecord?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        RejectReparse(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 or > MaximumJournalBytes)
            throw new InvalidDataException("The compatibility transaction journal size is invalid.");
        return await JsonSerializer.DeserializeAsync<TransactionRecord>(stream, JsonOptions, cancellationToken);
    }

    private string PathFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A transaction ID is required.", nameof(id));
        var path = Path.GetFullPath(Path.Combine(rootPath, $"{id:N}.json"));
        EnsureContained(path);
        return path;
    }

    private void EnsureContained(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The compatibility transaction path escaped its root.");
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Reparse points are not accepted for transaction journals.");
    }
}
