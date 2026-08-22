using FluentAssertions;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// A snapshot must stay restorable across an update that changes the bundle.
///
/// Renaming the frozen script for 0.9.17 changed the bundle hash, and rollback rejected any snapshot whose
/// hash was not the current one. The effect was silent and total: anyone who applied a group on 0.9.16 and
/// then updated found Restore refusing, with their changes now permanent. Nothing in the suite noticed,
/// because every test applied and rolled back within one build.
/// </summary>
public sealed class LegacyBundleSnapshotCompatibilityTests
{
    /// <summary>The bundle shipped as 0.9.16 — the first build distributed outside the owner's machine.</summary>
    private const string ShippedBundleSha256 = "97EE0BB400F6F57EF5A478A98BFFD047A0373A255BC77C894B6BFB0F79260A1F";

    [Fact]
    public async Task ASnapshotFromAPreviouslyShippedBundleCanStillBeRolledBack()
    {
        var registry = new RecordingRegistry();
        var operation = Full(registry);
        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);

        await FluentActions.Invoking(() => operation.RestoreAsync(Restamp(snapshot, ShippedBundleSha256), CancellationToken.None))
            .Should().NotThrowAsync("a snapshot carries the values it captured; rolling it back never reads the bundle");
    }

    [Fact]
    public async Task ASnapshotFromABundleThisProductNeverShippedIsStillRefused()
    {
        // The check is provenance, not decoration: widening it to "any hash" would accept a snapshot from
        // a fork or a corrupted file just as readily.
        var operation = Full(new RecordingRegistry());
        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);

        await FluentActions.Invoking(() => operation.RestoreAsync(
                Restamp(snapshot, new string('A', 64)), CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ASnapshotTakenByThisBuildCarriesThisBuildsBundle()
    {
        // Apply only ever stamps the current hash, which is what keeps the compatibility list from growing
        // on its own.
        var operation = Full(new RecordingRegistry());

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);

        SnapshotCodec.Decode(snapshot).BundleSha256.Should().Be(LegacyBundleIdentity.Sha256);
    }

    [Fact]
    public void TheCompatibilityListNamesOnlyHashesThatAreNoLongerCurrent()
    {
        // A stale entry equal to the current hash would be dead weight that reads as meaningful.
        ShippedBundleSha256.Should().NotBe(LegacyBundleIdentity.Sha256);
    }

    /// <summary>Re-stamps a snapshot with a different bundle hash, leaving its captured entries untouched.</summary>
    private static string Restamp(string? encoded, string bundleSha256) =>
        SnapshotCodec.Encode(SnapshotCodec.Decode(encoded) with { BundleSha256 = bundleSha256 });

    private static LegacyBundleOperation Full(ILegacyRegistryBackend registry) =>
        new(LegacyBundleProfile.FullLegacy, registry, new FixedProcessRunner(TimeSpan.FromSeconds(1)));

    /// <summary>Captures and restores in memory; rollback correctness is covered elsewhere.</summary>
    private sealed class RecordingRegistry : ILegacyRegistryBackend
    {
        public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target) =>
            new(effectId, target.Hive, target.SubKey, target.ValueName, KeyExisted: true, ValueExisted: true,
                Kind: 4, Payload: "0");
        public void Apply(LegacyRegistryTarget target) { }
        public void Restore(LegacyRegistrySnapshot snapshot) { }
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
    }
}
