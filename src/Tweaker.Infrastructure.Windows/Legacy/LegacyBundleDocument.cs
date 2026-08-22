using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tweaker.Infrastructure.Windows.Legacy;

internal sealed record LegacyBundleSource(string Path, long Bytes, string Sha256);
internal sealed record LegacyBundleEffect(string Id, string CanonicalFingerprint, IReadOnlyList<string> SourceFingerprints,
    string SourceFile, int LineNumber, string Section, string Kind, string Command,
    IReadOnlyList<string> Profiles, bool Executable, string? SkipReason, bool Irreversible, bool SecurityReduction);
internal sealed record LegacyBundleDocument(int SchemaVersion, int SourceFingerprintCount, int CanonicalEffectCount,
    IReadOnlyList<LegacyBundleSource> Sources, IReadOnlyList<LegacyBundleEffect> Effects);

internal static class LegacyBundleLoader
{
    private const string ResourceName = "66mods.legacy.bundle.json";
    private static readonly Lazy<LegacyBundleDocument> Cached = new(LoadCore);

    internal static LegacyBundleDocument Load() => Cached.Value;

    private static LegacyBundleDocument LoadCore()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The embedded legacy bundle is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(LegacyBundleIdentity.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The embedded legacy bundle hash does not match this executable.");
        var document = JsonSerializer.Deserialize<LegacyBundleDocument>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                AllowTrailingCommas = false,
                ReadCommentHandling = JsonCommentHandling.Disallow,
                PropertyNameCaseInsensitive = false,
                MaxDepth = 16
            }) ?? throw new InvalidDataException("The embedded legacy bundle is invalid.");
        Validate(document);
        return document;
    }

    private static void Validate(LegacyBundleDocument value)
    {
        if (value.SchemaVersion != LegacyBundleIdentity.SchemaVersion ||
            value.SourceFingerprintCount != LegacyBundleIdentity.SourceFingerprintCount ||
            value.CanonicalEffectCount != LegacyBundleIdentity.CanonicalEffectCount ||
            value.Effects.Count != LegacyBundleIdentity.CanonicalEffectCount ||
            value.Sources.Count != 3)
            throw new InvalidDataException("The embedded legacy bundle coverage header is invalid.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in value.Effects)
        {
            if (!ids.Add(effect.Id) || effect.SourceFingerprints.Count == 0 ||
                !effect.SourceFingerprints.Contains(effect.CanonicalFingerprint, StringComparer.Ordinal) ||
                effect.Profiles.Any(x => x is not ("safe" or "gaming" or "maximum" or "full")) ||
                effect.Executable != effect.Profiles.Contains("full", StringComparer.Ordinal) ||
                effect.SourceFingerprints.Any(x => x.Length != 64 || !fingerprints.Add(x)))
                throw new InvalidDataException("The embedded legacy bundle contains invalid or duplicate coverage.");
        }
        if (fingerprints.Count != LegacyBundleIdentity.SourceFingerprintCount)
            throw new InvalidDataException("The embedded legacy bundle source coverage is incomplete.");
    }
}
