using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsScanAndOptimizationPreview()
    {
        var operation = new FakeOperation();
        var shell = new ShellViewModel(new Scanner(), [operation], new TransactionCoordinator(new Store()));
        shell.InitializationStatus.Should().Be("Scanning this PC...");

        await shell.InitializeAsync(CancellationToken.None);

        shell.IsReady.Should().BeTrue();
        shell.InitializationStatus.Should().Be("Ready - 0 games detected");
        shell.Optimization.Items.Should().ContainSingle();
        shell.GameCards.Select(x => x.Name).Should().BeEquivalentTo("Fortnite", "Valorant", "GTA V", "Minecraft", "Roblox");
    }

    [Fact]
    public async Task Rescan_FindsAGameInstalledSinceLaunchAndRebuildsThePages()
    {
        var scanner = new Scanner();
        var shell = new ShellViewModel(scanner, [new FakeOperation()], new TransactionCoordinator(new Store()));
        await shell.InitializeAsync(CancellationToken.None);
        var firstOptimization = shell.Optimization;
        shell.GameCards.Single(x => x.Name == "Roblox").IsDetected.Should().BeFalse();
        var raised = new List<string>();
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        scanner.Installed.Add("Roblox");
        await shell.RescanCommand.ExecuteAsync();

        scanner.Scans.Should().Be(2);
        shell.IsReady.Should().BeTrue();
        shell.InitializationStatus.Should().Be("Ready - 1 games detected");
        shell.GameCards.Single(x => x.Name == "Roblox").IsDetected.Should().BeTrue();
        shell.GameProfiles.SelectedGame.Should().Be("Roblox");
        shell.Optimization.Should().NotBeSameAs(firstOptimization);
        raised.Should().Contain(nameof(ShellViewModel.Optimization)).And.Contain(nameof(ShellViewModel.GameProfiles),
            "the pages are bound to these; a silent swap would leave them on the old snapshot");
    }

    [Fact]
    public void ReduceMotion_IsOffForEveryoneUnlessSwitchedOn()
    {
        // Not tied to the Windows animation preference: most tweaked PCs have it off, and the app
        // then opened with a frozen emblem and backdrop. The Settings switch is the way to turn motion off.
        new ShellViewModel(new Scanner(), [new FakeOperation()], new TransactionCoordinator(new Store())).ReduceMotion.Should().BeFalse();
        var shell = new ShellViewModel(new Scanner(), [new FakeOperation()], new TransactionCoordinator(new Store()), reduceMotionDefault: true);
        shell.ReduceMotion.Should().BeTrue();
        shell.ReduceMotion = false;
        shell.ReduceMotion.Should().BeFalse();
    }

    private sealed class Scanner : ISystemScanner
    {
        public List<string> Installed { get; } = [];
        public int Scans { get; private set; }
        public Task<SystemSnapshot> ScanAsync(CancellationToken token)
        {
            Scans++;
            return Task.FromResult(new SystemSnapshot(
                new("Windows 11", "10", 26100), new("CPU", "AMD"), [new("GPU", "NVIDIA", "1")], new(16_000_000_000),
                new(false, true, "Balanced"), Installed.ToDictionary(x => x, x => new DetectedGame(x, true, null)), []));
        }
    }
    private sealed class FakeOperation : ITweakOperation
    {
        public TweakDescriptor Descriptor { get; } = new("test", "Test", TweakCategory.Windows, ImpactLevel.Low, RiskLevel.Safe, false, false);
        public bool IsSupported(SystemSnapshot snapshot) => true;
        public Task<string?> ReadCurrentValueAsync(CancellationToken token) => Task.FromResult<string?>("1");
        public Task ApplyAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task<bool> VerifyAsync(string value, CancellationToken token) => Task.FromResult(true);
        public Task RestoreAsync(string? value, CancellationToken token) => Task.CompletedTask;
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
