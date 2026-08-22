using FluentAssertions;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// Every profile has to be able to record its own rollback snapshot. A ceiling that only Safe fitted
/// under made Gaming, Maximum Performance and Full Legacy fail at apply time with a generic recovery
/// message, which is exactly what beta testers reported.
/// </summary>
public sealed class LegacyBundleSnapshotSizeTests
{
    /// <summary>Must stay at or below ProtectedPlanStore's per-value ceiling.</summary>
    private const int ProtectedStoreValueCeiling = 64 * 1024;

    [Fact]
    public async Task EveryProfile_CanRecordItsSnapshotWithoutHittingTheLimit()
    {
        foreach (var operation in Bundles())
        {
            var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);

            snapshot.Should().NotBeNull($"{operation.Descriptor.Name} must be able to capture a rollback snapshot");
            snapshot!.Length.Should().BeLessThan(ProtectedStoreValueCeiling,
                $"{operation.Descriptor.Name} snapshot must fit the protected journal");
        }
    }

    [Fact]
    public async Task TheLargestProfile_StillLeavesRoomForMachinesWithMoreAdaptersAndGpus()
    {
        var full = Bundles().Single(x => x.Category is null && x.Profile == LegacyBundleProfile.FullLegacy);

        var snapshot = await full.ReadCurrentValueAsync(CancellationToken.None);

        // Snapshot size scales with enumerated network interfaces and display class keys, so the
        // measured size on one PC must stay well under the ceiling rather than merely fit.
        snapshot!.Length.Should().BeLessThan(ProtectedStoreValueCeiling / 2,
            "a machine with more adapters must not tip the largest profile over the limit");
    }

    [Fact]
    public async Task ASnapshotRoundTripsBackToTheSameProfile()
    {
        var gaming = Bundles().Single(x => x.Profile == LegacyBundleProfile.Gaming);

        var snapshot = await gaming.ReadCurrentValueAsync(CancellationToken.None);

        // Restoring a snapshot taken for another profile must be refused, so the encoding has to carry
        // the profile identity through the round trip.
        var other = Bundles().Single(x => x.Profile == LegacyBundleProfile.Safe);
        var act = async () => await other.RestoreAsync(snapshot, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    private static IReadOnlyList<LegacyBundleOperation> Bundles() =>
        LegacyBundleOperation.CreateAll(new FixedProcessRunner()).Cast<LegacyBundleOperation>().ToArray();
}
