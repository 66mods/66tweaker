using System.Diagnostics;
using FluentAssertions;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// Verification is the product's core promise. It used to re-add the run tallies, which always matched,
/// so a run in which nothing landed still reported itself verified.
/// </summary>
public sealed class LegacyBundleVerificationTests
{
    [Fact]
    public async Task Verify_FailsWhenTheRegistryDoesNotHoldWhatWasWritten()
    {
        var registry = new DiscardingRegistry();
        var operation = Build(registry);

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None))
            .Should().BeFalse("nothing the run wrote can be read back");
    }

    [Fact]
    public async Task Verify_SucceedsWhenEveryWrittenValueReadsBack()
    {
        var operation = Build(new RecordingRegistry());

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Verify_FailsWhenAValueWasChangedBehindOurBack()
    {
        var registry = new RecordingRegistry();
        var operation = Build(registry);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        registry.CorruptOne();

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_SucceedsWhenTheBundleWritesConflictingValuesToOneKey()
    {
        // The frozen BAT really does this: Win32PrioritySeparation is written 38, then 22, then 40.
        // Only the last write survives, so verifying every write against the final state would make
        // Gaming, Maximum Performance and Full Legacy fail permanently.
        var registry = new RecordingRegistry();
        var operation = Build(registry);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        registry.RewriteOneTargetWithADifferentValue();
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Verify_FailsBeforeAnythingHasBeenApplied()
    {
        var operation = Build(new RecordingRegistry());

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_ReportsTalliesThatAddUpToTheSelectedEffectCount()
    {
        var operation = Build(new RecordingRegistry());

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        var summary = operation.LastSummary;
        (summary.Executed + summary.Skipped + summary.Failed).Should().Be(summary.Selected,
            "a refused restore point is not an effect and must not inflate the tallies");
    }

    [Fact]
    public async Task Verify_SucceedsForFullLegacyWhenLaterEffectsDeleteKeysTheRunWroteInto()
    {
        // Field failure on 0.9.3: the bundle writes 110 IFEO PerfOptions values and then deletes the keys
        // holding them. Reading every write back at the end found them gone, so Full Legacy could never
        // verify on any machine and always surfaced as "did not complete every requested operation".
        var operation = Full(new FaithfulRegistry());

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Restore_SkipsTargetsTheRunNeverChangedInsteadOfFailingOnThem()
    {
        // Windows refuses even an elevated writer on Edge's TaskCache\Tree keys. Rewriting a value that
        // still holds exactly what was captured is a no-op that can only fail, and it turned every
        // rollback into a partial rollback over changes the run had not made.
        var registry = new FaithfulRegistry { ProtectedSubKeyFragment = "TaskCache" };
        registry.Seed(LegacyRegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\MicrosoftEdgeUpdateTaskMachineUA");
        var operation = Full(registry);

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await FluentActions.Invoking(() => operation.RestoreAsync(snapshot, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task Restore_StillReportsFailureWhenSomethingTheRunChangedCannotBeRestored()
    {
        var registry = new FaithfulRegistry();
        var operation = Full(registry);
        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        registry.ProtectedSubKeyFragment = "CurrentVersion";

        await FluentActions.Invoking(() => operation.RestoreAsync(snapshot, CancellationToken.None))
            .Should().ThrowAsync<AggregateException>();
    }

    [Fact]
    public async Task Verify_SurvivesAScriptDeletingKeysTheRunWroteInto()
    {
        // The bundle's PowerShell wipe of Image File Execution Options is invisible to registry-action
        // analysis, so it is modelled here as an outside deletion after the writes have landed.
        var registry = new FaithfulRegistry();
        var operation = Full(registry);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);
        registry.DeleteTreeFromOutside(LegacyRegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options");

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Verify_FailsWhenAWriteSilentlyDoesNotStick()
    {
        // A key that accepts SetValue and keeps nothing is the failure the end-of-run comparison used to
        // catch; reading back at write time has to catch it too.
        var operation = Full(new DiscardingRegistry());

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_FailsWhenAValueIsClobberedWhileItsKeySurvives()
    {
        var registry = new FaithfulRegistry();
        var operation = Full(registry);
        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        registry.CorruptOneSurvivingValue();

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeFalse();
    }

    private static LegacyBundleOperation Full(ILegacyRegistryBackend registry) =>
        new(LegacyBundleProfile.FullLegacy, registry, new FixedProcessRunner(TimeSpan.FromSeconds(1), new NoopExecutor()));

    /// <summary>
    /// Models the two behaviours the earlier fakes did not: deleting a key removes every value beneath it,
    /// and some keys refuse writes however elevated the caller is.
    /// </summary>
    private sealed class FaithfulRegistry : ILegacyRegistryBackend
    {
        private readonly Dictionary<string, (int Kind, string? Payload)> values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        public string? ProtectedSubKeyFragment { get; set; }

        public void Seed(LegacyRegistryHive hive, string subKey) => keys.Add($"{hive}|{subKey}");

        /// <summary>Stands in for a PowerShell script or another process removing a whole tree mid-run.</summary>
        public void DeleteTreeFromOutside(LegacyRegistryHive hive, string subKey) => RemoveSubTree(hive, subKey);

        public void CorruptOneSurvivingValue()
        {
            var key = values.Keys.First();
            values[key] = (values[key].Kind, RegistryWire.Encode(0x7FFFFFFF));
        }

        public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target)
        {
            var keyExists = keys.Contains(KeyPath(target.Hive, target.SubKey));
            return values.TryGetValue(Key(target), out var stored)
                ? new(effectId, target.Hive, target.SubKey, target.ValueName, true, true, stored.Kind, stored.Payload)
                : new(effectId, target.Hive, target.SubKey, target.ValueName, keyExists, false, null, null);
        }

        public void Apply(LegacyRegistryTarget target)
        {
            Deny(target.SubKey);
            switch (target.Action)
            {
                case LegacyRegistryAction.Write:
                    keys.Add(KeyPath(target.Hive, target.SubKey));
                    values[Key(target)] = ((int)target.Kind!.Value, RegistryWire.Encode(target.Value));
                    break;
                case LegacyRegistryAction.CreateKey:
                    keys.Add(KeyPath(target.Hive, target.SubKey));
                    break;
                case LegacyRegistryAction.DeleteValue:
                    values.Remove(Key(target));
                    break;
                case LegacyRegistryAction.DeleteKey:
                    RemoveSubTree(target.Hive, target.SubKey);
                    break;
            }
        }

        public void Restore(LegacyRegistrySnapshot snapshot)
        {
            Deny(snapshot.SubKey);
            if (!snapshot.KeyExisted) { RemoveSubTree(snapshot.Hive, snapshot.SubKey); return; }
            keys.Add(KeyPath(snapshot.Hive, snapshot.SubKey));
            if (snapshot.ValueName is null) return;
            var key = $"{snapshot.Hive}|{snapshot.SubKey}|{snapshot.ValueName}";
            if (!snapshot.ValueExisted) values.Remove(key);
            else values[key] = (snapshot.Kind!.Value, snapshot.Payload);
        }

        private void Deny(string subKey)
        {
            if (ProtectedSubKeyFragment is { } fragment && subKey.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Access to the registry key '{subKey}' is denied.");
        }

        private void RemoveSubTree(LegacyRegistryHive hive, string subKey)
        {
            var prefix = KeyPath(hive, subKey);
            foreach (var stale in keys.Where(x => IsSelfOrBelow(x, prefix)).ToArray()) keys.Remove(stale);
            foreach (var stale in values.Keys.Where(x => IsSelfOrBelow(x, prefix + "|") || IsSelfOrBelow(x, prefix)).ToArray())
                values.Remove(stale);
        }

        private static bool IsSelfOrBelow(string candidate, string prefix) =>
            candidate.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(prefix + "|", StringComparison.OrdinalIgnoreCase);

        private static string KeyPath(LegacyRegistryHive hive, string subKey) => $"{hive}|{subKey}";
        private static string Key(LegacyRegistryTarget target) => $"{target.Hive}|{target.SubKey}|{target.ValueName}";
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
    }

    private static LegacyBundleOperation Build(ILegacyRegistryBackend registry) =>
        new(LegacyBundleProfile.Safe, registry, new FixedProcessRunner(TimeSpan.FromSeconds(1), new NoopExecutor()));

    private sealed class NoopExecutor : IFixedProcessExecutor
    {
        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new FixedProcessResult(0, string.Empty, string.Empty, false));
    }

    /// <summary>Accepts writes and forgets them, standing in for a machine where the write silently did not stick.</summary>
    private sealed class DiscardingRegistry : ILegacyRegistryBackend
    {
        public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target) =>
            new(effectId, target.Hive, target.SubKey, target.ValueName, false, false, null, null);
        public void Apply(LegacyRegistryTarget target) { }
        public void Restore(LegacyRegistrySnapshot snapshot) { }
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
    }

    private sealed class RecordingRegistry : ILegacyRegistryBackend
    {
        private readonly Dictionary<string, (int Kind, string? Payload)> values = new(StringComparer.OrdinalIgnoreCase);

        public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target) =>
            values.TryGetValue(Key(target), out var stored)
                ? new(effectId, target.Hive, target.SubKey, target.ValueName, true, true, stored.Kind, stored.Payload)
                : new(effectId, target.Hive, target.SubKey, target.ValueName, false, false, null, null);

        public void Apply(LegacyRegistryTarget target)
        {
            if (target.Action == LegacyRegistryAction.Write)
                values[Key(target)] = ((int)target.Kind!.Value, RegistryWire.Encode(target.Value));
        }

        internal void CorruptOne()
        {
            var key = values.Keys.First();
            values[key] = (values[key].Kind, RegistryWire.Encode(0x7FFFFFFF));
        }

        /// <summary>Simulates a second effect landing a different value on a key the run already wrote.</summary>
        internal void RewriteOneTargetWithADifferentValue() => CorruptOne();

        public void Restore(LegacyRegistrySnapshot snapshot) { }
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
        private static string Key(LegacyRegistryTarget target) => $"{target.Hive}|{target.SubKey}|{target.ValueName}";
    }
}
