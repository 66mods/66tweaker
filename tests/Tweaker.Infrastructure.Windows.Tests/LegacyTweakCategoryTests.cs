using FluentAssertions;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.Infrastructure.Windows.Tests;

/// <summary>
/// Categories replace the all-or-nothing presets on the Optimize page. Applying 1493 effects in one
/// transaction took minutes and rolled every one of them back when a single key refused; a category is
/// small enough to finish quickly and to fail on its own.
/// </summary>
public sealed class LegacyTweakCategoryTests
{
    private static IReadOnlyList<LegacyBundleOperation> Categories() =>
        LegacyBundleOperation.CreateCategories(new FixedProcessRunner()).Cast<LegacyBundleOperation>().ToArray();

    [Fact]
    public void EverySectionBelongsToExactlyOneCategoryOrIsDeliberatelyExcluded()
    {
        var full = LegacyBundleOperation.CreateAll(new FixedProcessRunner())
            .Cast<LegacyBundleOperation>().First(x => x.Category is null && x.Profile == LegacyBundleProfile.FullLegacy);
        var sections = full.DiagnoseEffects().Select(x => x.Section).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var claimed = LegacyTweakCategories.All.SelectMany(x => x.Sections)
            .Concat(LegacyTweakCategories.GameSections)
            .Concat(LegacyTweakCategories.InfrastructureSections)
            .ToArray();

        claimed.Should().OnlyHaveUniqueItems("a section applied by two categories would be written twice");
        sections.Should().BeSubsetOf(claimed, "an unclaimed section would be silently unreachable from the UI");
    }

    [Fact]
    public void TheCategoriesTogetherCoverEveryNonGameEffect()
    {
        var full = LegacyBundleOperation.CreateAll(new FixedProcessRunner())
            .Cast<LegacyBundleOperation>().First(x => x.Category is null && x.Profile == LegacyBundleProfile.FullLegacy);
        var expected = full.DiagnoseEffects().Count(x =>
            !LegacyTweakCategories.GameSections.Contains(x.Section, StringComparer.OrdinalIgnoreCase) &&
            !LegacyTweakCategories.InfrastructureSections.Contains(x.Section, StringComparer.OrdinalIgnoreCase));

        Categories().Sum(x => x.CanonicalEffectCount).Should().Be(expected);
    }

    [Fact]
    public void NoCategoryIsBigEnoughToBringBackTheAllOrNothingRun()
    {
        // Full Legacy was 1493 effects and ran for minutes. Every category has to be a fraction of that,
        // otherwise splitting the page has not actually solved the problem it was meant to solve.
        foreach (var category in Categories())
        {
            category.CanonicalEffectCount.Should().BeGreaterThan(0, $"{category.Descriptor.Id} would be an empty card");
            category.CanonicalEffectCount.Should().BeLessThan(500, $"{category.Descriptor.Id} is too large to apply as one unit");
        }
    }

    [Fact]
    public void EveryCategoryIsADistinctElevatedOperationTheDispatcherCanAddress()
    {
        var categories = Categories();
        categories.Should().HaveCount(LegacyTweakCategories.All.Count);
        categories.Select(x => x.Descriptor.Id).Should().OnlyHaveUniqueItems();
        categories.Should().OnlyContain(x => x.Descriptor.RequiresElevation);
        categories.Select(x => x.Descriptor.Id).Should().AllSatisfy(id =>
            Tweaker.Domain.Privilege.PrivilegedOperationRequest.IsCanonicalId(id).Should().BeTrue());
    }

    [Fact]
    public async Task ACategorySnapshotCannotBeRestoredByADifferentCategory()
    {
        // Both are slices of one bundle, so without an identity stamp a Memory snapshot would happily be
        // handed to the GPU operation and "restore" values it never captured.
        var registry = new MemoryRegistry();
        var power = Build("power", registry);
        var gpu = Build("gpu", registry);

        var powerSnapshot = await power.ReadCurrentValueAsync(CancellationToken.None);

        await FluentActions.Invoking(() => gpu.RestoreAsync(powerSnapshot, CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
        await FluentActions.Invoking(() => power.RestoreAsync(powerSnapshot, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ACategoryAppliesAndVerifiesOnItsOwn()
    {
        var operation = Build("input", new MemoryRegistry());

        await operation.ApplyAsync(operation.RequestedValue, CancellationToken.None);

        (await operation.VerifyAsync(operation.RequestedValue, CancellationToken.None)).Should().BeTrue();
        operation.LastSummary.Selected.Should().Be(operation.CanonicalEffectCount);
    }

    [Fact]
    public void OnlyTheReversibleCategorySkipsTheRestorePoint()
    {
        // A restore point costs minutes. Input tweaks are a handful of instantly reversible values, so
        // paying that price there would make the fastest category the slowest.
        var byId = Categories().ToDictionary(x => x.Category!.Id, StringComparer.Ordinal);
        byId["input"].WantsRestorePoint.Should().BeFalse();
        byId["debloat"].WantsRestorePoint.Should().BeTrue();
        byId["windows"].WantsRestorePoint.Should().BeTrue();
    }

    [Fact]
    public void TheIrreversibleCategoryIsMarkedAsSuchSoTheUiCanWarn()
    {
        var debloat = Categories().Single(x => x.Category!.Id == "debloat");
        debloat.Descriptor.Risk.Should().Be(RiskLevel.Experimental);
        debloat.IrreversibleEffectCount.Should().BeGreaterThan(0);

        var input = Categories().Single(x => x.Category!.Id == "input");
        input.Descriptor.Risk.Should().Be(RiskLevel.Safe);
        input.IrreversibleEffectCount.Should().Be(0);
    }

    private static LegacyBundleOperation Build(string id, ILegacyRegistryBackend registry) =>
        new(LegacyTweakCategories.Find(id)!, registry,
            new FixedProcessRunner(TimeSpan.FromSeconds(1), new NoopExecutor()),
            new LegacyScoreBaseline(Path.Combine(Path.GetTempPath(), "66mods-category-tests", Guid.NewGuid().ToString("N"))));

    private sealed class NoopExecutor : IFixedProcessExecutor
    {
        public Task<FixedProcessResult> ExecuteAsync(System.Diagnostics.ProcessStartInfo startInfo,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new FixedProcessResult(0, string.Empty, string.Empty, false));
    }

    private sealed class MemoryRegistry : ILegacyRegistryBackend
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
            else values.Remove(Key(target));
        }

        public void Restore(LegacyRegistrySnapshot snapshot) { }
        private static string Key(LegacyRegistryTarget target) => $"{target.Hive}|{target.SubKey}|{target.ValueName}";
        public IReadOnlyList<string> EnumerateSubKeys(LegacyRegistryHive hive, string subKey) => [];
        public IReadOnlyList<string> EnumerateDisplayClass(string vendor) => [];
        public IReadOnlyList<string> EnumerateNetworkClass() => [];
    }
}
