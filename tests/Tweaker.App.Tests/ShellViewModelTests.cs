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
    public void ReduceMotion_DefaultsToOperatingSystemPreferenceButCanBeOverridden()
    {
        var shell = new ShellViewModel(new Scanner(), [new FakeOperation()], new TransactionCoordinator(new Store()), reduceMotionDefault: true);
        shell.ReduceMotion.Should().BeTrue();
        shell.ReduceMotion = false;
        shell.ReduceMotion.Should().BeFalse();
    }

    private sealed class Scanner : ISystemScanner
    {
        public Task<SystemSnapshot> ScanAsync(CancellationToken token) => Task.FromResult(new SystemSnapshot(
            new("Windows 11", "10", 26100), new("CPU", "AMD"), [new("GPU", "NVIDIA", "1")], new(16_000_000_000),
            new(false, true, "Balanced"), new Dictionary<string, DetectedGame>(), []));
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
