using System.Text.Json;

namespace Tweaker.Domain.Legacy;

public enum LegacyDisposition
{
    Direct,
    RepairedEquivalent,
    HardwareGated,
    NonExecutable
}

public enum LegacyReviewState
{
    Draft,
    Reviewed
}

public sealed record LegacyParityEntry(
    string Fingerprint,
    string CanonicalFingerprint,
    string SourceFile,
    int LineNumber,
    string Section,
    LegacyCommandKind Kind,
    string Intent,
    LegacyDisposition Disposition,
    LegacyReviewState ReviewState,
    string? OperationId,
    string Evidence,
    string SnapshotStrategy,
    string VerificationStrategy,
    string RollbackStrategy,
    IReadOnlyList<string> DuplicateReferences);

/// <summary>
/// A reviewed, data-only ledger of frozen legacy source mutations. It is deliberately
/// unable to construct processes, registry targets, paths, or other executable input.
/// </summary>
public sealed class LegacyParityManifest
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumManifestBytes = 8 * 1024 * 1024;
    public const int MaximumJsonDepth = 16;

    private static readonly HashSet<string> CompiledOperationIds = new(StringComparer.Ordinal)
    {
        // Task 2 deliberately has no compiled legacy operation contracts. Task 5 must
        // add fixed, compiled identifiers here only when their recovery contracts exist.
    };

    private LegacyParityManifest(int schemaVersion, int historicalSourceCount, int historicalNormalizedUniqueCount, IReadOnlyList<LegacyParityEntry> entries)
    {
        SchemaVersion = schemaVersion;
        HistoricalSourceCount = historicalSourceCount;
        HistoricalNormalizedUniqueCount = historicalNormalizedUniqueCount;
        Entries = entries;
    }

    public int SchemaVersion { get; }
    public int HistoricalSourceCount { get; }
    public int HistoricalNormalizedUniqueCount { get; }
    public IReadOnlyList<LegacyParityEntry> Entries { get; }

    public static LegacyParityManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Parity manifest was not found.", path);
        }

        if (file.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException($"Parity manifest exceeds the {MaximumManifestBytes}-byte limit.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.SequentialScan);
        return Load(stream);
    }

    public static LegacyParityManifest Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var bounded = new BoundedReadStream(stream, MaximumManifestBytes);
        using var document = JsonDocument.Parse(bounded, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth
        });

        EnsureObjectWithoutDuplicateProperties(document.RootElement, "root");
        var root = document.RootElement;
        RequireExactProperties(root, "root", "schemaVersion", "historicalBaseline", "entries");
        var schemaVersion = ReadPositiveInt(root, "schemaVersion");
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported parity manifest schema version: {schemaVersion}.");
        }

        var baseline = RequireObject(root, "historicalBaseline");
        RequireExactProperties(baseline, "historicalBaseline", "sourceCount", "normalizedUniqueCount");
        var historicalSourceCount = ReadPositiveInt(baseline, "sourceCount");
        var historicalNormalizedUniqueCount = ReadPositiveInt(baseline, "normalizedUniqueCount");
        if (historicalSourceCount != 1904 || historicalNormalizedUniqueCount != 1487)
        {
            throw new InvalidDataException("Historical baseline must preserve the audited 1904/1487 evidence.");
        }

        var entriesElement = RequireArray(root, "entries");
        var entries = new List<LegacyParityEntry>(entriesElement.GetArrayLength());
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in entriesElement.EnumerateArray())
        {
            var entry = ReadEntry(element);
            if (!fingerprints.Add(entry.Fingerprint))
            {
                throw new InvalidDataException($"Duplicate manifest fingerprint: {entry.Fingerprint}.");
            }

            entries.Add(entry);
        }

        ValidateReferenceGraph(entries, fingerprints);
        return new LegacyParityManifest(schemaVersion, historicalSourceCount, historicalNormalizedUniqueCount, entries);
    }

    public IReadOnlyList<string> ValidateAgainst(IReadOnlyList<LegacySourceLine> sourceLines)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        var errors = new List<string>();
        var sourceFingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sourceLines)
        {
            if (!sourceFingerprints.Add(source.Fingerprint))
            {
                errors.Add($"Frozen source has duplicate fingerprint: {source.Fingerprint}.");
            }
        }

        var manifestFingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (!manifestFingerprints.Add(entry.Fingerprint))
            {
                errors.Add($"Manifest has duplicate fingerprint: {entry.Fingerprint}.");
            }

            if (!sourceFingerprints.Contains(entry.Fingerprint))
            {
                errors.Add($"Manifest fingerprint is not present in frozen sources: {entry.Fingerprint}.");
            }

            var source = sourceLines.FirstOrDefault(line => line.Fingerprint.Equals(entry.Fingerprint, StringComparison.Ordinal));
            if (source is not null && (source.SourceFile != entry.SourceFile || source.LineNumber != entry.LineNumber || source.Kind != entry.Kind))
            {
                errors.Add($"Manifest source context does not match frozen source: {entry.Fingerprint}.");
            }

            ValidateDispositionContract(entry, errors);
        }

        foreach (var sourceFingerprint in sourceFingerprints)
        {
            if (!manifestFingerprints.Contains(sourceFingerprint))
            {
                errors.Add($"Frozen source fingerprint is missing from manifest: {sourceFingerprint}.");
            }
        }

        errors.AddRange(LegacyParityCompleteness.Validate(Entries, sourceLines));
        return errors;
    }

    private static LegacyParityEntry ReadEntry(JsonElement element)
    {
        EnsureObjectWithoutDuplicateProperties(element, "entry");
        RequireExactProperties(element, "entry", "fingerprint", "canonicalFingerprint", "sourceFile", "lineNumber", "section", "kind", "intent", "disposition", "reviewState", "operationId", "evidence", "snapshotStrategy", "verificationStrategy", "rollbackStrategy", "duplicateReferences");
        var operationIdElement = RequireProperty(element, "operationId");
        var operationId = operationIdElement.ValueKind == JsonValueKind.Null ? null : ReadNonBlankString(operationIdElement, "operationId");
        var kindText = ReadNonBlankString(element, "kind");
        var dispositionText = ReadNonBlankString(element, "disposition");
        if (!Enum.TryParse<LegacyCommandKind>(kindText, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Invalid legacy command kind: {kindText}.");
        }

        if (!Enum.TryParse<LegacyDisposition>(dispositionText, ignoreCase: false, out var disposition) || !Enum.IsDefined(disposition))
        {
            throw new InvalidDataException($"Invalid legacy disposition: {dispositionText}.");
        }

        var reviewStateText = ReadNonBlankString(element, "reviewState");
        if (!Enum.TryParse<LegacyReviewState>(reviewStateText, ignoreCase: false, out var reviewState) || !Enum.IsDefined(reviewState))
        {
            throw new InvalidDataException($"Invalid legacy review state: {reviewStateText}.");
        }

        var references = RequireArray(element, "duplicateReferences").EnumerateArray()
            .Select(item => ReadNonBlankString(item, "duplicateReferences item"))
            .ToArray();
        if (references.Distinct(StringComparer.Ordinal).Count() != references.Length)
        {
            throw new InvalidDataException("Duplicate references must be unique.");
        }

        var entry = new LegacyParityEntry(
            ReadFingerprint(element, "fingerprint"),
            ReadFingerprint(element, "canonicalFingerprint"),
            ReadNonBlankString(element, "sourceFile"),
            ReadPositiveInt(element, "lineNumber"),
            ReadNonBlankString(element, "section"),
            kind,
            ReadNonBlankString(element, "intent"),
            disposition,
            reviewState,
            operationId,
            ReadNonBlankString(element, "evidence"),
            ReadNonBlankString(element, "snapshotStrategy"),
            ReadNonBlankString(element, "verificationStrategy"),
            ReadNonBlankString(element, "rollbackStrategy"),
            references);

        var errors = new List<string>();
        ValidateDispositionContract(entry, errors);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(errors[0]);
        }

        return entry;
    }

    private static void ValidateReferenceGraph(IReadOnlyList<LegacyParityEntry> entries, ISet<string> fingerprints)
    {
        foreach (var entry in entries)
        {
            if (!fingerprints.Contains(entry.CanonicalFingerprint))
            {
                throw new InvalidDataException($"Canonical fingerprint is not present in the manifest: {entry.CanonicalFingerprint}.");
            }

            if (entry.DuplicateReferences.Contains(entry.Fingerprint, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Entry cannot list itself as a duplicate reference: {entry.Fingerprint}.");
            }

            if (entry.DuplicateReferences.Any(reference => !fingerprints.Contains(reference)))
            {
                throw new InvalidDataException($"Duplicate reference is not present in the manifest for {entry.Fingerprint}.");
            }
        }
    }

    private static void ValidateDispositionContract(LegacyParityEntry entry, ICollection<string> errors)
    {
        if (entry.ReviewState == LegacyReviewState.Draft)
        {
            if (entry.Disposition != LegacyDisposition.NonExecutable || entry.OperationId is not null)
            {
                errors.Add($"Draft entry must remain a non-executable placeholder without an operation identifier: {entry.Fingerprint}.");
            }

            if (entry.Evidence.Length < 40 || IsGenericEvidence(entry.Evidence))
            {
                errors.Add($"Draft entry needs sanitized source/effect scope and an explicit unreviewed-contract statement: {entry.Fingerprint}.");
            }

            return;
        }

        if (entry.ReviewState != LegacyReviewState.Reviewed)
        {
            errors.Add($"Entry has an unsupported review state: {entry.Fingerprint}.");
            return;
        }

        if (entry.Disposition == LegacyDisposition.NonExecutable)
        {
            if (entry.OperationId is not null || entry.Evidence.Length < 40 || IsGenericEvidence(entry.Evidence))
            {
                errors.Add($"Reviewed non-executable entry needs specific technical evidence and no operation identifier: {entry.Fingerprint}.");
            }

            return;
        }

        if (entry.OperationId is null || !CompiledOperationIds.Contains(entry.OperationId))
        {
            errors.Add($"Executable entry has no recognized compiled operation identifier: {entry.Fingerprint}.");
        }

        if (string.IsNullOrWhiteSpace(entry.SnapshotStrategy) || string.IsNullOrWhiteSpace(entry.VerificationStrategy) || string.IsNullOrWhiteSpace(entry.RollbackStrategy))
        {
            errors.Add($"Executable entry lacks a complete recovery contract: {entry.Fingerprint}.");
        }
    }

    private static bool IsGenericEvidence(string evidence) => evidence.Trim().ToLowerInvariant() is "unsafe" or "unsupported" or "not supported" or "unknown";

    private static void EnsureObjectWithoutDuplicateProperties(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} must be an object.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException($"Duplicate JSON property in {context}: {property.Name}.");
            }
        }
    }

    private static void RequireExactProperties(JsonElement element, string context, params string[] names)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(names))
        {
            throw new InvalidDataException($"{context} has missing or unrecognized properties.");
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value : throw new InvalidDataException($"Missing required property: {propertyName}.");

    private static JsonElement RequireObject(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        EnsureObjectWithoutDuplicateProperties(value, propertyName);
        return value;
    }

    private static JsonElement RequireArray(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Array ? value : throw new InvalidDataException($"{propertyName} must be an array.");
    }

    private static string ReadNonBlankString(JsonElement element, string propertyName)
    {
        var value = element.ValueKind == JsonValueKind.Object ? RequireProperty(element, propertyName) : element;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{propertyName} must be a non-blank string.");
        }

        return value.GetString()!;
    }

    private static string ReadFingerprint(JsonElement element, string propertyName)
    {
        var value = ReadNonBlankString(element, propertyName);
        if (value.Length != 64 || value.Any(character => !((character is >= '0' and <= '9') || (character is >= 'A' and <= 'F'))))
        {
            throw new InvalidDataException($"{propertyName} must be an uppercase SHA-256 fingerprint.");
        }

        return value;
    }

    private static int ReadPositiveInt(JsonElement element, string propertyName)
    {
        var value = RequireProperty(element, propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result <= 0)
        {
            throw new InvalidDataException($"{propertyName} must be a positive integer.");
        }

        return result;
    }

    private sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Check(inner.Read(buffer));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ReadAsyncCore(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken) => Check(await inner.ReadAsync(buffer, cancellationToken));
        private int Check(int bytes)
        {
            _read += bytes;
            if (_read > maximumBytes)
            {
                throw new InvalidDataException($"Parity manifest exceeds the {maximumBytes}-byte limit.");
            }

            return bytes;
        }
    }
}
