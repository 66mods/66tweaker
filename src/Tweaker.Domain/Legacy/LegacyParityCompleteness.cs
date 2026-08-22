namespace Tweaker.Domain.Legacy;

internal static class LegacyParityCompleteness
{
    private static readonly IReadOnlyDictionary<string, (int SourceCount, int NormalizedCount)> AuditedSources =
        new Dictionary<string, (int SourceCount, int NormalizedCount)>(StringComparer.Ordinal)
        {
            ["66mods Tweaks v40012(RUN AS ADMIN).bat"] = (1908, 1491),
            ["Fixes/Fix Disabled WiFi (RUN AS ADMIN).bat"] = (6, 6),
            ["Fixes/Fix Fortnite Not Starting (RUN AS ADMIN).bat"] = (3, 3)
        };

    public static IReadOnlyList<string> Validate(IReadOnlyList<LegacyParityEntry> entries, IReadOnlyList<LegacySourceLine> sourceLines)
    {
        var errors = new List<string>();
        ValidateAuditedCounts(sourceLines, errors);
        if (entries.Count != sourceLines.Count)
        {
            errors.Add($"Manifest entry count {entries.Count} does not equal frozen source count {sourceLines.Count}.");
        }

        var entriesByFingerprint = entries.ToDictionary(entry => entry.Fingerprint, StringComparer.Ordinal);
        for (var index = 0; index < sourceLines.Count && index < entries.Count; index++)
        {
            if (!entries[index].Fingerprint.Equals(sourceLines[index].Fingerprint, StringComparison.Ordinal))
            {
                errors.Add($"Manifest source order diverges at index {index}.");
                break;
            }
        }

        foreach (var group in sourceLines.GroupBy(line => line.NormalizedText, StringComparer.Ordinal))
        {
            var orderedGroup = group.ToArray();
            var expectedCanonical = orderedGroup[0].Fingerprint;
            var groupFingerprints = orderedGroup.Select(line => line.Fingerprint).ToHashSet(StringComparer.Ordinal);
            foreach (var line in orderedGroup)
            {
                if (!entriesByFingerprint.TryGetValue(line.Fingerprint, out var entry))
                {
                    continue;
                }

                if (!entry.CanonicalFingerprint.Equals(expectedCanonical, StringComparison.Ordinal))
                {
                    errors.Add($"Canonical fingerprint does not match normalized source group for {line.Fingerprint}.");
                }

                var expectedDuplicates = new HashSet<string>(groupFingerprints, StringComparer.Ordinal);
                expectedDuplicates.Remove(line.Fingerprint);
                var actualDuplicates = entry.DuplicateReferences.ToHashSet(StringComparer.Ordinal);
                if (!actualDuplicates.SetEquals(expectedDuplicates))
                {
                    errors.Add($"Duplicate references do not exactly match normalized source group for {line.Fingerprint}.");
                }
            }
        }

        return errors;
    }

    private static void ValidateAuditedCounts(IReadOnlyList<LegacySourceLine> sourceLines, ICollection<string> errors)
    {
        foreach (var (sourceFile, expected) in AuditedSources)
        {
            var source = sourceLines.Where(line => line.SourceFile.Equals(sourceFile, StringComparison.Ordinal)).ToArray();
            if (source.Length != expected.SourceCount || source.Select(line => line.NormalizedText).Distinct(StringComparer.Ordinal).Count() != expected.NormalizedCount)
            {
                errors.Add($"Audited source totals do not match for {sourceFile}.");
            }
        }

        if (sourceLines.Any(line => !AuditedSources.ContainsKey(line.SourceFile)))
        {
            errors.Add("Frozen sources contain an unrecognized source file.");
        }

        if (sourceLines.Count != 1917 || sourceLines.Select(line => line.NormalizedText).Distinct(StringComparer.Ordinal).Count() != 1500)
        {
            errors.Add("Audited aggregate totals do not match 1917 source mutations and 1500 normalized effects.");
        }
    }
}
