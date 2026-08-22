using System.Diagnostics;
using FluentAssertions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class LegacyBundleTests
{
    [Fact]
    public void EmbeddedBundle_IsHashLockedAndCoversEveryFrozenMutation()
    {
        var bundle = LegacyBundleLoader.Load();

        bundle.SourceFingerprintCount.Should().Be(1917);
        bundle.CanonicalEffectCount.Should().Be(1500);
        bundle.Effects.SelectMany(x => x.SourceFingerprints).Should().HaveCount(1917);
        bundle.Effects.Select(x => x.CanonicalFingerprint).Should().OnlyHaveUniqueItems();
        bundle.Effects.Count(x => !x.Executable).Should().Be(7);
        bundle.Effects.Where(x => !x.Executable).Should().OnlyContain(x =>
            x.SkipReason!.Contains("resolution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SafeBundle_UsesFakeRegistryAndNeverLaunchesARealProcess()
    {
        var executor = new RecordingExecutor();
        var registry = new MemoryRegistry();
        var operation = new LegacyBundleOperation(LegacyBundleProfile.Safe, registry,
            new FixedProcessRunner(TimeSpan.FromSeconds(1), executor));

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
        operation.CanonicalEffectCount.Should().BeGreaterThan(0);
        registry.Applied.Should().Be(operation.CanonicalEffectCount);
        executor.Starts.Should().Be(0);
        await operation.RestoreAsync(snapshot, CancellationToken.None);
    }

    [Fact]
    public void Profiles_AreClosedCompiledOperationsAndFullMapsAllExecutableEffects()
    {
        var operations = LegacyBundleOperation.CreateAll(new FixedProcessRunner());
        operations.OfType<LegacyBundleOperation>().Count(x => x.Category is null).Should().Be(4);
        operations.Select(x => x.Descriptor.Id).Should().StartWith(
            ["legacy.bundle.safe", "legacy.bundle.gaming", "legacy.bundle.maximum", "legacy.bundle.full"]);
        var full = operations.Cast<LegacyBundleOperation>().Single(x => x.Category is null && x.Profile == LegacyBundleProfile.FullLegacy);
        full.CanonicalEffectCount.Should().Be(1493);
        full.SourceFingerprintCount.Should().BeLessThanOrEqualTo(1917);
        full.ExcludedResolutionEffects.Should().Be(7);
    }

    [Fact]
    public async Task RestorePoint_GetsItsOwnBudgetInsteadOfTheShortProcessDefault()
    {
        var executor = new RecordingExecutor();
        var operation = new LegacyBundleOperation(LegacyBundleProfile.MaximumPerformance, new MemoryRegistry(),
            new FixedProcessRunner(TimeSpan.FromSeconds(1), executor));

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        // Checkpoint-Computer is the first process this profile starts and the slowest thing it does.
        var checkpoint = executor.Timeouts.First();
        checkpoint.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(2));
        executor.Timeouts.Skip(1).Should().OnlyContain(x => x == TimeSpan.FromSeconds(1));
    }

    private sealed class RecordingExecutor : IFixedProcessExecutor
    {
        private readonly List<TimeSpan> timeouts = [];
        public int Starts { get; private set; }
        public IReadOnlyList<TimeSpan> Timeouts => timeouts;
        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Starts++;
            timeouts.Add(timeout);
            return Task.FromResult(new FixedProcessResult(0, string.Empty, string.Empty, false));
        }
    }

    /// <summary>
    /// Stores what it is given so a written value can be read back, which is what verification now does.
    /// A fake that always reported "absent" would make a real verification failure look like a pass.
    /// </summary>
    private sealed class MemoryRegistry : ILegacyRegistryBackend
    {
        private readonly Dictionary<string, (int Kind, string? Payload)> values = new(StringComparer.OrdinalIgnoreCase);
        public int Applied { get; private set; }

        public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target) =>
            values.TryGetValue(Key(target), out var stored)
                ? new(effectId, target.Hive, target.SubKey, target.ValueName, true, true, stored.Kind, stored.Payload)
                : new(effectId, target.Hive, target.SubKey, target.ValueName, false, false, null, null);

        public void Apply(LegacyRegistryTarget target)
        {
            Applied++;
            if (target.Action == LegacyRegistryAction.Write)
                values[Key(target)] = ((int)target.Kind!.Value, RegistryWire.Encode(target.Value));
            else values.Remove(Key(target));
        }

        public void Restore(LegacyRegistrySnapshot snapshot) { }
        private static string Key(LegacyRegistryTarget target) => $"{target.Hive}|{target.SubKey}|{target.ValueName}";
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
    }
}
