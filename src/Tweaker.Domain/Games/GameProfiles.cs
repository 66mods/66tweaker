namespace Tweaker.Domain.Games;

public enum GamePerformanceProfile { BalancedFps, Competitive, MegaFps, UltraPotato }

public sealed record SupportedGameProfile(string Game, IReadOnlySet<GamePerformanceProfile> Profiles, bool SupportsRenderScale);

public static class GameProfilePolicy
{
    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ResolutionSizeX", "ResolutionSizeY", "LastUserConfirmedResolutionSizeX", "LastUserConfirmedResolutionSizeY",
        "ScreenWidth", "ScreenHeight", "VideoMode", "fullscreenResolution"
    };

    public static int RenderScale(GamePerformanceProfile profile) => profile switch
    {
        GamePerformanceProfile.BalancedFps => 100,
        GamePerformanceProfile.Competitive => 90,
        GamePerformanceProfile.MegaFps => 70,
        GamePerformanceProfile.UltraPotato => 50,
        _ => 100
    };

    public static bool IsProtectedResolutionKey(string key) => ProtectedKeys.Contains(key);
}

public static class GameProfileCatalog
{
    public static IReadOnlyDictionary<string, SupportedGameProfile> Create()
    {
        var all = Enum.GetValues<GamePerformanceProfile>().ToHashSet();
        return new Dictionary<string, SupportedGameProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fortnite"] = new("Fortnite", all, true),
            ["Valorant"] = new("Valorant", all, false),
            ["GTA V"] = new("GTA V", all, false),
            ["Minecraft"] = new("Minecraft", all, false),
            ["Roblox"] = new("Roblox", all, false)
        };
    }
}
