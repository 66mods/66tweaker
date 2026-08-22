using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class GameProfilesViewModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-game-vm", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ApplySelectedAsync_FortniteUltraPotato_PreservesResolutionAndCanUndo()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "GameUserSettings.ini");
        const string original = "ResolutionSizeX=1920\nResolutionSizeY=1080\nsg.ResolutionQuality=100\nsg.ShadowQuality=3\n";
        await File.WriteAllTextAsync(path, original);
        var vm = new GameProfilesViewModel(Snapshot(path), new TransactionCoordinator(new Store()));
        vm.SelectedGame = "Fortnite";
        vm.SelectedProfile = GamePerformanceProfile.UltraPotato;

        await vm.ApplySelectedAsync(CancellationToken.None);
        (await File.ReadAllTextAsync(path)).Should().Contain("ResolutionSizeX=1920").And.Contain("sg.ResolutionQuality=50");
        vm.Status.Should().Contain("verified");
        await vm.UndoAsync(CancellationToken.None);
        (await File.ReadAllTextAsync(path)).Should().Be(original);
    }

    [Fact]
    public async Task ApplySelectedAsync_Roblox_ReturnsManualSafePlanWithoutFileMutation()
    {
        var vm = new GameProfilesViewModel(Snapshot(null, roblox: true), new TransactionCoordinator(new Store()));
        vm.SelectedGame = "Roblox";
        vm.SelectedProfile = GamePerformanceProfile.UltraPotato;
        await vm.ApplySelectedAsync(CancellationToken.None);
        vm.Status.Should().Contain("Graphics Mode").And.Contain("FastFlags");
    }

    private static SystemSnapshot Snapshot(string? fortnite, bool roblox = false) => new(new("Windows", "10", 26100), new("CPU", "AMD"), [], new(1), new(false, true, "Balanced"),
        new Dictionary<string, DetectedGame> { ["Fortnite"] = new("Fortnite", fortnite is not null, fortnite), ["Roblox"] = new("Roblox", roblox, roblox ? "manual" : null) }, []);
    private sealed class Store : ITransactionStore
    {
        private TransactionRecord? record;
        public Task BeginAsync(TransactionRecord value, CancellationToken token) { record = value; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord value, CancellationToken token) { record = value; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult(record);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) => Task.FromResult<TransactionRecord?>(null);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
