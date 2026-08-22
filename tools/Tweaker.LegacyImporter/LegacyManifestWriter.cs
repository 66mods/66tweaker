using System.Text;
using System.Text.Json;
using Tweaker.Domain.Legacy;

namespace Tweaker.LegacyImporter;

internal static class LegacyManifestWriter
{
    private static readonly string[] FixturePaths =
    [
        "66mods Tweaks v40012(RUN AS ADMIN).bat",
        "Fixes/Fix Disabled WiFi (RUN AS ADMIN).bat",
        "Fixes/Fix Fortnite Not Starting (RUN AS ADMIN).bat"
    ];

    public static void WriteDraft(string sourceRoot, string outputPath)
    {
        var root = Path.GetFullPath(sourceRoot);
        var canonicalSourceRoot = Program.ValidateExistingPathWithoutReparsePoints(root);
        var permittedOutputRoot = Path.GetFullPath(Path.Combine(root, ".."));
        var canonicalOutputRoot = Program.ValidateExistingPathWithoutReparsePoints(permittedOutputRoot);
        var output = Path.GetFullPath(outputPath);
        Program.EnsureContained(permittedOutputRoot, output, "Output path");
        var outputDirectory = Path.GetDirectoryName(output) ?? throw new ArgumentException("Output path must have a directory.");
        var canonicalOutputDirectory = Program.ValidateExistingPathWithoutReparsePoints(outputDirectory);
        Program.EnsureContained(canonicalOutputRoot, canonicalOutputDirectory, "Output path");

        var parser = new LegacyBatParser();
        var lines = FixturePaths.SelectMany(relativePath =>
        {
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Program.EnsureContained(root, path, "Fixture path");
            return parser.Parse(relativePath, Program.ReadBoundedLatin1Text(path, canonicalSourceRoot));
        }).ToArray();
        var groups = lines.GroupBy(line => line.NormalizedText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var entries = lines.Select(line => CreateDraftEntry(line, groups[line.NormalizedText])).ToArray();
        var json = JsonSerializer.Serialize(new ManifestDocument(LegacyParityManifest.CurrentSchemaVersion,
            new HistoricalBaseline(1904, 1487), entries),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        var bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);
        if (bytes.Length > LegacyParityManifest.MaximumManifestBytes)
        {
            throw new InvalidDataException("Generated draft exceeds the parity loader size limit.");
        }

        Program.WriteAtomically(output, bytes, canonicalOutputRoot);
    }

    private static ManifestEntry CreateDraftEntry(LegacySourceLine line, IReadOnlyList<LegacySourceLine> group)
    {
        var section = string.IsNullOrWhiteSpace(line.Section) ? "unsectioned" : line.Section;
        var effect = DescribeSanitizedEffect(line.Kind);
        return new ManifestEntry(
            line.Fingerprint,
            group[0].Fingerprint,
            line.SourceFile,
            line.LineNumber,
            section,
            line.Kind.ToString(),
            $"Draft {line.Kind} effect in section '{section}': {effect}",
            LegacyDisposition.NonExecutable.ToString(),
            LegacyReviewState.Draft.ToString(),
            null,
            $"Draft evidence for {line.Kind} at {line.SourceFile}:{line.LineNumber}: {effect} No reviewed compiled operation, mutation-specific Windows rationale, or recovery contract has been approved yet.",
            "Draft placeholder: an approved typed operation must snapshot the exact Windows pre-state before execution.",
            "Draft placeholder: an approved typed operation must perform fresh Windows read-back verification.",
            "Draft placeholder: an approved typed operation must journal and exactly restore the prior state.",
            group.Where(other => other.Fingerprint != line.Fingerprint).Select(other => other.Fingerprint).ToArray());
    }

    private static string DescribeSanitizedEffect(LegacyCommandKind kind) => kind switch
    {
        LegacyCommandKind.RegistryAdd => "adds or overwrites a Windows registry value",
        LegacyCommandKind.RegistryDelete => "deletes a Windows registry value or key",
        LegacyCommandKind.PowerCfg => "changes an active Windows power-scheme setting",
        LegacyCommandKind.BcdEdit => "changes a Windows boot configuration setting",
        LegacyCommandKind.ScheduledTask => "changes a Windows scheduled-task state or definition",
        LegacyCommandKind.ServiceControl => "changes a Windows service state or startup configuration",
        LegacyCommandKind.Netsh => "changes a Windows networking-stack setting",
        LegacyCommandKind.PowerShellMutation => "changes a Windows component through a PowerShell mutation API",
        LegacyCommandKind.FileDeletion => "deletes Windows file-system content",
        _ => throw new InvalidDataException("Unsupported legacy command kind.")
    };

    private sealed record HistoricalBaseline(int SourceCount, int NormalizedUniqueCount);
    private sealed record ManifestEntry(string Fingerprint, string CanonicalFingerprint, string SourceFile, int LineNumber, string Section, string Kind, string Intent, string Disposition, string ReviewState, string? OperationId, string Evidence, string SnapshotStrategy, string VerificationStrategy, string RollbackStrategy, IReadOnlyList<string> DuplicateReferences);
    private sealed record ManifestDocument(int SchemaVersion, HistoricalBaseline HistoricalBaseline, IReadOnlyList<ManifestEntry> Entries);
}
