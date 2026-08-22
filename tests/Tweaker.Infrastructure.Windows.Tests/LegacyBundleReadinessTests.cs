using System.Diagnostics;
using FluentAssertions;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class LegacyBundleReadinessTests : IDisposable
{
    [Fact]
    public void MeasureReadiness_UntouchedMachineScoresZeroAcrossRealMeasurableEffects()
    {
        var operation = Build(new AbsentRegistry());

        var readiness = operation.MeasureReadiness(CancellationToken.None);

        readiness.Measurable.Should().BeGreaterThan(0);
        readiness.Matching.Should().Be(0);
        readiness.ScorePercent.Should().Be(0);
    }

    [Fact]
    public void MeasureReadiness_ValuesWindowsAlreadyHadRightAreNotCountedAsOptimization()
    {
        // Everything already matches on the very first measurement, so none of it is something this tool
        // improved. Scoring it 100% is what made an untouched PC read as three-quarters optimized.
        var operation = Build(new AlreadyAppliedRegistry());

        var readiness = operation.MeasureReadiness(CancellationToken.None);

        readiness.Matching.Should().Be(readiness.Measurable);
        readiness.Improvable.Should().Be(0);
        readiness.AlreadyCorrect.Should().Be(readiness.Measurable);
        readiness.ScorePercent.Should().Be(100, "nothing is left for this profile to improve");
    }

    [Fact]
    public void MeasureReadiness_ScoresOneHundredOnceTheImprovableTargetsAreApplied()
    {
        var registry = new SwitchableRegistry();
        var baseline = NewBaseline();
        var first = new LegacyBundleOperation(LegacyBundleProfile.Safe, registry, Runner(), baseline);
        first.MeasureReadiness(CancellationToken.None).ScorePercent.Should().Be(0);

        registry.NowHoldsEveryTargetValue = true;
        var second = new LegacyBundleOperation(LegacyBundleProfile.Safe, registry, Runner(), baseline);

        second.MeasureReadiness(CancellationToken.None).ScorePercent.Should().Be(100);
    }

    [Fact]
    public void MeasureReadiness_RemembersTheFirstObservationAcrossMeasurements()
    {
        var registry = new SwitchableRegistry { NowHoldsEveryTargetValue = true };
        var baseline = NewBaseline();
        new LegacyBundleOperation(LegacyBundleProfile.Safe, registry, Runner(), baseline)
            .MeasureReadiness(CancellationToken.None);

        // The PC drifts away from the target after the baseline was learned.
        registry.NowHoldsEveryTargetValue = false;
        var later = new LegacyBundleOperation(LegacyBundleProfile.Safe, registry, Runner(), baseline)
            .MeasureReadiness(CancellationToken.None);

        later.Improvable.Should().Be(0, "targets that were already right when first seen stay excluded");
    }

    [Fact]
    public void MeasureReadiness_ReadsOnlyAndNeverMutatesOrLaunchesAProcess()
    {
        var registry = new AbsentRegistry();
        var executor = new RecordingExecutor();
        var operation = new LegacyBundleOperation(LegacyBundleProfile.Safe, registry,
            new FixedProcessRunner(TimeSpan.FromSeconds(1), executor));

        operation.MeasureReadiness(CancellationToken.None);

        registry.Applied.Should().Be(0);
        registry.Restored.Should().Be(0);
        executor.Starts.Should().Be(0);
    }

    [Fact]
    public void MeasureReadiness_HonoursCancellation()
    {
        var operation = Build(new AbsentRegistry());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => operation.MeasureReadiness(cancelled.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ScorePercent_IsUnscoredOnlyWhenThereIsNothingToMeasure() =>
        new LegacyBundleReadiness(0, 0, 0, 0).ScorePercent.Should().BeNull();

    [Fact]
    public void ScorePercent_IsFullWhenEveryTargetedValueIsAlreadyRight() =>
        new LegacyBundleReadiness(9, 9, 0, 0).ScorePercent.Should().Be(100);

    [Theory]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 67)]
    [InlineData(1, 8, 13)]
    public void ScorePercent_RoundsHalfAwayFromZero(int improved, int improvable, int expected) =>
        new LegacyBundleReadiness(improved, improvable, improved, improvable).ScorePercent.Should().Be(expected);

    private static FixedProcessRunner Runner() =>
        new(TimeSpan.FromSeconds(1), new RecordingExecutor());

    /// <summary>A baseline in a throwaway folder, so measuring never writes into the real user profile.</summary>
    private LegacyScoreBaseline NewBaseline() =>
        new(Path.Combine(root, Guid.NewGuid().ToString("N"), "baseline.json"));

    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-score", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private LegacyBundleOperation Build(ILegacyRegistryBackend registry) =>
        new(LegacyBundleProfile.Safe, registry, Runner(), NewBaseline());

    /// <summary>Flips between "nothing is at target" and "everything is", to move the score deliberately.</summary>
    private sealed class SwitchableRegistry : ILegacyRegistryBackend
    {
        internal bool NowHoldsEveryTargetValue { get; set; }
        public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target) =>
            NowHoldsEveryTargetValue
                ? new(effectId, target.Hive, target.SubKey, target.ValueName, true, true,
                    (int?)target.Kind, RegistryWire.Encode(target.Value))
                : new(effectId, target.Hive, target.SubKey, target.ValueName, false, false, null, null);
        public void Apply(LegacyRegistryTarget target) { }
        public void Restore(LegacyRegistrySnapshot snapshot) { }
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
    }

    private sealed class RecordingExecutor : IFixedProcessExecutor
    {
        public int Starts { get; private set; }
        public Task<FixedProcessResult> ExecuteAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Starts++;
            return Task.FromResult(new FixedProcessResult(0, string.Empty, string.Empty, false));
        }
    }

    private sealed class AbsentRegistry : ILegacyRegistryBackend
    {
        public int Applied { get; private set; }
        public int Restored { get; private set; }
        public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target) =>
            new(effectId, target.Hive, target.SubKey, target.ValueName, false, false, null, null);
        public void Apply(LegacyRegistryTarget target) => Applied++;
        public void Restore(LegacyRegistrySnapshot snapshot) => Restored++;
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
    }

    private sealed class AlreadyAppliedRegistry : ILegacyRegistryBackend
    {
        public LegacyRegistrySnapshot Capture(string effectId, LegacyRegistryTarget target) =>
            new(effectId, target.Hive, target.SubKey, target.ValueName, true, true,
                (int?)target.Kind, RegistryWire.Encode(target.Value));
        public void Apply(LegacyRegistryTarget target) => throw new InvalidOperationException("Measurement must not mutate.");
        public void Restore(LegacyRegistrySnapshot snapshot) => throw new InvalidOperationException("Measurement must not restore.");
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
    }
}
