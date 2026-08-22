using System.Security.Cryptography;
using System.Text;
using Tweaker.Domain.Abstractions;
using Tweaker.Domain.Games;
using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Games;

public sealed class GameConfigOperation : ITweakOperation
{
    private readonly string path;
    private readonly GamePerformanceProfile profile;
    private readonly Func<string, string> transform;
    private string? previewHash;
    private string? expectedHash;

    private GameConfigOperation(string game, string path, GamePerformanceProfile profile, Func<string, string> transform)
    {
        this.path = Path.GetFullPath(path);
        this.profile = profile;
        this.transform = transform;
        Descriptor = new($"game.{game.ToLowerInvariant().Replace(' ', '-')}.{profile.ToString().ToLowerInvariant()}",
            $"{game} · {ProfileName(profile)}", TweakCategory.Games, Impact(profile), RiskLevel.Safe, false, false);
    }

    public TweakDescriptor Descriptor { get; }
    public static GameConfigOperation ForUnreal(string game, string path, GamePerformanceProfile profile) =>
        new(game, path, profile, input => new UnrealIniTransformer().Transform(input, game, profile));
    public static GameConfigOperation ForMinecraft(string path, GamePerformanceProfile profile) =>
        new("Minecraft", path, profile, input => new MinecraftOptionsTransformer().Transform(input, profile));
    public static GameConfigOperation ForGta(string path, GamePerformanceProfile profile) =>
        new("GTA V", path, profile, input => new GtaXmlTransformer().Transform(input, profile));
    /// <summary>
    /// The Roblox client's own graphics settings. Applied alongside the NVIDIA profile rather than instead
    /// of it: the driver cannot reach the quality level, and on the weak hardware these profiles exist for
    /// the quality level is worth more than everything the driver can do.
    /// </summary>
    public static GameConfigOperation ForRoblox(string path, GamePerformanceProfile profile) =>
        new("Roblox", path, profile, input => new RobloxSettingsTransformer().Transform(input, profile));

    public bool IsSupported(SystemSnapshot snapshot) => File.Exists(path);

    public async Task<string?> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        previewHash = Convert.ToHexString(SHA256.HashData(bytes));
        return Convert.ToBase64String(bytes);
    }

    public async Task ApplyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (!string.Equals(requestedValue, profile.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException("Requested game profile does not match the operation");
        var originalBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (previewHash is null || !string.Equals(Convert.ToHexString(SHA256.HashData(originalBytes)), previewHash, StringComparison.Ordinal))
            throw new IOException("The game configuration changed after preview; scan again before applying.");
        var original = Encoding.UTF8.GetString(originalBytes);
        var changed = transform(original);
        var changedBytes = new UTF8Encoding(false).GetBytes(changed);
        expectedHash = Convert.ToHexString(SHA256.HashData(changedBytes));
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Configuration directory is unavailable");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.66mods.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, changedBytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<bool> VerifyAsync(string requestedValue, CancellationToken cancellationToken)
    {
        if (expectedHash is null) return false;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedHash, StringComparison.Ordinal);
    }

    public async Task RestoreAsync(string? originalValue, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(originalValue)) throw new InvalidDataException("Game configuration snapshot is missing");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(originalValue); }
        catch (FormatException error) { throw new InvalidDataException("Game configuration snapshot is corrupt", error); }
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Configuration directory is unavailable");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.66mods.restore.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ProfileName(GamePerformanceProfile value) => value switch
    {
        GamePerformanceProfile.BalancedFps => "Balanced FPS", GamePerformanceProfile.Competitive => "Competitive",
        GamePerformanceProfile.MegaFps => "Mega FPS", _ => "Ultra Potato"
    };
    private static ImpactLevel Impact(GamePerformanceProfile value) => value is GamePerformanceProfile.MegaFps or GamePerformanceProfile.UltraPotato ? ImpactLevel.High : ImpactLevel.Medium;
}
