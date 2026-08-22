using FluentAssertions;
using Tweaker.Domain.Games;
using Tweaker.Infrastructure.Windows.Games;

namespace Tweaker.Infrastructure.Windows.Tests;

public sealed class GameConfigOperationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "66mods-game-operation", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ApplyAndRestore_UsesExactOriginalFileContent()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "GameUserSettings.ini");
        const string original = "ResolutionSizeX=1920\nResolutionSizeY=1080\nsg.ShadowQuality=3\n";
        await File.WriteAllTextAsync(path, original);
        var operation = GameConfigOperation.ForUnreal("Fortnite", path, GamePerformanceProfile.UltraPotato);

        var snapshot = await operation.ReadCurrentValueAsync(CancellationToken.None);
        await operation.ApplyAsync("UltraPotato", CancellationToken.None);
        (await operation.VerifyAsync("UltraPotato", CancellationToken.None)).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Contain("sg.ShadowQuality=0").And.Contain("ResolutionSizeX=1920");
        await operation.RestoreAsync(snapshot, CancellationToken.None);

        (await File.ReadAllTextAsync(path)).Should().Be(original);
    }

    [Fact]
    public async Task Apply_WhenFileChangesAfterSnapshot_RefusesToOverwrite()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "options.txt");
        await File.WriteAllTextAsync(path, "renderDistance:16\n");
        var operation = GameConfigOperation.ForMinecraft(path, GamePerformanceProfile.MegaFps);
        await operation.ReadCurrentValueAsync(CancellationToken.None);
        await File.WriteAllTextAsync(path, "renderDistance:20\n");

        Func<Task> action = () => operation.ApplyAsync("MegaFps", CancellationToken.None);

        await action.Should().ThrowAsync<IOException>().WithMessage("*changed after preview*");
        (await File.ReadAllTextAsync(path)).Should().Be("renderDistance:20\n");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

