using System.Text;
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;

namespace Tweaker.App.Tests;

public sealed class ShellGameRecoveryIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-shell-recovery", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InterruptedGameTransaction_CanRestoreExactConfigAfterAppRestart()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "GameUserSettings.ini");
        const string original = "ResolutionSizeX=1920\nsg.ShadowQuality=3\n";
        await File.WriteAllTextAsync(path, "ResolutionSizeX=1920\nsg.ShadowQuality=0\n");
        var record = new TransactionRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, TransactionStatus.InProgress,
            [new("game.fortnite.ultrapotato", Convert.ToBase64String(Encoding.UTF8.GetBytes(original)), "UltraPotato",
                TweakStatus.Pending, false, "Snapshot saved", DateTimeOffset.UtcNow)]);
        var store = new Store(record);
        var shell = new ShellViewModel(new Scanner(path), [], new TransactionCoordinator(store),
            reduceMotionDefault: true, transactionStore: store);

        await shell.InitializeAsync(CancellationToken.None);
        await shell.Recovery!.RollbackAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(path)).Should().Be(original);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Scanner(string path) : ISystemScanner
    {
        public Task<SystemSnapshot> ScanAsync(CancellationToken token) => Task.FromResult(new SystemSnapshot(
            new("Windows", "10", 26100), new("CPU", "AMD"), [], new(1), new(false, true, "Balanced"),
            new Dictionary<string, DetectedGame> { ["Fortnite"] = new("Fortnite", true, path) }, []));
    }

    private sealed class Store(TransactionRecord record) : ITransactionStore
    {
        private TransactionRecord value = record;
        public Task BeginAsync(TransactionRecord next, CancellationToken token) { value = next; return Task.CompletedTask; }
        public Task SaveAsync(TransactionRecord next, CancellationToken token) { value = next; return Task.CompletedTask; }
        public Task<TransactionRecord?> LoadAsync(Guid id, CancellationToken token) => Task.FromResult<TransactionRecord?>(value);
        public Task<TransactionRecord?> LoadLatestIncompleteAsync(CancellationToken token) =>
            Task.FromResult<TransactionRecord?>(value.Status == TransactionStatus.InProgress ? value : null);
    }
}
