using System.IO;
using FluentAssertions;
using Tweaker.App.ViewModels;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;
using Tweaker.Domain.Services;
using Tweaker.Infrastructure.Windows.Storage;

namespace Tweaker.App.Tests;

/// <summary>
/// Undo must return the PC to the state before 66mods touched it, even after several profiles were
/// applied in a row. Restoring only the newest snapshot would leave the previously applied profile
/// in place, which is what the owner hit: Mega FPS, then Ultra Potato, then Undo landed on Mega FPS.
/// </summary>
public sealed class GameProfileUndoRewindTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-undo", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Undo_AfterTwoProfiles_RestoresTheOriginalConfigurationNotTheIntermediateOne()
    {
        var (view, config, original) = Build();

        view.SelectedProfile = GamePerformanceProfile.MegaFps;
        await view.ApplySelectedAsync(CancellationToken.None);
        var afterMega = await File.ReadAllTextAsync(config);
        afterMega.Should().NotBe(original, "the first profile must actually change the configuration");

        view.SelectedProfile = GamePerformanceProfile.UltraPotato;
        await view.ApplySelectedAsync(CancellationToken.None);
        (await File.ReadAllTextAsync(config)).Should().NotBe(afterMega);

        await view.UndoAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(config)).Should().Be(original);
    }

    [Fact]
    public async Task Undo_RewindsEverySnapshotAndSaysHowMany()
    {
        var (view, _, _) = Build();

        view.SelectedProfile = GamePerformanceProfile.MegaFps;
        await view.ApplySelectedAsync(CancellationToken.None);
        view.SelectedProfile = GamePerformanceProfile.Competitive;
        await view.ApplySelectedAsync(CancellationToken.None);
        await view.UndoAsync(CancellationToken.None);

        view.Status.Should().Contain("2");
        view.Progress.OutcomeKind.Should().Be("Success");
    }

    [Fact]
    public async Task Undo_AfterASingleProfile_StillRestoresTheOriginal()
    {
        var (view, config, original) = Build();

        view.SelectedProfile = GamePerformanceProfile.UltraPotato;
        await view.ApplySelectedAsync(CancellationToken.None);
        await view.UndoAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(config)).Should().Be(original);
    }

    [Fact]
    public async Task Undo_WithoutAnyApplyReportsNothingToRestore()
    {
        var (view, _, _) = Build();

        await view.UndoAsync(CancellationToken.None);

        view.Status.Should().Contain("No game profile session");
        view.Progress.OutcomeKind.Should().Be("Warning");
    }

    [Fact]
    public async Task Undo_RunTwiceDoesNotRewindAlreadyRestoredWork()
    {
        var (view, config, original) = Build();

        view.SelectedProfile = GamePerformanceProfile.MegaFps;
        await view.ApplySelectedAsync(CancellationToken.None);
        await view.UndoAsync(CancellationToken.None);
        await view.UndoAsync(CancellationToken.None);

        view.Status.Should().Contain("No game profile session");
        (await File.ReadAllTextAsync(config)).Should().Be(original);
    }

    private (GameProfilesViewModel View, string ConfigPath, string Original) Build()
    {
        var config = Path.Combine(root, ".minecraft", "options.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        var original = string.Join('\n',
            "renderDistance:12", "graphicsMode:2", "ao:2", "particles:0",
            "entityShadows:true", "fancyGraphics:true", "maxFps:120", "");
        File.WriteAllText(config, original);

        var snapshot = new SystemSnapshot(new("Windows 10 Pro 22H2", "10.0.19045", 19045),
            new("CPU", "AMD"), [new("GPU", "NVIDIA", "1")], new(16_000_000_000),
            new(false, true, "Balanced"),
            new Dictionary<string, DetectedGame> { ["Minecraft"] = new("Minecraft", true, config) }, []);

        var view = new GameProfilesViewModel(snapshot,
            new TransactionCoordinator(new JsonTransactionStore(Path.Combine(root, "tx"))))
        {
            SelectedGame = "Minecraft"
        };
        return (view, config, original);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
