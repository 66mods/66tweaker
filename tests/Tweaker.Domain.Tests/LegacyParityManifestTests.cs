using System.Text;
using System.Text.Json;
using FluentAssertions;
using Tweaker.Domain.Legacy;
using Tweaker.LegacyImporter;

namespace Tweaker.Domain.Tests;

public sealed class LegacyParityManifestTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Manifest_AccountsForEveryFrozenMutationExactlyOnce()
    {
        var sourceLines = ParseFrozenSources();
        var manifest = LegacyParityManifest.Load(Path.Combine(RepositoryRoot, "legacy", "parity-manifest.json"));

        sourceLines.Should().HaveCount(1917);
        sourceLines.Select(line => line.NormalizedText).Distinct(StringComparer.Ordinal).Should().HaveCount(1500);
        manifest.ValidateAgainst(sourceLines).Should().BeEmpty();
    }

    [Fact]
    public void Manifest_PreservesHistoricalEvidenceAndKeepsAllEntriesNonExecutable()
    {
        var manifest = LegacyParityManifest.Load(Path.Combine(RepositoryRoot, "legacy", "parity-manifest.json"));

        manifest.HistoricalSourceCount.Should().Be(1904);
        manifest.HistoricalNormalizedUniqueCount.Should().Be(1487);
        manifest.Entries.Should().OnlyContain(entry =>
            entry.ReviewState == LegacyReviewState.Draft &&
            entry.Disposition == LegacyDisposition.NonExecutable &&
            entry.OperationId == null &&
            entry.Evidence.Length >= 40 &&
            !entry.Evidence.Equals("unsafe", StringComparison.OrdinalIgnoreCase));
        manifest.Entries.Should().NotContain(entry => entry.Disposition == LegacyDisposition.Direct);
    }

    [Fact]
    public void Loader_RejectsDuplicatePropertiesTrailingCommasAndUnrecognizedOperationIds()
    {
        const string duplicateProperty = "{\"schemaVersion\":1,\"schemaVersion\":1,\"historicalBaseline\":{\"sourceCount\":1904,\"normalizedUniqueCount\":1487},\"entries\":[]}";
        const string trailingComma = "{\"schemaVersion\":1,\"historicalBaseline\":{\"sourceCount\":1904,\"normalizedUniqueCount\":1487},\"entries\":[],}";
        var unrecognizedOperation = ValidEntryJson("\"operationId\":\"raw-registry-command\"");

        Action duplicate = () => LoadJson(duplicateProperty);
        Action trailing = () => LoadJson(trailingComma);
        Action operation = () => LoadJson(unrecognizedOperation);

        duplicate.Should().Throw<InvalidDataException>();
        trailing.Should().Throw<JsonException>();
        operation.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ValidateAgainst_RejectsWrongExistingCanonicalAndDuplicateReferences()
    {
        var sourceLines = ParseFrozenSources();
        var manifestPath = Path.Combine(RepositoryRoot, "legacy", "parity-manifest.json");
        var manifest = LegacyParityManifest.Load(manifestPath);
        var entry = manifest.Entries.First(candidate => candidate.DuplicateReferences.Count > 0);
        var foreign = manifest.Entries.First(candidate =>
            candidate.CanonicalFingerprint != entry.CanonicalFingerprint &&
            candidate.Fingerprint != entry.Fingerprint &&
            !entry.DuplicateReferences.Contains(candidate.Fingerprint, StringComparer.Ordinal)).Fingerprint;
        var json = File.ReadAllText(manifestPath, Encoding.UTF8);

        var wrongCanonical = ReplaceFirst(json, $"\"canonicalFingerprint\": \"{entry.CanonicalFingerprint}\"", $"\"canonicalFingerprint\": \"{foreign}\"");
        var duplicatePosition = json.IndexOf($"\"fingerprint\": \"{entry.Fingerprint}\"", StringComparison.Ordinal);
        var referencePosition = json.IndexOf($"\"{entry.DuplicateReferences[0]}\"", duplicatePosition, StringComparison.Ordinal);
        var wrongDuplicate = json[..referencePosition] + $"\"{foreign}\"" + json[(referencePosition + 66)..];

        LegacyParityManifest.Load(new MemoryStream(Encoding.UTF8.GetBytes(wrongCanonical))).ValidateAgainst(sourceLines).Should().NotBeEmpty();
        LegacyParityManifest.Load(new MemoryStream(Encoding.UTF8.GetBytes(wrongDuplicate))).ValidateAgainst(sourceLines).Should().NotBeEmpty();
    }

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? throw new InvalidOperationException("Expected JSON value was not found.") : value[..index] + newValue + value[(index + oldValue.Length)..];
    }
    private static LegacyParityManifest LoadJson(string json) =>
        LegacyParityManifest.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static string ValidEntryJson(string operationId) =>
        "{\"schemaVersion\":1,\"historicalBaseline\":{\"sourceCount\":1904,\"normalizedUniqueCount\":1487},\"entries\":[{" +
        "\"fingerprint\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"canonicalFingerprint\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"sourceFile\":\"fixture.bat\",\"lineNumber\":1,\"section\":\"fixture\",\"kind\":\"RegistryAdd\",\"intent\":\"Draft fixture mutation.\",\"disposition\":\"NonExecutable\",\"reviewState\":\"Draft\"," +
        operationId + ",\"evidence\":\"This specific fixture registry mutation has no typed Windows recovery contract.\",\"snapshotStrategy\":\"Not applicable because no compiled operation exists.\",\"verificationStrategy\":\"Not applicable because no fixed target exists.\",\"rollbackStrategy\":\"Not applicable because no journaled rollback exists.\",\"duplicateReferences\":[]}]}";

    private static IReadOnlyList<LegacySourceLine> ParseFrozenSources()
    {
        var parser = new LegacyBatParser();
        return new[]
        {
            "66mods Tweaks v40012(RUN AS ADMIN).bat",
            "Fixes/Fix Disabled WiFi (RUN AS ADMIN).bat",
            "Fixes/Fix Fortnite Not Starting (RUN AS ADMIN).bat"
        }.SelectMany(relativePath => parser.Parse(relativePath, ReadFixture(relativePath))).ToArray();
    }

    private static string ReadFixture(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot, "legacy", "source", relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllText(path, Encoding.Latin1);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "66mods.Tweaker.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
