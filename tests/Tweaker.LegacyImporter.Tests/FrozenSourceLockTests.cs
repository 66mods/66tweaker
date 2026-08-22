using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;

namespace Tweaker.LegacyImporter.Tests;

/// <summary>
/// The frozen scripts must still be the exact bytes `source-hashes.json` records.
///
/// Nothing enforced this, and it broke without a single test failing. Git normalises line endings on
/// checkout, so a fresh clone produced a batch file six bytes longer than the lock claimed; the bundle
/// regenerated from that clone carried a different SHA-256 than the one shipped inside the app, which
/// made the lock the README promises impossible for anyone else to verify. `.gitattributes` now marks
/// those files binary, and this is the check that says so out loud.
/// </summary>
public sealed class FrozenSourceLockTests
{
    private sealed record LockedFile(string Path, long Bytes, string Sha256);
    private sealed record LockFile(LockedFile[] Files);

    [Fact]
    public void EveryFrozenScriptStillMatchesItsRecordedSizeAndHash()
    {
        var root = Path.Combine(FindRepositoryRoot(), "legacy", "source");
        var locked = JsonSerializer.Deserialize<LockFile>(
            File.ReadAllBytes(Path.Combine(root, "source-hashes.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        locked.Files.Should().NotBeEmpty("the lock file itself is part of what is being checked");

        foreach (var file in locked.Files)
        {
            var path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue("{0} is named by the lock", file.Path);

            var bytes = File.ReadAllBytes(path);
            // Size first: it is the symptom a line-ending rewrite produces, and it reads far more
            // usefully in a failure than two hex strings that differ.
            bytes.LongLength.Should().Be(file.Bytes,
                "{0} must be byte-for-byte what the lock records — check .gitattributes before the file", file.Path);
            Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(file.Sha256, "{0}", file.Path);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "66mods.Tweaker.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
