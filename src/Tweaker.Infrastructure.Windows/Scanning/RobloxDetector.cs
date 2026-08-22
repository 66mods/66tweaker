using Tweaker.Domain.Games;

namespace Tweaker.Infrastructure.Windows.Scanning;

/// <summary>
/// Finds a usable Roblox client across the official launcher and the community launchers that
/// relocate the version folder. Read-only: it inspects paths and never launches or modifies anything.
/// </summary>
public sealed class RobloxDetector(string localAppData)
{
    private const string PlayerExecutable = "RobloxPlayerBeta.exe";

    // Ordered by precedence: the official install wins, then community launchers in name order.
    private static readonly (RobloxLauncherKind Kind, string Folder)[] Candidates =
    [
        (RobloxLauncherKind.Official, "Roblox"),
        (RobloxLauncherKind.Bloxstrap, "Bloxstrap"),
        (RobloxLauncherKind.Fishstrap, "Fishstrap"),
        (RobloxLauncherKind.Froststrap, "Froststrap")
    ];

    public RobloxInstallation? Detect()
    {
        foreach (var (kind, folder) in Candidates)
        {
            var executable = FindNewestPlayer(Path.Combine(localAppData, folder, "Versions"));
            if (executable is null) continue;
            return new RobloxInstallation(kind, executable, StableSettingsPath(),
                FastFlagsPath(kind, folder), $"{folder} version executable");
        }
        return null;
    }

    /// <summary>The stable graphics XML is shared: every launcher runs the official client, which owns this file.</summary>
    public string StableSettingsPath() =>
        Path.Combine(localAppData, "Roblox", "GlobalBasicSettings_13.xml");

    /// <summary>Bloxstrap and Fishstrap keep FastFlags under Modifications; Froststrap keeps them at the root.</summary>
    private string FastFlagsPath(RobloxLauncherKind kind, string folder) => kind switch
    {
        RobloxLauncherKind.Bloxstrap or RobloxLauncherKind.Fishstrap =>
            Path.Combine(localAppData, folder, "Modifications", "ClientSettings", "ClientAppSettings.json"),
        RobloxLauncherKind.Froststrap =>
            Path.Combine(localAppData, folder, "ClientSettings", "ClientAppSettings.json"),
        _ => Path.Combine(localAppData, folder, "ClientSettings", "ClientAppSettings.json")
    };

    private static string? FindNewestPlayer(string versionsRoot)
    {
        if (!Directory.Exists(versionsRoot)) return null;
        try
        {
            return Directory.EnumerateDirectories(versionsRoot)
                .Select(x => Path.Combine(x, PlayerExecutable))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
