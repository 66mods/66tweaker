
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class OptimizationProfileSelectionTests
{
    [Fact]
    public async Task SelectingProfiles_ChangesTheRealOperationSelection()
    {
        var low = new Operation("low", ImpactLevel.Low, RiskLevel.Safe);
        var medium = new Operation("medium", ImpactLevel.Medium, RiskLevel.Safe);
        var advanced = new Operation("advanced", ImpactLevel.Medium, RiskLevel.Advanced);
        var experimental = new Operation("experimental", ImpactLevel.High, RiskLevel.Experimental);
        var vm = OptimizationViewModel.CreateForTests([low, medium, advanced, experimental], new TransactionCoordinator(new Store()), Snapshot());
        await vm.LoadAsync(CancellationToken.None);

        vm.SelectedProfile.Should().Be("Safe");
        vm.Items.Single(x => x.Operation == low).IsSelected.Should().BeTrue();
        vm.Items.Single(x => x.Operation == medium).IsSelected.Should().BeFalse();
        vm.Items.Single(x => x.Operation == experimental).IsSelected.Should().BeFalse();

        vm.SelectedProfile = "Gaming";
        vm.Items.Single(x => x.Operation == medium).IsSelected.Should().BeTrue();
        vm.Items.Single(x => x.Operation == advanced).IsSelected.Should().BeFalse();
        vm.Items.Single(x => x.Operation == experimental).IsSelected.Should().BeFalse();

        vm.SelectedProfile = "Maximum Performance";
        vm.Items.Single(x => x.Operation == medium).IsSelected.Should().BeTrue();
        vm.Items.Single(x => x.Operation == advanced).IsSelected.Should().BeTrue();
        vm.Items.Single(x => x.Operation == experimental).IsSelected.Should().BeFalse();

        vm.SelectedProfile = "Experimental";
        vm.Items.Single(x => x.Operation == low).IsSelected.Should().BeFalse();
        vm.Items.Single(x => x.Operation == experimental).IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task CustomProfile_PreservesManualCheckboxChoices()
    {
        var operation = new Operation("low", ImpactLevel.Low, RiskLevel.Safe);
        var vm = OptimizationViewModel.CreateForTests([operation], new TransactionCoordinator(new Store()), Snapshot());
        await vm.LoadAsync(CancellationToken.None);
        vm.Items[0].IsSelected = false;

        vm.SelectedProfile = "Custom";

        vm.Items[0].IsSelected.Should().BeFalse();
    }

    private static SystemSnapshot Snapshot() => new(new("Windows 11", "10", 26100), new("CPU", "AMD"), [],
        new(16_000_000_000), new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []);

    private sealed class Operation(string id, ImpactLevel impact, RiskLevel risk) : ITweakOperation, IRequestedValueProvider
    {
        public TweakDescriptor Descriptor { get; } = new(id, id, TweakCategory.Windows, impact, risk, false, false);
        public string RequestedValue => "1";
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("0");
        public Task ApplyAsync(string requestedValue, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string requestedValue, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? originalValue, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Store : ITransactionStore
    {
        private TransactionRecord? record;
        public Task BeginAsync(TransactionRecord value, CancellationToken token) { record = value; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord value, CancellationToken token) { record = value; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(record);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
    }
}
