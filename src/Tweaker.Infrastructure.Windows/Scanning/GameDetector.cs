using Tweaker.Domain.Models;

namespace Tweaker.Infrastructure.Windows.Scanning;

public sealed class GameDetector(string localAppData, string roamingAppData, string documentsPath)
{
    public IReadOnlyDictionary<string, DetectedGame> Detect()
    {
        var fortnite = Path.Combine(localAppData, "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");
        var valorant = Path.Combine(localAppData, "VALORANT", "Saved", "Config");
        var gta = Path.Combine(documentsPath, "Rockstar Games", "GTA V", "settings.xml");
        var minecraft = Path.Combine(roamingAppData, ".minecraft", "options.txt");
        return new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fortnite"] = FromFile("Fortnite", fortnite),
            ["Valorant"] = FromNewestConfig("Valorant", valorant, "GameUserSettings.ini"),
            ["GTA V"] = FromFile("GTA V", gta),
            ["Minecraft"] = FromFile("Minecraft", minecraft),
            ["Roblox"] = DetectRoblox()
        };
    }

    private DetectedGame DetectRoblox()
    {
        var installation = new RobloxDetector(localAppData).Detect();
        return installation is null
            ? new("Roblox", false, null)
            : new("Roblox", true, installation.StableSettingsPath) { Installation = installation };
    }

    private static DetectedGame FromNewestConfig(string name, string root, string fileName)
    {
        if (!Directory.Exists(root)) return new(name, false, null);
        try
        {
            var path = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            return new(name, path is not null, path);
        }
        catch (IOException) { return new(name, true, null); }
        catch (UnauthorizedAccessException) { return new(name, true, null); }
    }
    private static DetectedGame FromFile(string name, string path) => new(name, File.Exists(path), File.Exists(path) ? path : null);
}
