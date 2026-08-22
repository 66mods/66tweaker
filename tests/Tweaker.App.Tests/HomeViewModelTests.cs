using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;

using Tweaker.Infrastructure.Windows.Scanning;

namespace Tweaker.App.Tests;

public sealed class HomeViewModelTests
{
    [Fact]
    public async Task ScanAsync_Success_ExposesSystemAndInstalledGameCount()
    {
        var scanner = new FakeScanner(Snapshot());
        var vm = new HomeViewModel(scanner);

        await vm.ScanAsync(CancellationToken.None);

        vm.ScanState.Should().Be("System ready");
        vm.SystemSummary.Should().Contain("RTX 4060");
        vm.InstalledGames.Should().Be(2);
    }

    [Fact]
    public async Task ScanAsync_Failure_ProvidesActionableState()
    {
        var vm = new HomeViewModel(new FakeScanner(new InvalidOperationException("probe failed")));

        await vm.ScanAsync(CancellationToken.None);

        vm.ScanState.Should().Be("Scan failed");
        vm.StatusDetail.Should().Contain("probe failed");
    }

    private static SystemSnapshot Snapshot() => new(new("Windows 11", "10", 26100),
        new("Ryzen", "AMD"), [new("RTX 4060", "NVIDIA", "1")], new(16_000_000_000),
        new(false, true, "Balanced"), new Dictionary<string, DetectedGame>
        {
            ["Fortnite"] = new("Fortnite", true, "a"), ["Roblox"] = new("Roblox", true, "b")
        }, []);

    private sealed class FakeScanner : ISystemScanner
    {
        private readonly SystemSnapshot? snapshot;
        private readonly Exception? error;
        public FakeScanner(SystemSnapshot value) => snapshot = value;
        public FakeScanner(Exception value) => error = value;
        public Task<SystemSnapshot> ScanAsync(CancellationToken token) =>
            error is null ? Task.FromResult(snapshot!) : Task.FromException<SystemSnapshot>(error);
    }

    [Fact]
    public void LiveMetrics_AreHiddenUntilTheFirstRealSample()
    {
        var vm = new HomeViewModel(NoScan());

        vm.HasLiveMetrics.Should().BeFalse("no reader was supplied, so there is nothing honest to show");
        vm.MemoryText.Should().Be("—");
        vm.UptimeText.Should().Be("—");
    }

    [Fact]
    public void LiveMetrics_ReportWhatTheReaderMeasured()
    {
        var vm = new HomeViewModel(NoScan(),
            new StubMetrics(new LiveMetrics(37, 9835, 32694, TimeSpan.FromHours(30))),
            new StubMachineState(new MachineState(187, 89, 75, 40, 5, 9835, 32694)));

        vm.SampleLiveMetrics();

        vm.HasLiveMetrics.Should().BeTrue();
        vm.CpuLoadPercent.Should().Be(37);
        vm.MemoryLoadPercent.Should().Be(30);
        vm.MemoryText.Should().Be("9.6 / 31.9 GB");
        vm.UptimeText.Should().Be("1d 6h");
        vm.RunningProcesses.Should().Be(187);
        vm.RunningServices.Should().Be(89);
    }

    [Fact]
    public void UptimeUnderADayIsShownInHoursAndMinutes()
    {
        var vm = new HomeViewModel(NoScan(),
            new StubMetrics(new LiveMetrics(5, 4000, 16000, TimeSpan.FromMinutes(95))));

        vm.SampleLiveMetrics();

        vm.UptimeText.Should().Be("1h 35m");
    }

    [Fact]
    public void AReaderThatThrowsDoesNotBreakThePage()
    {
        // A metric that cannot be read is not worth interrupting the user over; the tiles simply stay empty.
        var vm = new HomeViewModel(NoScan(), new ThrowingMetrics());

        vm.Invoking(x => x.SampleLiveMetrics()).Should().NotThrow();
        vm.HasLiveMetrics.Should().BeFalse();
    }

    [Fact]
    public void SamplingRaisesTheNotificationsTheTilesBindTo()
    {
        var vm = new HomeViewModel(NoScan(),
            new StubMetrics(new LiveMetrics(42, 8000, 16000, TimeSpan.FromHours(2))));
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        vm.SampleLiveMetrics();

        seen.Should().Contain([nameof(HomeViewModel.CpuLoadPercent), nameof(HomeViewModel.MemoryLoadPercent),
            nameof(HomeViewModel.MemoryText), nameof(HomeViewModel.UptimeText), nameof(HomeViewModel.HasLiveMetrics)]);
    }

    /// <summary>These tests are about the live tiles, not the scan, so the scanner is never invoked.</summary>
    private static FakeScanner NoScan() => new(new InvalidOperationException("not used by these tests"));

    private sealed class StubMetrics(LiveMetrics sample) : ILiveMetricsReader
    {
        public LiveMetrics Sample() => sample;
    }

    private sealed class ThrowingMetrics : ILiveMetricsReader
    {
        public LiveMetrics Sample() => throw new InvalidOperationException("sensor unavailable");
    }

    private sealed class StubMachineState(MachineState state) : IMachineStateReader
    {
        public MachineState Read() => state;
    }
}
