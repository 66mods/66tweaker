using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Models;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Legacy;
using Tweaker.Infrastructure.Windows.Operations.Process;

namespace Tweaker.App.Tests;

/// <summary>
/// The score went dead when the Optimize page stopped having a selection: it was measuring "the selected
/// profile", every card became a Run button, nothing was ever selected, and Home read "Not scored" with
/// "0 effects across 0 categories" forever. It now describes the machine, so it has to hold without anyone
/// selecting anything.
/// </summary>
public sealed class OptimizationScoreTests
{
    private static OptimizationViewModel Build() =>
        OptimizationViewModel.CreateForTests(
            LegacyBundleOperation.CreateCategories(new FixedProcessRunner()),
            new TransactionCoordinator(new InMemoryStore()), Snapshot());

    [Fact]
    public async Task TheHeadlineCountsDescribeEveryGroupWithoutASelection()
    {
        var vm = Build();

        await vm.LoadAsync(CancellationToken.None);

        vm.Items.Should().OnlyContain(x => !x.IsSelected, "nothing is selected; each card runs itself");
        vm.SelectedEffectCount.Should().BeGreaterThan(1000, "the counts cover every group the page offers");
        vm.CategorySummary.Should().NotContain("0 effects");
        vm.CategoryBreakdown.Count(x => x.Count > 0).Should().BeGreaterThan(1,
            "the breakdown must add the groups up, not show one profile's slice");
    }

    [Fact]
    public async Task TheBreakdownSumsTheGroupsRatherThanTakingOne()
    {
        var vm = Build();
        await vm.LoadAsync(CancellationToken.None);

        var groups = LegacyBundleOperation.CreateCategories(new FixedProcessRunner()).Cast<LegacyBundleOperation>();
        var expected = groups.Sum(x => x.CanonicalEffectCount);

        vm.CategoryBreakdown.Sum(x => x.Count).Should().Be(expected);
        vm.SelectedEffectCount.Should().Be(expected);
    }

    [Fact]
    public async Task TheScoreIsMeasuredOnLoadAndReadsTheRealMachine()
    {
        var vm = Build();

        await vm.LoadAsync(CancellationToken.None);
        await vm.MeasureScoreAsync(CancellationToken.None);

        // Read-only over the live registry: it must produce a real percentage, not stay unscored.
        vm.OptimizationScore.Should().NotBeNull("Home showing \"Not scored\" on a working PC is the bug");
        vm.OptimizationScore!.Value.Should().BeInRange(0, 100);
        vm.ScoreCaption.Should().Be("Optimized");
        vm.OptimizationScoreText.Should().NotBe("—");
    }

    [Fact]
    public async Task TheHeroTextStopsNamingAProfileThePageNoLongerHas()
    {
        var vm = Build();
        await vm.LoadAsync(CancellationToken.None);
        await vm.MeasureScoreAsync(CancellationToken.None);

        vm.HeroSubtitle.Should().NotContain("Safe", "presets are gone from this page");
        vm.HeroSubtitle.Should().NotBeEmpty();
        vm.HeroTitle.Should().NotBeEmpty();
    }

    private static SystemSnapshot Snapshot() => new(
        new("Windows 11", "10", 26100), new("CPU", "Vendor"), [], new(16L * 1024 * 1024 * 1024),
        new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    private sealed class InMemoryStore : ITransactionStore
    {
        private readonly Dictionary<Guid, TransactionRecord> records = [];
        public Task BeginAsync(TransactionRecord transaction, CancellationToken cancellationToken)
        {
            records[transaction.Id] = transaction;
            return Task.CompletedTask;
        }
        public Task SaveAsync(TransactionRecord transaction, CancellationToken cancellationToken)
        {
            records[transaction.Id] = transaction;
            return Task.CompletedTask;
        }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(records.GetValueOrDefault(id));
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult<TransactionRecord?>(null);
    }
}
