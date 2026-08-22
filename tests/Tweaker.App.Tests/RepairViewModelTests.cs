using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Infrastructure.Windows.Repair;

namespace Tweaker.App.Tests;

public sealed class RepairViewModelTests
{
    [Fact]
    public void SelectedAction_UpdatesReadyStatusWithUserFacingName()
    {
        var vm = new RepairViewModel(new RepairService(new Runner()), new Confirmation(false));
        var action = vm.Actions.Single(x => x.Id == "reset-winsock");

        vm.SelectedAction = action;

        vm.Status.Should().Contain(action.Name);
        vm.Status.Should().Contain("No changes");
    }

    [Fact]
    public async Task RunAsync_UserDeclines_DoesNotStartProcess()
    {
        var runner = new Runner();
        var vm = new RepairViewModel(new RepairService(runner), new Confirmation(false));
        vm.SelectedAction = vm.Actions.Single(x => x.Id == "flush-dns");

        await vm.RunAsync(CancellationToken.None);

        runner.Count.Should().Be(0);
        vm.Status.Should().Contain("Cancelled");
    }

    [Fact]
    public async Task RunAsync_UserConfirms_RunsSelectedAllowlistedAction()
    {
        var runner = new Runner();
        var vm = new RepairViewModel(new RepairService(runner), new Confirmation(true));
        vm.SelectedAction = vm.Actions.Single(x => x.Id == "flush-dns");

        await vm.RunAsync(CancellationToken.None);

        runner.Count.Should().Be(1);
        vm.Status.Should().Contain("Completed");
    }

    [Fact]
    public async Task RunAsync_ElevatedAction_UsesOnlyScopedWorker()
    {
        var runner = new Runner();
        var launcher = new Launcher();
        var vm = new RepairViewModel(new RepairService(runner), new Confirmation(true), launcher, isAdministrator: false);
        vm.SelectedAction = vm.Actions.Single(x => x.Id == "verify-system");

        await vm.RunAsync(CancellationToken.None);

        launcher.ActionId.Should().Be("verify-system");
        runner.Count.Should().Be(0);
        vm.Status.Should().Contain("action only");
    }

    private sealed class Launcher : IRepairElevationLauncher
    {
        public string? ActionId { get; private set; }
        public Task LaunchAsync(string actionId, CancellationToken token) { ActionId = actionId; return Task.CompletedTask; }
    }
    private sealed class Confirmation(bool answer) : IRepairConfirmation
    {
        public bool Confirm(RepairAction action) => answer;
    }

    private sealed class Runner : IRepairProcessRunner
    {
        public int Count { get; private set; }
        public Task<RepairProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Count++;
            return Task.FromResult(new RepairProcessResult(0, "Done", ""));
        }
    }
}
